using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Ir;

/// <summary>
/// Turns a bus + project into a <see cref="ProtocolIr"/>. Runs the layout engine, walks the produced
/// tree, resolves every type reference into a concrete kind, and collects the enums referenced by any
/// field into a top-level list so codegen can emit them once. Fails fast on any unresolved reference â€”
/// validation is expected to have run first.
/// </summary>
public sealed class IrBuilder
{
    private readonly LayoutEngine _engine = new();

    /// <summary>
    /// Builds the IR for a bus, optionally narrowed to a subset of its messages.
    /// </summary>
    /// <param name="project">The project owning the types the bus's messages reference.</param>
    /// <param name="bus">The bus to build. Its layout options set the defaults every message inherits.</param>
    /// <param name="only">
    /// The messages to include, or null for all of them. Filtering happens before anything else is
    /// collected, so the enum and struct tables end up holding only what the chosen messages actually
    /// reach â€” asking for one message does not drag in the types only its neighbours used.
    /// </param>
    public ProtocolIr Build(Project project, Bus bus, IReadOnlySet<MessageId>? only = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bus);

        var selected = only is null
            ? bus.Messages
            : bus.Messages.Where(m => only.Contains(m.Id)).ToList();

        // Collect enums first so every IrField.EnumIndex points into the same list.
        var enumTable = new Dictionary<TypeId, int>();
        var enums = new List<IrEnum>();

        foreach (var message in selected)
            CollectEnums(project, message.Fields, enumTable, enums);

        // A synthetic enum declares no members of its own; the bus supplies them. Filling them here is
        // what makes one shared Header mean the right thing on every bus that uses it â€” and why the
        // member list is never stored: renaming a message or a module changes it, and there is nothing
        // left behind to go stale.
        // The name takes the bus with it for the same reason the members do: the type is project-wide but
        // its contents are per-bus, so one C project including headers from two buses would otherwise have
        // two different enums called MessageId. Renaming the bus therefore renames this too.
        for (var i = 0; i < enums.Count; i++)
            if (SyntheticMembers(bus, enums[i]) is { } filled)
                enums[i] = enums[i] with { Members = filled, Name = bus.Name + enums[i].Name };

        // Then structs, post-order, so a struct always lands after everything it depends on and a
        // generator can emit the list top to bottom without sorting it again.
        var structTable = new Dictionary<TypeId, int>();
        var structs = new List<IrStruct>();

        // Struct sizes are measured under the bus's options rather than any one message's, because a
        // struct belongs to the project and is reported once for the whole bus.
        var typeOptions = EffectiveLayoutOptions.Resolve(bus.Options, project.Options);

        foreach (var message in selected)
            foreach (var field in message.Fields)
                CollectStructs(project, field.TypeId, enumTable, structTable, structs, typeOptions, _engine);

        var messages = new List<IrMessage>();
        foreach (var message in selected)
        {
            var layout = _engine.Compute(project, bus, message);
            messages.Add(BuildMessage(project, message, layout, enumTable, structTable));
        }

        // Modules are numbered from 1 so 0 stays free to mean "not assigned", matching the message-id
        // enum. Declaration order is the order the user sees in the editor.
        var modules = bus.Modules
            .Select((m, i) => new IrModule(m.Name, i + 1))
            .ToList();

