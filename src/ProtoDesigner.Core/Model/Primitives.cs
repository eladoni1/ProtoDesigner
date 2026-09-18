namespace ProtoDesigner.Core.Model;

/// <summary>
/// The set of primitive value kinds the designer understands. These describe the *logical* value,
/// not its serialized width — see <see cref="FieldEncoding.BitWidth"/> for that.
/// </summary>
public enum PrimitiveKind
{
    Bool,
    Char,
    I8, U8,
    I16, U16,
    I32, U32,
    I64, U64,
    F32, F64,
}

public enum Endianness
{
    Little,
    Big,
}

/// <summary>An enum whose members the bus supplies, rather than the user.</summary>
public enum SyntheticEnum
{
    /// <summary>An ordinary enum: the members are the ones declared on the type.</summary>
    None,

    /// <summary>Every message on the bus that carries a wire id, keyed by that id.</summary>
    MessageId,

    /// <summary>Every module on the bus, numbered from one in declaration order.</summary>
    ModuleId,
}

/// <summary>Order in which bits of a value are written within its storage bits.</summary>
public enum BitOrder
{
    MsbFirst,
    LsbFirst,
}

/// <summary>How consecutive bit-packed fields are placed.</summary>
public enum BitPackingMode
{
    /// <summary>Pure bit stream. Fields pack contiguously and may straddle byte boundaries.</summary>
    Contiguous,

    /// <summary>
    /// C-style bitfields. A field that will not fit in the remainder of the current storage unit
    /// starts a new unit. The unit size is the natural width of the field's primitive kind.
    /// </summary>
    StorageUnit,
}

public enum Transport
{
    Ethernet,
    Uart,
}

public static class PrimitiveKindExtensions
{
    /// <summary>Width the value occupies in a host language struct, in bits.</summary>
    public static int NaturalBits(this PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => 8,
        PrimitiveKind.Char => 8,
        PrimitiveKind.I8 or PrimitiveKind.U8 => 8,
        PrimitiveKind.I16 or PrimitiveKind.U16 => 16,
        PrimitiveKind.I32 or PrimitiveKind.U32 or PrimitiveKind.F32 => 32,
        PrimitiveKind.I64 or PrimitiveKind.U64 or PrimitiveKind.F64 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown primitive kind."),
    };

    /// <summary>True for kinds whose wire representation is a two's complement signed integer.</summary>
    public static bool IsSigned(this PrimitiveKind kind) =>
        kind is PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64;

    /// <summary>False for F32/F64, whose bit pattern is IEEE-754 and cannot be range-compressed without a transform.</summary>
    public static bool IsIntegral(this PrimitiveKind kind) => !kind.IsFloat();

    /// <summary>
    /// Whether the kind has values between its values.
    /// </summary>
    /// <remarks>
    /// The distinction behind several decisions that look unrelated: a float may be quantized because a
    /// finer step is more resolution, an integer may not because there is nothing between 1000 and 1001;
    /// a float is 32 or 64 bits in protobuf as it is here; a float's wire form is IEEE rather than a
    /// width to choose. Written out once so those decisions cannot drift apart.
    /// </remarks>
    public static bool IsFloat(this PrimitiveKind kind) =>
        kind is PrimitiveKind.F32 or PrimitiveKind.F64;

    /// <summary>
    /// The full span of values the kind can hold, used to pre-fill a new type's range so a <c>uint8</c>
    /// starts at 0..255 rather than empty.
    /// </summary>
    /// <remarks>
    /// Null for <see cref="PrimitiveKind.F32"/> and <see cref="PrimitiveKind.F64"/>: their ranges exceed
    /// what <see cref="decimal"/> can represent, and a float that has not been given real-world bounds
    /// cannot be range-compressed anyway. The editor asks for an explicit range in that case.
    /// </remarks>
    public static NumericRange? NaturalRange(this PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => new NumericRange(0, 1),
        PrimitiveKind.Char => new NumericRange(0, byte.MaxValue),
        PrimitiveKind.I8 => new NumericRange(sbyte.MinValue, sbyte.MaxValue),
        PrimitiveKind.U8 => new NumericRange(byte.MinValue, byte.MaxValue),
        PrimitiveKind.I16 => new NumericRange(short.MinValue, short.MaxValue),
        PrimitiveKind.U16 => new NumericRange(ushort.MinValue, ushort.MaxValue),
        PrimitiveKind.I32 => new NumericRange(int.MinValue, int.MaxValue),
        PrimitiveKind.U32 => new NumericRange(uint.MinValue, uint.MaxValue),
        PrimitiveKind.I64 => new NumericRange(long.MinValue, long.MaxValue),
        PrimitiveKind.U64 => new NumericRange(ulong.MinValue, ulong.MaxValue),
        PrimitiveKind.F32 or PrimitiveKind.F64 => null,
        _ => null,
    };
}

/// <summary>
/// Inclusive value range. Stored as <see cref="decimal"/> so that the full 64-bit integer domain is
/// represented exactly (a <c>double</c> loses precision above 2^53). Float ranges beyond decimal's
/// magnitude are out of scope — such values are not range-compressible in any case.
/// </summary>
public readonly record struct NumericRange
{
    public decimal Min { get; }
    public decimal Max { get; }

    public NumericRange(decimal min, decimal max)
    {
        if (min > max)
            throw new ArgumentException($"Range minimum {min} exceeds maximum {max}.", nameof(min));
        Min = min;
        Max = max;
    }

    /// <summary>A range whose bounds are equal denotes a constant.</summary>
    public bool IsConstant => Min == Max;

    public bool Contains(decimal value) => value >= Min && value <= Max;

    public override string ToString() => $"[{Min}, {Max}]";
}

/// <summary>
/// Affine mapping between a logical value and its integer wire code: <c>wire = (value - Offset) / Scale</c>.
/// Turns "range 1000..1015" into 4 bits, and is how quantized floats are encoded.
/// </summary>
public readonly record struct ScalarTransform
{
    public decimal Offset { get; }
    public decimal Scale { get; }

    public ScalarTransform(decimal offset, decimal scale)
    {
        if (scale <= 0m)
            throw new ArgumentException($"Scale must be positive, got {scale}.", nameof(scale));
        Offset = offset;
        Scale = scale;
    }

    public static ScalarTransform Identity { get; } = new(0m, 1m);

    public bool IsIdentity => Offset == 0m && Scale == 1m;

    public decimal ToWire(decimal value) => (value - Offset) / Scale;

    public decimal FromWire(decimal code) => (code * Scale) + Offset;

    public override string ToString() => IsIdentity ? "identity" : $"(v - {Offset}) / {Scale}";
}
