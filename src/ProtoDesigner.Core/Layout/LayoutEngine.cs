using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Layout;

/// <summary>
/// Computes the wire layout of a message. Stateless and deterministic: the same model always produces the
/// same layout, and no offset is ever stored on the model. Resizing or reordering a field is a model edit
/// followed by a recompute, which is why neither operation needs to "shift" anything.
/// </summary>
/// <remarks>
/// Offset conventions:
/// <list type="bullet">
/// <item>A node's <see cref="LayoutNode.BitOffset"/> is relative to the start of its region. A message with
/// no dynamic arrays has exactly one region, so its offsets are absolute.</item>
/// <item>Struct fields expand inline and carry region-relative offsets.</item>
/// <item>Nodes beneath an array describe one element and carry element-relative offsets.</item>
/// </list>
/// Phase 0 limitations, each of which throws rather than producing a wrong answer:
/// an array element must be fixed size, and byte padding requires dynamic element strides to be whole bytes.
/// </remarks>
public sealed class LayoutEngine
{
    public MessageLayout Compute(Project project, Bus bus, Message message) =>
        Compute(message, project.Types, project.OptionsFor(bus, message));

    public MessageLayout Compute(Message message, TypeLibrary types, EffectiveLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(types);

        var ctx = new Context(types, options ?? EffectiveLayoutOptions.Default);
        var nodes = new List<LayoutNode>();

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in message.Fields)
        {
            if (!names.Add(field.Name))
                throw new LayoutException($"Message '{message.Name}' has more than one field named '{field.Name}'.", field.Name);
            LayoutBinding(ctx, nodes, field, field.Name);
        }

        if (ctx.Options.PadToByteBoundary)
            Pad(ctx, nodes, BitMath.AlignUp(ctx.Bit, 8), "$end");

        ctx.CloseFixedRegion(force: ctx.Regions.Count == 0);