        return new ProtocolIr(project.Name, bus.Name, bus.Transport,
            CollectPrimitives(project, selected), enums, structs, messages, modules);
    }

    /// <summary>
    /// The members a synthetic enum takes from the bus, or null when it is an ordinary enum.
    /// </summary>
    private static IReadOnlyList<IrEnumMember>? SyntheticMembers(Bus bus, IrEnum candidate)
    {
        if (candidate.Synthetic == SyntheticEnum.None) return null;

        return SyntheticEnumMembers.For(bus, candidate.Synthetic)
            .Select(m => new IrEnumMember(m.Name, m.Value))
            .ToList();
    }

    // ---- primitive collection ---------------------------------------------------------------

    /// <summary>
    /// Every named primitive the chosen messages reach, in declaration order, deduplicated by id.
    /// </summary>
    /// <remarks>
    /// Reached through struct members and array elements as well as directly, so a <c>u16</c> used only
    /// inside a <c>Header</c> still gets its size emitted â€” that is exactly the case where the caller
    /// cannot see the width from the message alone.
    /// </remarks>
    private static IReadOnlyList<IrPrimitive> CollectPrimitives(Project project, IEnumerable<Message> messages)
    {
        var seen = new HashSet<TypeId>();
        var sink = new List<IrPrimitive>();

        foreach (var message in messages)
            foreach (var field in message.Fields)
                Walk(field.TypeId);

        return sink;

        void Walk(TypeId typeId)
        {
            if (!project.Types.TryGet(typeId, out var type) || type is null) return;
            if (!seen.Add(typeId)) return;

            switch (type)
            {
                case ParameterType p:
                    sink.Add(new IrPrimitive(p.Name, p.Kind, p.WireBits ?? p.Kind.NaturalBits(), p.Range));
                    break;

                case StructType s:
                    foreach (var member in s.Fields) Walk(member.TypeId);
                    break;

                case ArrayType a:
                    Walk(a.ElementTypeId);
                    break;
            }
        }
    }

    // ---- struct collection ------------------------------------------------------------------

    /// <summary>
    /// Registers a struct and everything it reaches, depth first, so dependencies precede dependants.
    /// </summary>
    /// <remarks>
    /// The entry is reserved before the members are built. Recursion is impossible in a valid model â€” the
    /// layout engine rejects it and <c>PD0010</c> reports it â€” so the reservation is only there to stop a
    /// malformed one from recursing forever before those checks run.
    /// </remarks>
    private static void CollectStructs(Project project, TypeId typeId,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable, List<IrStruct> sink,
        EffectiveLayoutOptions options, LayoutEngine engine)
    {
        if (!project.Types.TryGet(typeId, out var type) || type is null) return;

        switch (type)
        {
            case StructType s:
                if (structTable.ContainsKey(s.Id)) return;

                foreach (var member in s.Fields)
                    CollectStructs(project, member.TypeId, enumTable, structTable, sink, options, engine);

                structTable[s.Id] = sink.Count;
                sink.Add(new IrStruct(s.Name, BuildMembers(project, s.Fields, enumTable, structTable),
                    WireBits: MeasureStruct(s, project, options, engine)));
                break;

            case ArrayType a:
                CollectStructs(project, a.ElementTypeId, enumTable, structTable, sink, options, engine);
                break;
        }
    }

    /// <summary>
    /// Measures a struct by laying it out on its own, as the only field of a throwaway message.
    /// </summary>
    /// <remarks>
    /// Reusing the engine rather than summing the members' widths is the point: padding, alignment and
    /// bit packing all affect the total, and re-deriving those rules here would be a second implementation
    /// to keep in step with the first. The probe message is discarded; nothing is added to the project.
    /// </remarks>
    private static int MeasureStruct(StructType type, Project project,
        EffectiveLayoutOptions options, LayoutEngine engine)
    {
        var probe = new Message(MessageId.New(), $"__measure_{type.Name}");
        probe.Fields.Add(new FieldBinding(FieldId.New(), "value", type.Id));

        try
        {
            return engine.Compute(probe, project.Types, options).MaxBits;
        }
        catch (LayoutException)
        {
            // A struct that cannot stand alone â€” one ending in a fill-remaining array, say â€” has no size
            // of its own. Reporting 0 says "not a fixed size" without failing the whole build, and the
            // generators omit the constant rather than emitting a wrong one.
            return 0;
        }
    }

    // ---- host-shape members -----------------------------------------------------------------

    /// <summary>
    /// Describes a list of bindings as host struct members â€” the shape the user declared, not the
    /// flattened wire order.
    /// </summary>
    private static IReadOnlyList<IrMember> BuildMembers(Project project, IReadOnlyList<FieldBinding> fields,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable)
    {
        var members = new List<IrMember>(fields.Count);

        foreach (var binding in fields)
        {
            if (!project.Types.TryGet(binding.TypeId, out var type) || type is null)
                throw new InvalidOperationException(
                    $"Field '{binding.Name}' references unknown type {binding.TypeId}.");

            members.Add(BuildMember(project, binding, type, enumTable, structTable));
        }

        return members;
    }

    private static IrMember BuildMember(Project project, FieldBinding binding, TypeDefinition type,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable)
    {
        switch (type)
        {
            case ParameterType p:
                return new IrMember(binding.Name, IrMemberKind.Scalar, p.Kind,
                    EnumIndex: null, StructIndex: null, ArrayCapacity: null,
                    NeedsCountMember: false, Note: DescribeScalar(binding, p), Range: p.Range,
                    ProtoFieldNumber: binding.ProtoFieldNumber);

            case EnumType e:
                return new IrMember(binding.Name, IrMemberKind.EnumRef, e.UnderlyingKind,
                    EnumIndex: enumTable[e.Id], StructIndex: null, ArrayCapacity: null,
                    NeedsCountMember: false, Note: null,
                    ProtoFieldNumber: binding.ProtoFieldNumber);

            case StructType s:
                return new IrMember(binding.Name, IrMemberKind.StructRef, PrimitiveKind.U8,
                    EnumIndex: null, StructIndex: structTable[s.Id], ArrayCapacity: null,
                    NeedsCountMember: false, Note: null,
                    ProtoFieldNumber: binding.ProtoFieldNumber);

            case ArrayType a:
                return BuildArrayMember(project, binding, a, enumTable, structTable);

            default:
                throw new InvalidOperationException(
                    $"Field '{binding.Name}' has unsupported type kind {type.GetType().Name}.");
        }
    }

    private static IrMember BuildArrayMember(Project project, FieldBinding binding, ArrayType array,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable)
    {
        if (!project.Types.TryGet(array.ElementTypeId, out var element) || element is null)
            throw new InvalidOperationException(
                $"Array '{array.Name}' references unknown element type {array.ElementTypeId}.");

        // A self-describing array carries its own count. A Fixed one does not (the count is a constant)
        // and neither does a CountFromField one (its count already lives in another field, and a second
        // copy could disagree with it and silently desynchronise the encoder).
        var needsCount = array.Length is not (ArrayLength.Fixed or ArrayLength.CountFromField);
        var note = $"{DescribeLength(array.Length)}";
        var min = array.Length.MinimumCount;

        return element switch
        {
            // The range belongs to the element type, so it constrains each element rather than the array.
            ParameterType p => new IrMember(binding.Name, IrMemberKind.Scalar, p.Kind,
                null, null, array.Length.Capacity, needsCount, note, p.Range, binding.ProtoFieldNumber, min),

            EnumType e => new IrMember(binding.Name, IrMemberKind.EnumRef, e.UnderlyingKind,
                enumTable[e.Id], null, array.Length.Capacity, needsCount, note, null, binding.ProtoFieldNumber, min),

            StructType s => new IrMember(binding.Name, IrMemberKind.StructRef, PrimitiveKind.U8,
                null, structTable[s.Id], array.Length.Capacity, needsCount, note, null, binding.ProtoFieldNumber, min),

            _ => throw new InvalidOperationException(
                $"Array '{array.Name}' has unsupported element kind {element.GetType().Name}."),
        };
    }

    /// <summary>
    /// The human-readable length note. Comment text only â€” nothing parses it back.
    /// </summary>
    private static string DescribeLength(ArrayLength length)
    {
        var floor = length.MinimumCount > 0 ? $"at least {length.MinimumCount}, " : "";

        return length switch
        {
            ArrayLength.Fixed f => $"exactly {f.Count} elements",
            ArrayLength.CountFromField c => $"{floor}up to {c.MaxCount}, count from an earlier field",
            ArrayLength.LengthPrefixed l => $"{floor}up to {l.MaxCount}, {l.PrefixBits}-bit length prefix",
            ArrayLength.Terminated t => $"{floor}up to {t.MaxCount}, sentinel-terminated",
            ArrayLength.FillRemaining r => $"{floor}up to {r.MaxCount}, fills the frame",
            _ => "array",
        };
    }

    private static string? DescribeScalar(FieldBinding binding, ParameterType type)
    {
        var bits = binding.Encoding.BitWidth;
        return bits is null ? null : $"{bits} bits";
    }

    // ---- enum collection --------------------------------------------------------------------

    private static void CollectEnums(Project project, IEnumerable<FieldBinding> fields,
        Dictionary<TypeId, int> table, List<IrEnum> sink)
    {
        foreach (var f in fields)
            if (project.Types.TryGet(f.TypeId, out var type) && type is not null)
                CollectEnumsFromType(project, type, table, sink);
    }

    private static void CollectEnumsFromType(Project project, TypeDefinition type,
        Dictionary<TypeId, int> table, List<IrEnum> sink)
    {
        switch (type)
        {
            case EnumType e:
                if (table.ContainsKey(e.Id)) return;
                table[e.Id] = sink.Count;
                sink.Add(new IrEnum(e.Name, e.UnderlyingKind, e.IsFlags,
                    e.Members.Select(m => new IrEnumMember(m.Name, m.Value)).ToArray(),
                    WireBits: e.WireBits ?? e.UnderlyingKind.NaturalBits(),
                    Synthetic: e.Synthetic));
                break;

            case StructType s:
                CollectEnums(project, s.Fields, table, sink);
                break;

            case ArrayType a:
                if (project.Types.TryGet(a.ElementTypeId, out var elem) && elem is not null)
                    CollectEnumsFromType(project, elem, table, sink);
                break;
        }
    }

    // ---- per-message build ------------------------------------------------------------------

    private static IrMessage BuildMessage(Project project, Message message, MessageLayout layout,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable)
    {
        // First pass: flatten every leaf node into a raw IrField, remembering which flattened index
        // each layout FieldId ended up at. Both region-level and array-level count references resolve
        // through this map in the second pass, so nested-in-struct count fields work correctly.
        var raw = new List<IrField>();
        var indexByFieldId = new Dictionary<FieldId, int>();

        foreach (var node in layout.Flatten())
        {
            // Skip: padding, struct openers (their members flow through as separate leaves), length
            // prefixes (framing the array field already describes through IrArrayInfo.PrefixBits â€” emitting
            // it again would both double-count it and hand the generator an untyped field), and array
            // element descriptors (path contains "[]" â€” those describe one element, not a real field).
            if (node.Kind is LayoutNodeKind.Padding or LayoutNodeKind.Struct or LayoutNodeKind.LengthPrefix)
                continue;
            if (node.Path.Contains("[]"))
                continue;

            var field = ResolveField(project, node, enumTable, structTable);
            if (node.FieldId is { } id) indexByFieldId.TryAdd(id, raw.Count);
            raw.Add(field);
        }

        // Second pass: an array field's CountFieldIndex is the same one carried by its variable region
        // (both point at the same original FieldId). Reading from the region keeps the two in sync.
        var fields = raw.Select(f =>
        {
            if (f.Array is not { Kind: IrArrayKind.CountFromField } arr) return f;
            var region = layout.Regions.FirstOrDefault(r => r.Index == f.RegionIndex);
            if (region?.CountFieldId is not { } cid) return f;
            if (!indexByFieldId.TryGetValue(cid, out var idx)) return f;
            return f with { Array = arr with { CountFieldIndex = idx } };
        }).ToArray();

        var regions = layout.Regions
            .Select(r => new IrRegion(
                r.Index,
                r.Kind == LayoutRegionKind.Fixed ? IrRegionKind.Fixed : IrRegionKind.Variable,
                r.MinBits,
                r.MaxBits,
                r.ElementBits,
                r.MaxElements,
                r.CountFieldId is { } cid && indexByFieldId.TryGetValue(cid, out var i) ? i : null,
                r.PrefixBits))
            .ToArray();

        var members = BuildMembers(project, message.Fields, enumTable, structTable);

        return new IrMessage(message.Name, message.WireId, layout.MinBits, layout.MaxBits,
            regions, fields, members);
    }

    // ---- per-node build ---------------------------------------------------------------------

    private static IrField ResolveField(Project project, LayoutNode node,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable)
    {
        var typeId = node.TypeId ?? throw new InvalidOperationException(
            $"Layout node '{node.Path}' has no TypeId; the IR requires every value node to be typed.");
        if (!project.Types.TryGet(typeId, out var type) || type is null)
            throw new InvalidOperationException($"Layout node '{node.Path}' references unknown type {typeId}.");

        return type switch
        {
            ParameterType p => new IrField(
                node.Path, IrFieldKind.Scalar, p.Kind, EnumIndex: null,
                node.RegionIndex, node.BitOffset, node.BitWidth,
                node.Endianness, node.BitOrder, node.Transform, Array: null,
                WireIsSigned: WireIsSigned(p.Kind, p.Range, node.Transform)),

            EnumType e => new IrField(
                node.Path, IrFieldKind.EnumRef, e.UnderlyingKind, EnumIndex: enumTable[e.Id],
                node.RegionIndex, node.BitOffset, node.BitWidth,
                node.Endianness, node.BitOrder, node.Transform, Array: null,
                WireIsSigned: WireIsSigned(e.UnderlyingKind, e.MemberRange, node.Transform)),

            ArrayType a => BuildArrayField(project, node, a, enumTable, structTable),

            _ => throw new InvalidOperationException($"Layout node '{node.Path}' has unsupported type kind {type.GetType().Name}."),
        };
    }

    /// <summary>
    /// Whether the wire codes for a field can go negative, and therefore need a two's-complement field.
    /// </summary>
    /// <remarks>
    /// This is a property of the <em>transform applied to the range</em>, not of the host kind. An
    /// unsigned host can never produce a negative code, so it is always unsigned. A signed host usually
    /// can â€” but not when its transform biases the range non-negative, which is exactly what the editor
    /// derives when a user narrows a -100..100 field onto a byte. Getting this from the host kind instead
    /// meant those codes were written unsigned and read back signed, silently corrupting every value from
    /// 0x80 up.
    ///
    /// With no declared range there is nothing to reason about, so the host kind is the honest fallback.
    /// </remarks>
    private static bool WireIsSigned(PrimitiveKind host, NumericRange? range, ScalarTransform transform)
    {
        if (!host.IsSigned()) return false;
        if (range is not { } r) return true;

        // The lowest code the field can ever hold. Negative means the field must carry a sign.
        var lowest = Math.Min(transform.ToWire(r.Min), transform.ToWire(r.Max));
        return lowest < 0m;
    }

    private static IrField BuildArrayField(Project project, LayoutNode node, ArrayType type,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable)
    {
        var elementNode = node.Children.FirstOrDefault()
            ?? throw new InvalidOperationException($"Array '{node.Path}' has no element node.");
        var elementBits = node.ElementBits > 0 ? node.ElementBits : elementNode.BitWidth;

        var elemType = project.Types.TryGet(type.ElementTypeId, out var t) ? t : null;

        // An array whose element is itself an array has a variable or nested stride the region model
        // cannot express, so it stays refused. PD0036 reports it in the editor; this is the backstop.
        if (elemType is ArrayType)
            throw new InvalidOperationException(
                $"Array '{node.Path}' has elements of type '{elemType.Name}'. Code generation supports "
                + "arrays of primitives, enums and structs; an array of arrays has no single stride.");

        // A struct element has no single primitive kind, so it is described by its members instead.
        // The layout engine has already positioned each of them within one element; all that is needed
        // is to carry that through rather than drop it.
        if (elemType is StructType structElem)
            return BuildCompositeArrayField(project, node, type, elementNode, elementBits,
                                            structElem, enumTable, structTable);

        // Struct elements were handled above, so anything left that is not a primitive or an enum has no
        // element kind to report. Defaulting to one is how an array of something else silently became an
        // array of uint8_t; PD0036 is what should have caught it, and naming the array says where to look.
        var (elemKind, elemEnumIdx) = elemType switch
        {
            ParameterType pt => (pt.Kind, (int?)null),
            EnumType et => (et.UnderlyingKind, enumTable.TryGetValue(et.Id, out var i) ? i : (int?)null),
            null => throw new InvalidOperationException(
                $"Array '{node.Path}' has an element type that is not in the project's type library."),
            _ => throw new InvalidOperationException(
                $"Array '{node.Path}' has {elemType.GetType().Name} elements. Only primitives, enums and "
                + "structs may be array elements."),
        };

        var elemRange = elemType switch
        {
            ParameterType pt => pt.Range,
            EnumType et => et.MemberRange,
            _ => null,
        };

        var (irKind, count, maxCount, prefixBits, sentinel) = ResolveLength(type, node.Path);

        var info = new IrArrayInfo(irKind, count, maxCount, elementBits, elemKind, elemEnumIdx,
            CountFieldIndex: null, prefixBits, sentinel);

        return new IrField(node.Path, IrFieldKind.Array, elemKind, elemEnumIdx,
            node.RegionIndex, node.BitOffset, elementBits,
            node.Endianness, node.BitOrder, node.Transform, info,
            WireIsSigned: WireIsSigned(elemKind, elemRange, node.Transform));
    }

    /// <summary>
    /// The length rule, shared by scalar and composite element arrays.
    /// </summary>
    /// <remarks>
    /// CountFieldIndex is deliberately left null by both callers. The second pass in BuildMessage
    /// resolves it from the layout region's CountFieldId, so that the array field and its region can
    /// never disagree about which field carries the count.
    /// </remarks>
    private static (IrArrayKind Kind, int? Count, int MaxCount, int PrefixBits, IReadOnlyList<byte> Sentinel)
        ResolveLength(ArrayType type, string path)
    {
        IReadOnlyList<byte> none = Array.Empty<byte>();

        return type.Length switch
        {
            ArrayLength.Fixed f => (IrArrayKind.Fixed, f.Count, f.Count, 0, none),
            ArrayLength.CountFromField c => (IrArrayKind.CountFromField, null, c.MaxCount, 0, none),
            ArrayLength.LengthPrefixed l => (IrArrayKind.LengthPrefixed, null, l.MaxCount, l.PrefixBits, none),
            ArrayLength.Terminated s => (IrArrayKind.Terminated, null, s.MaxCount, 0, s.Sentinel.ToArray()),
            ArrayLength.FillRemaining r => (IrArrayKind.FillRemaining, null, r.MaxCount, 0, none),
            _ => throw new InvalidOperationException(
                $"Array '{path}' has unknown length kind {type.Length.GetType().Name}."),
        };
    }

    /// <summary>
    /// An array whose element is a struct: described by its members rather than by one primitive kind.
    /// </summary>
    private static IrField BuildCompositeArrayField(Project project, LayoutNode node, ArrayType type,
        LayoutNode elementNode, int elementBits, StructType structElem,
        Dictionary<TypeId, int> enumTable, Dictionary<TypeId, int> structTable)
    {
        var members = new List<IrElementField>();
        CollectElementFields(project, elementNode, prefix: "", enumTable, members, node.Path);

        if (members.Count == 0)
            throw new InvalidOperationException(
                $"Array '{node.Path}' has struct elements of type '{structElem.Name}' with no value members.");

        var (irKind, count, maxCount, prefixBits, sentinel) = ResolveLength(type, node.Path);

        var info = new IrArrayInfo(irKind, count, maxCount, elementBits,
            // A placeholder: a struct element has no single kind, and PrimitiveKind has no "none".
            // IrArrayInfo.HasCompositeElement is what a consumer checks before reading it.
            ElementPrimitive: PrimitiveKind.U8,
            ElementEnumIndex: null,
            CountFieldIndex: null, prefixBits, sentinel,
            ElementFields: members);

        return new IrField(node.Path, IrFieldKind.Array, PrimitiveKind.U8, EnumIndex: null,
            node.RegionIndex, node.BitOffset, elementBits,
            node.Endianness, node.BitOrder, node.Transform, info,
            WireIsSigned: false);
    }

    /// <summary>
    /// Flattens one array element's value nodes, keeping each offset relative to the element start.
    /// </summary>
    /// <remarks>
    /// Nested structs recurse and contribute a dotted access path, which is what a generator needs to
    /// write <c>msg-&gt;items[i].inner.x</c>. A fixed-size array member stays a single entry carrying
    /// its item count, so the generator emits one inner loop rather than N unrolled writes.
    /// </remarks>
    private static void CollectElementFields(Project project, LayoutNode container, string prefix,
        Dictionary<TypeId, int> enumTable, List<IrElementField> sink, string arrayPath)
    {
        foreach (var child in container.Children)
        {
            if (child.Kind is LayoutNodeKind.Padding or LayoutNodeKind.LengthPrefix) continue;

            var leaf = child.Path.Contains('.')
                ? child.Path[(child.Path.LastIndexOf('.') + 1)..]
                : child.Path;
            var name = prefix.Length == 0 ? leaf : $"{prefix}.{leaf}";

            if (child.Kind == LayoutNodeKind.Struct)
            {
                CollectElementFields(project, child, name, enumTable, sink, arrayPath);
                continue;
            }

            var childType = child.TypeId is { } tid && project.Types.TryGet(tid, out var ct) ? ct : null;

            if (childType is ArrayType inner)
            {
                // Only a fixed count is expressible here: a dynamic array inside an array element would
                // make the element stride vary from one element to the next, and every offset after it
                // would depend on data rather than on the layout.
                if (inner.Length is not ArrayLength.Fixed fixedLen)
                    throw new InvalidOperationException(
                        $"Array '{arrayPath}' has a struct element containing dynamic array '{name}'. "
                        + "Only fixed-size arrays may appear inside an array element.");

                var itemNode = child.Children.FirstOrDefault();
                var itemType = itemNode?.TypeId is { } iid && project.Types.TryGet(iid, out var it) ? it : null;
                if (itemType is StructType or ArrayType)
                    throw new InvalidOperationException(
                        $"Array '{arrayPath}' has a struct element containing '{name}', an array of "
                        + "composites. Only arrays of primitives or enums may appear inside an element.");

                var (itemKind, itemEnum, itemRange) = Describe(itemType, enumTable, name, arrayPath);
                var itemBits = child.ElementBits > 0 ? child.ElementBits : (itemNode?.BitWidth ?? child.BitWidth);

                sink.Add(new IrElementField(name, itemKind, itemEnum, child.BitOffset, itemBits,
                    child.Endianness, child.BitOrder, child.Transform,
                    WireIsSigned(itemKind, itemRange, child.Transform),
                    FixedArrayCount: fixedLen.Count));
                continue;
            }

            var (kind, enumIdx, range) = Describe(childType, enumTable, name, arrayPath);
            sink.Add(new IrElementField(name, kind, enumIdx, child.BitOffset, child.BitWidth,
                child.Endianness, child.BitOrder, child.Transform,
                WireIsSigned(kind, range, child.Transform)));
        }
    }

    /// <summary>
    /// The wire description of one value inside an array element.
    /// </summary>
    /// <remarks>
    /// Anything that is not a primitive or an enum throws rather than defaulting to a kind. A silent
    /// fallback here is what once turned an array of structs into an array of <c>uint8_t</c>: the output
    /// compiled, ran, and put the wrong bytes on the wire. Reaching this means <c>PD0036</c> let a shape
    /// through, so the message names the member and the array it sits in.
    /// </remarks>
    private static (PrimitiveKind Kind, int? EnumIndex, NumericRange? Range) Describe(
        TypeDefinition? type, Dictionary<TypeId, int> enumTable, string member, string arrayPath) => type switch
    {
        ParameterType p => (p.Kind, (int?)null, p.Range),
        EnumType e => (e.UnderlyingKind, enumTable.TryGetValue(e.Id, out var i) ? i : (int?)null, e.MemberRange),
        null => throw new InvalidOperationException(
            $"Array '{arrayPath}' has a struct element whose member '{member}' references a type that is "
            + "not in the project's type library."),
        _ => throw new InvalidOperationException(
            $"Array '{arrayPath}' has a struct element whose member '{member}' is a {type.GetType().Name}. "
            + "Only primitives and enums may appear as values inside an array element."),
    };
}
