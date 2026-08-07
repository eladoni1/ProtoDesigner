using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Layout;

public enum LayoutNodeKind
{
    Parameter,
    Enum,
    Struct,
    Array,
    Padding,
}

public enum LayoutRegionKind
{
    /// <summary>Every offset inside is a compile-time constant.</summary>
    Fixed,

    /// <summary>Holds exactly one dynamic array. Its length is only known at parse time.</summary>
    Variable,
}

/// <summary>
/// One entry in the computed layout tree. Offsets are relative to the start of the node's region, and
/// relative to the start of one element for nodes beneath an array.
/// </summary>
public sealed class LayoutNode
{
    public required string Path { get; init; }

    public required LayoutNodeKind Kind { get; init; }

    public TypeId? TypeId { get; init; }

    public FieldId? FieldId { get; init; }

    public int RegionIndex { get; init; }

    /// <summary>Offset from the start of the enclosing region, or of the enclosing array element.</summary>
    public int BitOffset { get; init; }

    /// <summary>Total width of this node. For a static array this is stride times count; for a dynamic array, the stride.</summary>
    public int BitWidth { get; init; }

    /// <summary>Stride of one element. Zero for anything that is not an array.</summary>
    public int ElementBits { get; init; }

    /// <summary>Element count for a static array. Null for a dynamic one.</summary>
    public int? ElementCount { get; init; }

    public Endianness Endianness { get; init; }

    public BitOrder BitOrder { get; init; }

    public ScalarTransform Transform { get; init; } = ScalarTransform.Identity;

    public IReadOnlyList<LayoutNode> Children { get; init; } = Array.Empty<LayoutNode>();

    /// <summary>True for nodes that carry an actual value, as opposed to grouping or padding.</summary>
    public bool IsScalar => Kind is LayoutNodeKind.Parameter or LayoutNodeKind.Enum;

    public override string ToString() =>
        $"{Path} [{Kind}] r{RegionIndex}+{BitOffset} w{BitWidth}";
}

/// <summary>
/// A span of the message whose internal offsets share a single origin. A message with no dynamic arrays
/// has exactly one Fixed region, so region offsets are absolute offsets.
/// </summary>
public sealed class LayoutRegion
{
    public required int Index { get; init; }

    public required LayoutRegionKind Kind { get; init; }

    public required int MinBits { get; init; }

    public required int MaxBits { get; init; }

    /// <summary>Variable regions only: stride of one element.</summary>
    public int ElementBits { get; init; }

    /// <summary>Variable regions only: declared capacity.</summary>
    public int MaxElements { get; init; }

    /// <summary>Variable regions only: fewest elements a valid message may carry. Zero unless declared.</summary>
    /// <remarks>
    /// This is what makes <see cref="MinBits"/> the real floor rather than the floor of an empty array,
    /// so a frame-budget check measures the message a caller actually promised to send.
    /// </remarks>
    public int MinElements { get; init; }

    /// <summary>Variable regions only: the field carrying the element count, when the count comes from a field.</summary>
    public FieldId? CountFieldId { get; init; }

    /// <summary>Variable regions only: width of an inline length prefix, which lives in the preceding fixed region.</summary>
    public int PrefixBits { get; init; }

    public bool IsFixedSize => MinBits == MaxBits;

    public override string ToString() =>
        $"region {Index} [{Kind}] {MinBits}..{MaxBits} bits";
}

/// <summary>
/// The computed wire layout of a message. Derived data — never persisted, always recomputed from the model,
/// which is what makes resize and reorder free.
/// </summary>
public sealed class MessageLayout
{
    public MessageLayout(
        MessageId messageId,
        string messageName,
        EffectiveLayoutOptions options,
        IReadOnlyList<LayoutRegion> regions,
        IReadOnlyList<LayoutNode> nodes)
    {
        MessageId = messageId;
        MessageName = messageName;
        Options = options;
        Regions = regions;
        Nodes = nodes;
        MinBits = regions.Sum(r => r.MinBits);
        MaxBits = regions.Sum(r => r.MaxBits);
    }

    public MessageId MessageId { get; }

    public string MessageName { get; }

    public EffectiveLayoutOptions Options { get; }

    public IReadOnlyList<LayoutRegion> Regions { get; }

    /// <summary>Top-level nodes in wire order. Structs and arrays carry their children.</summary>
    public IReadOnlyList<LayoutNode> Nodes { get; }

    public int MinBits { get; }

    public int MaxBits { get; }

    public bool IsFixedSize => MinBits == MaxBits;

    public int MinBytes => (MinBits + 7) / 8;

    public int MaxBytes => (MaxBits + 7) / 8;

    public bool HasVariableRegions => Regions.Any(r => r.Kind == LayoutRegionKind.Variable);

    /// <summary>Every node in wire order, parents before children.</summary>
    public IEnumerable<LayoutNode> Flatten()
    {
        foreach (var node in Nodes)
        {
            foreach (var descendant in Walk(node)) yield return descendant;
        }

        static IEnumerable<LayoutNode> Walk(LayoutNode node)
        {
            yield return node;
            foreach (var child in node.Children)
            {
                foreach (var descendant in Walk(child)) yield return descendant;
            }
        }
    }

    /// <summary>Every node that carries a value, padding excluded.</summary>
    public IEnumerable<LayoutNode> Values() => Flatten().Where(n => n.Kind != LayoutNodeKind.Padding);

    public IEnumerable<LayoutNode> Padding() => Flatten().Where(n => n.Kind == LayoutNodeKind.Padding);

    public LayoutNode this[string path] => TryGet(path, out var node)
        ? node!
        : throw new KeyNotFoundException(
            $"No layout node at path '{path}' in '{MessageName}'. Known: {string.Join(", ", Values().Select(n => n.Path))}");

    public bool TryGet(string path, out LayoutNode? node)
    {
        node = Flatten().FirstOrDefault(n => n.Path == path);
        return node is not null;
    }

    public override string ToString() =>
        IsFixedSize
            ? $"'{MessageName}' {MinBits} bits ({MinBytes} bytes), fixed"
            : $"'{MessageName}' {MinBits}..{MaxBits} bits, {Regions.Count} regions";
}

/// <summary>
/// Thrown when a message cannot be laid out at all. Phase 1's validator reports these conditions as
/// diagnostics before the engine is ever reached; the exception is the backstop, not the user-facing path.
/// </summary>
public sealed class LayoutException : Exception
{
    public LayoutException(string message, string? path = null) : base(message)
    {
        Path = path;
    }

    /// <summary>Dotted path of the field that could not be laid out, when known.</summary>
    public string? Path { get; }
}
