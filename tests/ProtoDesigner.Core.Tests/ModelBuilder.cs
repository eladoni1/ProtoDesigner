namespace ProtoDesigner.Core.Tests;

/// <summary>
/// Terse construction of type libraries and messages. Every test starts from a fresh builder, so cases stay
/// independent. Extend this rather than hand-rolling model setup in individual tests.
/// </summary>
internal sealed class ModelBuilder
{
    private readonly LayoutEngine _engine = new();

    public TypeLibrary Types { get; } = new();

    public LayoutOptions Options { get; } = new();

    // ---- types ----------------------------------------------------------------------------------

    public ParameterType Param(string name, PrimitiveKind kind, NumericRange? range = null) =>
        Types.Add(new ParameterType(TypeId.New(), name, kind, range));

    /// <summary>Shorthand for the common primitives, created on demand and reused by name.</summary>
    public ParameterType U8(string name = "u8") => GetOrAdd(name, PrimitiveKind.U8);

    public ParameterType U16(string name = "u16") => GetOrAdd(name, PrimitiveKind.U16);

    public ParameterType U32(string name = "u32") => GetOrAdd(name, PrimitiveKind.U32);

    public ParameterType I32(string name = "i32") => GetOrAdd(name, PrimitiveKind.I32);

    public ParameterType F32(string name = "f32") => GetOrAdd(name, PrimitiveKind.F32);

    public ParameterType Char(string name = "char") => GetOrAdd(name, PrimitiveKind.Char);

    public ParameterType Bool(string name = "bool") => GetOrAdd(name, PrimitiveKind.Bool);

    private ParameterType GetOrAdd(string name, PrimitiveKind kind)
    {
        var existing = Types.All.OfType<ParameterType>().FirstOrDefault(t => t.Name == name && t.Kind == kind);
        return existing ?? Param(name, kind);
    }

    public EnumType Enum(string name, PrimitiveKind underlying, params (string Name, long Value)[] members)
    {
        var type = Types.Add(new EnumType(TypeId.New(), name, underlying));
        foreach (var (memberName, value) in members) type.With(memberName, value);
        return type;
    }

    public EnumType Flags(string name, PrimitiveKind underlying, params (string Name, long Value)[] members)
    {
        var type = Types.Add(new EnumType(TypeId.New(), name, underlying, isFlags: true));
        foreach (var (memberName, value) in members) type.With(memberName, value);
        return type;
    }

    public StructType Struct(string name, params FieldBinding[] fields) =>
        Types.Add(new StructType(TypeId.New(), name)).With(fields);

    public ArrayType Array(string name, TypeDefinition element, ArrayLength length) =>
        Types.Add(new ArrayType(TypeId.New(), name, element.Id, length));

    public ArrayType Array(string name, TypeDefinition element, int count) =>
        Array(name, element, new ArrayLength.Fixed(count));

    // ---- bindings and messages ------------------------------------------------------------------

    public static FieldBinding F(string name, TypeDefinition type, FieldEncoding? encoding = null) =>
        new(name, type.Id, encoding);

    public static Message Msg(string name, params FieldBinding[] fields) =>
        new Message(name).With(fields);

    // ---- computation ----------------------------------------------------------------------------

    public MessageLayout Layout(Message message, EffectiveLayoutOptions? options = null) =>
        _engine.Compute(message, Types, options ?? EffectiveLayoutOptions.Resolve(message.Options, Options));

    public MessageLayout Layout(params FieldBinding[] fields) => Layout(Msg("m", fields));

    public LayoutException LayoutThrows(Message message, EffectiveLayoutOptions? options = null) =>
        Assert.Throws<LayoutException>(() => Layout(message, options));

    public LayoutException LayoutThrows(params FieldBinding[] fields) => LayoutThrows(Msg("m", fields));
}

/// <summary>Assertions phrased in the vocabulary of the layout, so failures read like layout problems.</summary>
internal static class LayoutAssert
{
    public static void At(this MessageLayout layout, string path, int bitOffset, int bitWidth)
    {
        var node = layout[path];
        Assert.Equal((bitOffset, bitWidth), (node.BitOffset, node.BitWidth));
    }

    public static void At(this MessageLayout layout, string path, int region, int bitOffset, int bitWidth)
    {
        var node = layout[path];
        Assert.Equal((region, bitOffset, bitWidth), (node.RegionIndex, node.BitOffset, node.BitWidth));
    }

    public static void Sized(this MessageLayout layout, int bits)
    {
        Assert.True(layout.IsFixedSize, $"Expected a fixed-size layout, got {layout.MinBits}..{layout.MaxBits} bits.");
        Assert.Equal(bits, layout.MinBits);
    }

    public static void Spans(this MessageLayout layout, int minBits, int maxBits)
    {
        Assert.Equal((minBits, maxBits), (layout.MinBits, layout.MaxBits));
    }

    /// <summary>Value paths in wire order, padding excluded. The clearest single assertion for a whole layout.</summary>
    public static void Order(this MessageLayout layout, params string[] paths)
    {
        Assert.Equal(paths, layout.Values().Select(n => n.Path).ToArray());
    }
}
