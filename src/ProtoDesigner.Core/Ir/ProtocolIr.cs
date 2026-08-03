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
    IReadOnlyList<IrStruct> Structs,
    IReadOnlyList<IrMessage> Messages);

/// <summary>An enum type referenced by one or more fields. Emitted as a first-class type by generators.</summary>
public sealed record IrEnum(
    string Name,
    PrimitiveKind Underlying,
    bool IsFlags,
    IReadOnlyList<IrEnumMember> Members);

public sealed record IrEnumMember(string Name, long Value);

/// <summary>
/// A struct type referenced by one or more messages, in declaration order — a struct always appears
/// after everything it depends on, so a generator can emit the list top to bottom.
/// </summary>
public sealed record IrStruct(string Name, IReadOnlyList<IrMember> Members);

public enum IrMemberKind
{
    Scalar,
    EnumRef,
    StructRef,
}

/// <summary>
/// One member of a host struct, describing the shape a value has <em>in memory</em>.
/// </summary>
/// <remarks>
/// This is the deliberate counterpart to <see cref="IrField"/>: **flat for the wire, nested for the
/// host**. <see cref="IrMessage.Fields"/> stays flattened because offsets, regions and the variable-region
/// cursor are all defined over leaves in wire order. Members describe the struct the user actually
/// declared, so a <c>Header</c> used by ten messages is emitted once and referenced by name rather than
/// inlined ten times. A generator reads Members to declare types and Fields to convert them, and
/// <see cref="IrField.Path"/> (<c>header.messageId</c>) is what ties the two together.
/// </remarks>
public sealed record IrMember(
    string Name,
    IrMemberKind Kind,
    // Meaningful when Kind is Scalar; for an array it is the element's kind.
    PrimitiveKind Primitive,
    // Index into ProtocolIr.Enums when Kind is EnumRef.
    int? EnumIndex,
    // Index into ProtocolIr.Structs when Kind is StructRef.
    int? StructIndex,
    // Element capacity when this member is an array; null when it is a single value.
    int? ArrayCapacity,
    // True when the array carries its own element count at runtime (length-prefixed, sentinel or
    // fill-remaining). Fixed arrays and count-from-field arrays do not: the first is a constant and the
    // second already has a count field, and a second copy could disagree with it.
    bool NeedsCountMember,
    // Human-readable note the generator can put in a comment — width, transform, array length rule.
    string? Note);

/// <summary>
/// One message. <see cref="Fields"/> are flattened in wire order (structs and static arrays expanded);
/// <see cref="Members"/> describe the host struct's shape.
/// </summary>
public sealed record IrMessage(
    string Name,
    int? WireId,
    int MinBits,
    int MaxBits,
    IReadOnlyList<IrRegion> Regions,
    IReadOnlyList<IrField> Fields,
    IReadOnlyList<IrMember> Members);

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
    IrArrayInfo? Array,
    // Whether the wire code is two's-complement signed. Resolved here rather than inferred by each
    // generator from the host kind, because the two are not the same question: a signed host whose
    // transform biases its range non-negative — -100..100 offset by -100 — produces codes 0..255, which
    // only fit an unsigned field. Reading those back as signed turned code 200 into -56, and the inverse
    // transform then into a value nowhere near the original.
    bool WireIsSigned);

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
