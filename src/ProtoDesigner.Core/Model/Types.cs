namespace ProtoDesigner.Core.Model;

/// <summary>
/// Base of the closed type hierarchy. A type describes what a value *means*; how it is serialized is
/// carried separately by <see cref="FieldEncoding"/> on each binding, so the same type can appear on
/// the wire at different widths in different messages.
/// </summary>
public abstract class TypeDefinition
{
    protected TypeDefinition(TypeId id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Type name must not be empty.", nameof(name));
        Id = id;
        Name = name;
    }

    public TypeId Id { get; }

    /// <summary>Display name. Mutable and never used as identity — references are always by <see cref="Id"/>.</summary>
    public string Name { get; set; }

    public string? Description { get; set; }

    public abstract TResult Accept<TResult>(ITypeVisitor<TResult> visitor);

    public override string ToString() => $"{GetType().Name} '{Name}'";
}

/// <summary>Dispatch over the closed type hierarchy. Adding a kind becomes a compile error here rather than a runtime bug.</summary>
public interface ITypeVisitor<out TResult>
{
    TResult VisitParameter(ParameterType type);
    TResult VisitEnum(EnumType type);
    TResult VisitStruct(StructType type);
    TResult VisitArray(ArrayType type);
}

/// <summary>How a type is represented on the wire, as chosen once on the type itself.</summary>
public enum WireForm
{
    /// <summary>Unsigned integer of <see cref="IWireSized.WireBits"/> bits. Any width from 1 bit up.</summary>
    Unsigned,

    /// <summary>
    /// Two's-complement signed integer. Only the conventional widths are offered, because a signed field
    /// of an odd bit count has no portable representation in a generated struct.
    /// </summary>
    Signed,

    /// <summary>IEEE-754. Only 32 or 64 bits are meaningful.</summary>
    Float,
}

/// <summary>
/// Types that carry a default wire representation.
/// </summary>
/// <remarks>
/// This is a deliberate exception to "encoding lives on the binding". Every occurrence of a given type
/// is expected to serialise identically, so the choice is made once on the type and propagated to every
/// binding that references it. The per-binding <see cref="FieldEncoding"/> remains the single source the
/// layout engine reads, which keeps the engine and its tests untouched.
/// </remarks>
public interface IWireSized
{
    /// <summary>Serialised width in bits. Null means "use the natural width of the host kind".</summary>
    int? WireBits { get; set; }

    WireForm WireForm { get; set; }

    /// <summary>
    /// Value that wire code 0 represents. Null means "derive it" — the range minimum, which biases the
    /// codes non-negative and buys the tightest possible width.
    /// </summary>
    decimal? WireOffset { get; set; }

    /// <summary>
    /// Value represented by one wire step. Null means "derive it" — the finest resolution the declared
    /// width can carry.
    /// </summary>
    /// <remarks>
    /// Deriving is a good default but a poor constraint: real protocols usually want a round constant
    /// (0.1, 0.01, a power of two) so the wire value is readable in a spec and on a scope. Setting this
    /// explicitly is how you get that.
    /// </remarks>
    decimal? WireScale { get; set; }
}

/// <summary>A primitive value, optionally constrained to a range. Min == Max denotes a constant.</summary>
public sealed class ParameterType : TypeDefinition, IWireSized
{
    public ParameterType(TypeId id, string name, PrimitiveKind kind, NumericRange? range = null)
        : base(id, name)
    {
        Kind = kind;
        Range = range;
        WireForm = kind switch
        {
            PrimitiveKind.F32 or PrimitiveKind.F64 => WireForm.Float,
            _ when kind.IsSigned() => WireForm.Signed,
            _ => WireForm.Unsigned,
        };
    }

    public PrimitiveKind Kind { get; set; }

    public NumericRange? Range { get; set; }

    public int? WireBits { get; set; }

    public WireForm WireForm { get; set; }

    public decimal? WireOffset { get; set; }

    public decimal? WireScale { get; set; }

    public bool IsConstant => Range is { IsConstant: true };

    public override TResult Accept<TResult>(ITypeVisitor<TResult> visitor) => visitor.VisitParameter(this);
}

public sealed record EnumMember(string Name, long Value);

/// <summary>
/// A restricted value with named options. Members may start at any value and need not be contiguous.
/// When <see cref="IsFlags"/> is set, valid values are bitwise unions of the declared members.
/// </summary>
public sealed class EnumType : TypeDefinition, IWireSized
{
    public EnumType(TypeId id, string name, PrimitiveKind underlyingKind, bool isFlags = false)
        : base(id, name)
    {
        if (!underlyingKind.IsIntegral())
            throw new ArgumentException($"Enum underlying kind must be integral, got {underlyingKind}.", nameof(underlyingKind));
        UnderlyingKind = underlyingKind;
        IsFlags = isFlags;
    }

    public PrimitiveKind UnderlyingKind { get; set; }

    public bool IsFlags { get; set; }

    public int? WireBits { get; set; }

