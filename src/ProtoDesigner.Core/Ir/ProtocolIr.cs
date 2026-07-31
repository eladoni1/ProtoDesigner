using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Ir;

/// <summary>
/// A resolved, validated, layout-annotated snapshot of one bus's protocol. Generators consume this
/// and this alone — no library references, no overrides, no unresolved names. Every offset, every
/// width, every endianness is baked in.
/// </summary>
public sealed record ProtocolIr(
    string ProjectName,
    string BusName,
    Transport Transport,
    IReadOnlyList<IrEnum> Enums,
    IReadOnlyList<IrMessage> Messages);

/// <summary>An enum type referenced by one or more fields. Emitted as a first-class type by generators.</summary>
public sealed record IrEnum(
    string Name,
    PrimitiveKind Underlying,
    bool IsFlags,
    IReadOnlyList<IrEnumMember> Members);

public sealed record IrEnumMember(string Name, long Value);

/// <summary>One message. Fields are flattened in wire order (structs and static arrays are expanded).</summary>
public sealed record IrMessage(
    string Name,
    int? WireId,
    int MinBits,
    int MaxBits,
    IReadOnlyList<IrRegion> Regions,
    IReadOnlyList<IrField> Fields);

public enum IrRegionKind
{
    Fixed,
    Variable,
}

/// <summary>
/// A contiguous span of the message. Fixed regions carry compile-time-constant offsets; a Variable
/// region holds exactly one dynamic array, whose length is only known at parse time.
/// </summary>
public sealed record IrRegion(
    int Index,
    IrRegionKind Kind,
    int MinBits,
    int MaxBits,
    // ---- Variable-region only ------------------------------------------------------------------
    // ElementBits: bit stride of one element.
    int ElementBits,
    // MaxElements: declared capacity.
    int MaxElements,
    // CountFieldIndex: index into IrMessage.Fields of the count field, when the count is carried
    // by another field. Null for length-prefixed / sentinel / fill-remaining.
    int? CountFieldIndex,
    // PrefixBits: non-zero if the length is written as an inline prefix before the elements.
    int PrefixBits);

public enum IrFieldKind
{
    Scalar,
    EnumRef,
    Array,
}

public enum IrArrayKind
{
    Fixed,
    CountFromField,
    LengthPrefixed,
    Terminated,
    FillRemaining,
}

/// <summary>
/// A leaf field carrying an actual value. Structs are flattened away and their members appear here
/// with dotted paths (`header.messageId`). Static arrays are one field with array info; dynamic
/// arrays live in a variable region.
/// </summary>
public sealed record IrField(
    string Path,
    IrFieldKind Kind,
    PrimitiveKind Primitive,
    int? EnumIndex,
    int RegionIndex,
    int BitOffset,
    int BitWidth,
    Endianness Endianness,
    BitOrder BitOrder,
    ScalarTransform Transform,
    IrArrayInfo? Array);

/// <summary>Everything a generator needs to code an array element in isolation.</summary>
public sealed record IrArrayInfo(
    IrArrayKind Kind,
    // For Fixed: the exact element count. Null for any dynamic kind.
    int? ElementCount,
    // Declared maximum element count. Equals ElementCount for Fixed.
    int MaxElements,
    // Bit stride of a single element.
    int ElementBits,
    // The element's primitive kind (integral / float / char).
    PrimitiveKind ElementPrimitive,
    // Element enum index into ProtocolIr.Enums, if the element is an enum.
    int? ElementEnumIndex,
    // For CountFromField: index into IrMessage.Fields.
    int? CountFieldIndex,
    // For LengthPrefixed: width of the length prefix in bits.
    int PrefixBits,
    // For Terminated: sentinel bytes.
    IReadOnlyList<byte> Sentinel);