        return new MessageLayout(message.Id, message.Name, ctx.Options, ctx.Regions, nodes);
    }

    // ---- dispatch -------------------------------------------------------------------------------

    private LayoutNode LayoutBinding(Context ctx, List<LayoutNode> sink, FieldBinding binding, string path)
    {
        var type = Resolve(ctx, binding.TypeId, path);

        return type switch
        {
            ParameterType parameter => LayoutScalar(ctx, sink, binding, path, parameter.Kind, LayoutNodeKind.Parameter),
            EnumType enumeration => LayoutScalar(ctx, sink, binding, path, enumeration.UnderlyingKind, LayoutNodeKind.Enum),
            StructType structure => LayoutStruct(ctx, sink, binding, structure, path),
            ArrayType array => LayoutArray(ctx, sink, binding, array, path),
            _ => throw new LayoutException($"Unsupported type kind '{type.GetType().Name}'.", path),
        };
    }

    // ---- scalars --------------------------------------------------------------------------------

    private static LayoutNode LayoutScalar(
        Context ctx,
        List<LayoutNode> sink,
        FieldBinding binding,
        string path,
        PrimitiveKind primitive,
        LayoutNodeKind kind)
    {
        var encoding = binding.Encoding;
        var natural = primitive.NaturalBits();
        var width = encoding.BitWidth ?? natural;

        if (width is <= 0 or > BitMath.MaxScalarBits)
            throw new LayoutException(
                $"Field '{path}' declares a serialized width of {width} bits; the supported range is 1..{BitMath.MaxScalarBits}.",
                path);

        Place(ctx, sink, encoding, path, width, natural);

        var node = new LayoutNode
        {
            Path = path,
            Kind = kind,
            TypeId = binding.TypeId,
            FieldId = binding.Id,
            RegionIndex = ctx.RegionIndex,
            BitOffset = ctx.Bit,
            BitWidth = width,
            Endianness = encoding.Endianness ?? ctx.Options.Endianness,
            BitOrder = encoding.BitOrder ?? ctx.Options.BitOrder,
            Transform = encoding.Transform ?? ScalarTransform.Identity,
        };

        ctx.Bit += width;
        ctx.SeenFields.Add(binding.Id);
        sink.Add(node);
        return node;
    }

    // ---- structs --------------------------------------------------------------------------------

    private LayoutNode LayoutStruct(Context ctx, List<LayoutNode> sink, FieldBinding binding, StructType type, string path)
    {
        if (binding.Encoding.BitWidth is not null)
            throw new LayoutException($"Field '{path}' is a struct; a serialized width applies to its members, not the struct.", path);

        Place(ctx, sink, binding.Encoding, path, width: 0, storageUnitBits: null);

        var startRegion = ctx.RegionIndex;
        var startBit = ctx.Bit;
        var children = new List<LayoutNode>();

        ctx.EnterType(type, path);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in type.Fields)
        {
            if (!names.Add(field.Name))
                throw new LayoutException($"Struct '{type.Name}' has more than one field named '{field.Name}'.", path);
            LayoutBinding(ctx, children, field, $"{path}.{field.Name}");
        }
        ctx.ExitType(type);

        // A struct that contains a dynamic array spans regions; its extent is described by those regions.
        var width = ctx.RegionIndex == startRegion ? ctx.Bit - startBit : 0;

        var node = new LayoutNode
        {
            Path = path,
            Kind = LayoutNodeKind.Struct,
            TypeId = binding.TypeId,
            FieldId = binding.Id,
            RegionIndex = startRegion,
            BitOffset = startBit,
            BitWidth = width,
            Endianness = binding.Encoding.Endianness ?? ctx.Options.Endianness,
            BitOrder = binding.Encoding.BitOrder ?? ctx.Options.BitOrder,
            Children = children,
        };

        ctx.SeenFields.Add(binding.Id);
        sink.Add(node);
        return node;
    }

    // ---- arrays ---------------------------------------------------------------------------------

    private LayoutNode LayoutArray(Context ctx, List<LayoutNode> sink, FieldBinding binding, ArrayType type, string path)
    {
        ctx.EnterType(type, path);
        var (element, elementBits) = MeasureElement(ctx, binding, type, path);
        ctx.ExitType(type);

        if (elementBits <= 0)
            throw new LayoutException($"Array '{path}' has a zero-width element type.", path);

        return type.Length switch
        {
            ArrayLength.Fixed fixedLength => LayoutStaticArray(ctx, sink, binding, path, element, elementBits, fixedLength),
            _ => LayoutDynamicArray(ctx, sink, binding, type, path, element, elementBits),
        };
    }

    private static LayoutNode LayoutStaticArray(
        Context ctx,
        List<LayoutNode> sink,
        FieldBinding binding,
        string path,
        LayoutNode element,
        int elementBits,
        ArrayLength.Fixed length)
    {
        if (length.Count <= 0)
            throw new LayoutException($"Array '{path}' declares {length.Count} elements; a static array needs at least one.", path);

        Place(ctx, sink, binding.Encoding, path, width: 0, storageUnitBits: null);

        var node = new LayoutNode
        {
            Path = path,
            Kind = LayoutNodeKind.Array,
            TypeId = binding.TypeId,
            FieldId = binding.Id,
            RegionIndex = ctx.RegionIndex,
            BitOffset = ctx.Bit,
            BitWidth = elementBits * length.Count,
            ElementBits = elementBits,
            ElementCount = length.Count,
            Endianness = binding.Encoding.Endianness ?? ctx.Options.Endianness,
            BitOrder = binding.Encoding.BitOrder ?? ctx.Options.BitOrder,
            Children = new[] { element },
        };

        ctx.Bit += node.BitWidth;
        ctx.SeenFields.Add(binding.Id);
        sink.Add(node);
        return node;
    }

    private static LayoutNode LayoutDynamicArray(
        Context ctx,
        List<LayoutNode> sink,
        FieldBinding binding,
        ArrayType type,
        string path,
        LayoutNode element,
        int elementBits)
    {
        var length = type.Length;
        if (length.Capacity <= 0)
            throw new LayoutException($"Array '{path}' declares a capacity of {length.Capacity}; it must be at least one.", path);

        var prefixBits = 0;
        FieldId? countFieldId = null;
        var sentinelBits = 0;

        switch (length)
        {
            case ArrayLength.CountFromField countFromField:
                if (!ctx.SeenFields.Contains(countFromField.CountFieldId))
                    throw new LayoutException(
                        $"Array '{path}' takes its element count from a field that has not been laid out yet. " +
                        "The count field must appear earlier in the message.",
                        path);
                countFieldId = countFromField.CountFieldId;
                break;

            case ArrayLength.LengthPrefixed lengthPrefixed:
                prefixBits = lengthPrefixed.PrefixBits;
                if (prefixBits is <= 0 or > BitMath.MaxScalarBits)
                    throw new LayoutException(
                        $"Array '{path}' declares a {prefixBits}-bit length prefix; the supported range is 1..{BitMath.MaxScalarBits}.",
                        path);
                if (BitMath.MaxUnsigned(prefixBits) < (ulong)length.Capacity)
                    throw new LayoutException(
                        $"Array '{path}' has a {prefixBits}-bit length prefix, which cannot express its capacity of {length.Capacity}.",
                        path);
                EmitLengthPrefix(ctx, sink, path, prefixBits);
                break;

            case ArrayLength.Terminated terminated:
                if (terminated.Sentinel.Count == 0)
                    throw new LayoutException($"Array '{path}' is sentinel-terminated but declares an empty sentinel.", path);
                sentinelBits = terminated.Sentinel.Count * 8;
                break;
        }

        if (ctx.Options.PadToByteBoundary)
        {
            if (elementBits % 8 != 0)
                throw new LayoutException(
                    $"Array '{path}' has a {elementBits}-bit element stride, so offsets after it would not be byte-addressable. " +
                    "Use a whole-byte stride, or clear PadToByteBoundary for a pure bit-stream layout.",
                    path);
            Pad(ctx, sink, BitMath.AlignUp(ctx.Bit, 8), path);
        }

        ctx.CloseFixedRegion(force: false);

        var maxBits = (elementBits * length.Capacity) + sentinelBits;
        var regionIndex = ctx.AddVariableRegion(new LayoutRegion
        {
            Index = ctx.RegionIndex,
            Kind = LayoutRegionKind.Variable,
            MinBits = sentinelBits,
            MaxBits = maxBits,
            ElementBits = elementBits,
            MaxElements = length.Capacity,
            CountFieldId = countFieldId,
            PrefixBits = prefixBits,
        });

        var node = new LayoutNode
        {
            Path = path,
            Kind = LayoutNodeKind.Array,
            TypeId = binding.TypeId,
            FieldId = binding.Id,
            RegionIndex = regionIndex,
            BitOffset = 0,
            BitWidth = elementBits,
            ElementBits = elementBits,
            ElementCount = null,
            Endianness = binding.Encoding.Endianness ?? ctx.Options.Endianness,
            BitOrder = binding.Encoding.BitOrder ?? ctx.Options.BitOrder,
            Children = new[] { element },
        };

        ctx.SeenFields.Add(binding.Id);
        sink.Add(node);
        return node;
    }

    private static void EmitLengthPrefix(Context ctx, List<LayoutNode> sink, string path, int prefixBits)
    {
        Pad(ctx, sink, BitMath.AlignUp(ctx.Bit, ctx.Options.DefaultAlignmentBits), $"{path}.__length");

        sink.Add(new LayoutNode
        {
            Path = $"{path}.__length",
            Kind = LayoutNodeKind.Parameter,
            RegionIndex = ctx.RegionIndex,
            BitOffset = ctx.Bit,
            BitWidth = prefixBits,
            Endianness = ctx.Options.Endianness,
            BitOrder = ctx.Options.BitOrder,
        });

        ctx.Bit += prefixBits;
    }

    /// <summary>
    /// Lays out a single element in isolation so its offsets come out element-relative, and reports its stride.
    /// A dynamic array nested inside an element would open a region here, which is how that case is rejected.
    /// </summary>
    private (LayoutNode Element, int Bits) MeasureElement(Context ctx, FieldBinding binding, ArrayType type, string path)
    {
        // AlignmentBits is deliberately not propagated: it positions the array, not each element.
        var elementEncoding = new FieldEncoding
        {
            BitWidth = binding.Encoding.BitWidth,
            Endianness = binding.Encoding.Endianness,
            BitOrder = binding.Encoding.BitOrder,
            AllowBitPacking = binding.Encoding.AllowBitPacking,
            Transform = binding.Encoding.Transform,
        };

        var sub = ctx.ForElement();
        var sink = new List<LayoutNode>();
        var elementBinding = new FieldBinding(binding.Id, "element", type.ElementTypeId, elementEncoding);

        LayoutBinding(sub, sink, elementBinding, $"{path}[]");

        if (sub.Regions.Count > 0)
            throw new LayoutException(
                $"Array '{path}' has a variable-length element type. An array element must be fixed size.",
                path);

        if (sink.Count != 1)
            throw new LayoutException($"Array '{path}' produced {sink.Count} element nodes; expected exactly one.", path);

        return (sink[0], sub.Bit);
    }

    // ---- placement ------------------------------------------------------------------------------

    /// <summary>
    /// Advances the cursor to where the next value may start. Explicit alignment wins; otherwise a field that
    /// is not bit-packed takes the message default, and a packed field takes nothing in Contiguous mode or a
    /// storage-unit boundary in StorageUnit mode.
    /// </summary>
    private static void Place(Context ctx, List<LayoutNode> sink, FieldEncoding encoding, string path, int width, int? storageUnitBits)
    {
        if (encoding.AlignmentBits is { } explicitAlignment)
        {
            if (explicitAlignment <= 0)
                throw new LayoutException($"Field '{path}' declares an alignment of {explicitAlignment} bits; it must be positive.", path);
            Pad(ctx, sink, BitMath.AlignUp(ctx.Bit, explicitAlignment), path);
            return;
        }

        if (!encoding.AllowBitPacking)
        {
            Pad(ctx, sink, BitMath.AlignUp(ctx.Bit, ctx.Options.DefaultAlignmentBits), path);
            return;
        }

        if (ctx.Options.PackingMode == BitPackingMode.StorageUnit && storageUnitBits is { } unit && unit > 0)
        {
            if ((ctx.Bit % unit) + width > unit)
                Pad(ctx, sink, BitMath.AlignUp(ctx.Bit, unit), path);
        }
    }

    private static void Pad(Context ctx, List<LayoutNode> sink, int targetBit, string beforePath)
    {
        if (targetBit <= ctx.Bit) return;

        sink.Add(new LayoutNode
        {
            Path = $"__pad:{beforePath}",
            Kind = LayoutNodeKind.Padding,
            RegionIndex = ctx.RegionIndex,
            BitOffset = ctx.Bit,
            BitWidth = targetBit - ctx.Bit,
            Endianness = ctx.Options.Endianness,
            BitOrder = ctx.Options.BitOrder,
        });

        ctx.Bit = targetBit;
    }

    private static TypeDefinition Resolve(Context ctx, TypeId id, string path) =>
        ctx.Types.TryGet(id, out var type) && type is not null
            ? type
            : throw new LayoutException($"Field '{path}' references type {id}, which is not in the type library.", path);

    // ---- cursor ---------------------------------------------------------------------------------

    private sealed class Context
    {
        public Context(TypeLibrary types, EffectiveLayoutOptions options, HashSet<TypeId>? visiting = null)
        {
            Types = types;
            Options = options;
            Visiting = visiting ?? new HashSet<TypeId>();
        }

        public TypeLibrary Types { get; }

        public EffectiveLayoutOptions Options { get; }

        public List<LayoutRegion> Regions { get; } = new();

        public HashSet<FieldId> SeenFields { get; } = new();

        private HashSet<TypeId> Visiting { get; }

        public int RegionIndex { get; private set; }

        /// <summary>Cursor position within the current region, in bits.</summary>
        public int Bit { get; set; }

        /// <summary>A nested cursor for measuring one array element. Shares the visiting set so cycles are still caught.</summary>
        public Context ForElement() =>
            new(Types, Options with { PadToByteBoundary = false }, Visiting);

        public void CloseFixedRegion(bool force)
        {
            if (Bit == 0 && !force) return;

            Regions.Add(new LayoutRegion
            {
                Index = RegionIndex,
                Kind = LayoutRegionKind.Fixed,
                MinBits = Bit,
                MaxBits = Bit,
            });

            RegionIndex++;
            Bit = 0;
        }

        public int AddVariableRegion(LayoutRegion region)
        {
            Regions.Add(region);
            var index = RegionIndex;
            RegionIndex++;
            Bit = 0;
            return index;
        }

        public void EnterType(TypeDefinition type, string path)
        {
            if (!Visiting.Add(type.Id))
                throw new LayoutException(
                    $"Type '{type.Name}' contains itself, directly or through another type. A fixed layout cannot be recursive.",
                    path);
        }

        public void ExitType(TypeDefinition type) => Visiting.Remove(type.Id);
    }
}
