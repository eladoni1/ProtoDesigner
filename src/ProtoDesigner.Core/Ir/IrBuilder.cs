using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Ir;

/// <summary>
/// Turns a bus + project into a <see cref="ProtocolIr"/>. Runs the layout engine, walks the produced
/// tree, resolves every type reference into a concrete kind, and collects the enums referenced by any
/// field into a top-level list so codegen can emit them once. Fails fast on any unresolved reference —
/// validation is expected to have run first.
/// </summary>
public sealed class IrBuilder
{
    private readonly LayoutEngine _engine = new();

    public ProtocolIr Build(Project project, Bus bus)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(bus);

        // Collect enums first so every IrField.EnumIndex points into the same list.
        var enumTable = new Dictionary<TypeId, int>();
        var enums = new List<IrEnum>();

        foreach (var message in bus.Messages)
            CollectEnums(project, message.Fields, enumTable, enums);

        var messages = new List<IrMessage>();
        foreach (var message in bus.Messages)
        {
            var layout = _engine.Compute(project, bus, message);
            messages.Add(BuildMessage(project, message, layout, enumTable));
        }

        return new ProtocolIr(project.Name, bus.Name, bus.Transport, enums, messages);
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
                    e.Members.Select(m => new IrEnumMember(m.Name, m.Value)).ToArray()));
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
        Dictionary<TypeId, int> enumTable)
    {
        // First pass: flatten every leaf node into a raw IrField, remembering which flattened index
        // each layout FieldId ended up at. Both region-level and array-level count references resolve
        // through this map in the second pass, so nested-in-struct count fields work correctly.
        var raw = new List<IrField>();
        var indexByFieldId = new Dictionary<FieldId, int>();

        foreach (var node in layout.Flatten())
        {
            // Skip: padding, struct openers (their members flow through as separate leaves), and array
            // element descriptors (path contains "[]" — those describe one element, not a real field).
            if (node.Kind is LayoutNodeKind.Padding or LayoutNodeKind.Struct)
                continue;
            if (node.Path.Contains("[]"))
                continue;

            var field = ResolveField(project, node, enumTable);
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

        return new IrMessage(message.Name, message.WireId, layout.MinBits, layout.MaxBits, regions, fields);
    }

    // ---- per-node build ---------------------------------------------------------------------

    private static IrField ResolveField(Project project, LayoutNode node,
        Dictionary<TypeId, int> enumTable)
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
                node.Endianness, node.BitOrder, node.Transform, Array: null),

            EnumType e => new IrField(
                node.Path, IrFieldKind.EnumRef, e.UnderlyingKind, EnumIndex: enumTable[e.Id],
                node.RegionIndex, node.BitOffset, node.BitWidth,
                node.Endianness, node.BitOrder, node.Transform, Array: null),

            ArrayType a => BuildArrayField(project, node, a, enumTable),

            _ => throw new InvalidOperationException($"Layout node '{node.Path}' has unsupported type kind {type.GetType().Name}."),
        };
    }

    private static IrField BuildArrayField(Project project, LayoutNode node, ArrayType type,
        Dictionary<TypeId, int> enumTable)
    {
        var elementNode = node.Children.FirstOrDefault()
            ?? throw new InvalidOperationException($"Array '{node.Path}' has no element node.");
        var elementBits = node.ElementBits > 0 ? node.ElementBits : elementNode.BitWidth;

        var elemType = project.Types.TryGet(type.ElementTypeId, out var t) ? t : null;
        var (elemKind, elemEnumIdx) = elemType switch
        {
            ParameterType pt => (pt.Kind, (int?)null),
            EnumType et => (et.UnderlyingKind, enumTable.TryGetValue(et.Id, out var i) ? i : (int?)null),
            _ => (PrimitiveKind.U8, (int?)null),
        };

        // CountFieldIndex uses the original binding's FieldId here; the second pass in BuildMessage
        // maps that id to the flattened index. We stash the id in ElementEnumIndex? No — instead we
        // resolve it at second-pass time via the layout region's CountFieldId. To keep BuildArrayField
        // self-contained we leave CountFieldIndex null here.
        var (irKind, count, maxCount, prefixBits, sentinel) = type.Length switch
        {
            ArrayLength.Fixed f => (IrArrayKind.Fixed, (int?)f.Count, f.Count, 0, (IReadOnlyList<byte>)Array.Empty<byte>()),
            ArrayLength.CountFromField c => (IrArrayKind.CountFromField, (int?)null, c.MaxCount, 0, (IReadOnlyList<byte>)Array.Empty<byte>()),
            ArrayLength.LengthPrefixed l => (IrArrayKind.LengthPrefixed, (int?)null, l.MaxCount, l.PrefixBits, (IReadOnlyList<byte>)Array.Empty<byte>()),
            ArrayLength.Terminated s => (IrArrayKind.Terminated, (int?)null, s.MaxCount, 0, (IReadOnlyList<byte>)s.Sentinel.ToArray()),
            ArrayLength.FillRemaining r => (IrArrayKind.FillRemaining, (int?)null, r.MaxCount, 0, (IReadOnlyList<byte>)Array.Empty<byte>()),
            _ => throw new InvalidOperationException($"Array '{node.Path}' has unknown length kind {type.Length.GetType().Name}."),
        };

        var info = new IrArrayInfo(irKind, count, maxCount, elementBits, elemKind, elemEnumIdx,
            CountFieldIndex: null, prefixBits, sentinel);

        return new IrField(node.Path, IrFieldKind.Array, elemKind, elemEnumIdx,
            node.RegionIndex, node.BitOffset, elementBits,
            node.Endianness, node.BitOrder, node.Transform, info);
    }
}