    /// <summary>Integral on the wire; an enum has no floating-point representation.</summary>
    public WireForm WireForm
    {
        get => _wireForm;
        set => _wireForm = value == WireForm.Float ? WireForm.Unsigned : value;
    }

    private WireForm _wireForm = WireForm.Unsigned;

    public decimal? WireOffset { get; set; }

    public decimal? WireScale { get; set; }

    public List<EnumMember> Members { get; } = new();

    public EnumType With(string name, long value)
    {
        Members.Add(new EnumMember(name, value));
        return this;
    }

    /// <summary>Range spanned by the declared members, or null when the enum has none.</summary>
    public NumericRange? MemberRange => Members.Count == 0
        ? null
        : new NumericRange(Members.Min(m => m.Value), Members.Max(m => m.Value));

    public override TResult Accept<TResult>(ITypeVisitor<TResult> visitor) => visitor.VisitEnum(this);
}

/// <summary>An ordered collection of named fields. Expands inline into the containing layout.</summary>
public sealed class StructType : TypeDefinition
{
    public StructType(TypeId id, string name) : base(id, name) { }

    public List<FieldBinding> Fields { get; } = new();

    public StructType With(params FieldBinding[] fields)
    {
        Fields.AddRange(fields);
        return this;
    }

    public override TResult Accept<TResult>(ITypeVisitor<TResult> visitor) => visitor.VisitStruct(this);
}

/// <summary>
/// An ordered collection of same-typed elements. A string is an array of <see cref="PrimitiveKind.Char"/>;
/// an enum list is an array of an <see cref="EnumType"/>.
/// </summary>
public sealed class ArrayType : TypeDefinition
{
    public ArrayType(TypeId id, string name, TypeId elementTypeId, ArrayLength length)
        : base(id, name)
    {
        ElementTypeId = elementTypeId;
        Length = length ?? throw new ArgumentNullException(nameof(length));
    }

    public TypeId ElementTypeId { get; set; }

    public ArrayLength Length { get; set; }

    public override TResult Accept<TResult>(ITypeVisitor<TResult> visitor) => visitor.VisitArray(this);
}

/// <summary>
/// How many elements an array holds. <see cref="Fixed"/> is always exactly that many; every other case
/// declares a maximum capacity with the actual count determined at parse time.
/// </summary>
public abstract record ArrayLength
{
    private protected ArrayLength() { }

    /// <summary>Maximum number of elements. Equals the exact count for <see cref="Fixed"/>.</summary>
    public abstract int Capacity { get; }

    /// <summary>
    /// Fewest elements a valid message may carry. Zero unless declared; equals the exact count for
    /// <see cref="Fixed"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A lower bound is <em>declarative intent</em>, not a wire mechanism: nothing about the encoding
    /// changes because a caller promised at least one element. It exists so the promise can be stated
    /// once and checked everywhere — the validator warns when a count field cannot honour it, the layout
    /// engine folds it into <c>MinBits</c> so a frame budget is measured against the real floor rather
    /// than an empty array, and the protobuf target emits it as <c>repeated.min_items</c>.
    /// </para>
    /// <para>
    /// The generated C codec does not enforce it. It enforces no bound today — an over-long array is
    /// capped by the emitted loop, not rejected — and a decoder that refuses a short array would be a
    /// policy decision the caller never asked for.
    /// </para>
    /// </remarks>
    public abstract int MinimumCount { get; }

    public bool IsDynamic => this is not Fixed;

    /// <summary>Whether the count is pinned to a single value, so min and max coincide.</summary>
    public bool IsExactCount => MinimumCount == Capacity;

    /// <summary>Exactly <paramref name="Count"/> elements, always.</summary>
    public sealed record Fixed(int Count) : ArrayLength
    {
        public override int Capacity => Count;

        public override int MinimumCount => Count;
    }

    /// <summary>Element count is carried by an earlier field in the same message.</summary>
    public sealed record CountFromField(FieldId CountFieldId, int MaxCount, int MinCount = 0) : ArrayLength
    {
        public override int Capacity => MaxCount;

        public override int MinimumCount => MinCount;
    }

    /// <summary>Element count is written immediately before the elements, in <paramref name="PrefixBits"/> bits.</summary>
    public sealed record LengthPrefixed(int PrefixBits, int MaxCount, int MinCount = 0) : ArrayLength
    {
        public override int Capacity => MaxCount;

        public override int MinimumCount => MinCount;
    }

    /// <summary>Elements run until a sentinel value is seen — the NUL-terminated string case.</summary>
    public sealed record Terminated(IReadOnlyList<byte> Sentinel, int MaxCount, int MinCount = 0) : ArrayLength
    {
        public override int Capacity => MaxCount;

        public override int MinimumCount => MinCount;
    }

    /// <summary>Elements consume the remainder of the frame.</summary>
    public sealed record FillRemaining(int MaxCount, int MinCount = 0) : ArrayLength
    {
        public override int Capacity => MaxCount;

        public override int MinimumCount => MinCount;
    }
}
