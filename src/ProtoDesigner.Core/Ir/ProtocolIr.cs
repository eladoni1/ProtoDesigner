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
    IReadOnlyList<IrPrimitive> Primitives,
    IReadOnlyList<IrEnum> Enums,
    IReadOnlyList<IrStruct> Structs,
    IReadOnlyList<IrMessage> Messages,
    // Every module on the bus, in declaration order. Carried so a generator can emit a stable id for
    // each one: a module is a participant on the bus, not a type, so nothing else in the IR names them.
    IReadOnlyList<IrModule> Modules);

/// <summary>A module on the bus, with the id a generator gives it.</summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is assigned here rather than stored on the model, in the same spirit as a layout
/// offset: it is derived from declaration order, so adding a module appends and never renumbers the ones
/// before it. A module removed from the middle *does* shift the ones after it — module ids are for code
/// on both ends of one generated header, not a value anybody puts on the wire.
/// </para>
/// <para>
/// If a module id ever needs to survive a reorder the way <c>Message.WireId</c> does, it has to become
/// declared state on <c>Module</c>, and this becomes its fallback.
/// </para>
/// </remarks>
public sealed record IrModule(string Name, int Value);

/// <summary>
/// A named primitive type the protocol declares — a <c>Temperature</c> that is a <c>double</c> on the
/// host and 8 bits on the wire.
/// </summary>
/// <remarks>
/// It carries no members and generates no declaration: the host type is already a built-in, so emitting
/// a typedef would add a name without adding meaning. What it exists for is the wire size, which is the
/// one thing about it a caller cannot work out from the host type alone.
/// </remarks>
/// <param name="Name">The type's name, as the user declared it.</param>
/// <param name="Host">The primitive kind it is stored as.</param>
/// <param name="WireBits">Its declared wire size, or the host kind's natural width.</param>
/// <param name="Range">
/// The limits the user declared, or null for an unbounded type. Carried because a target may be able to
/// express the range even when it cannot express the encoding that the range made possible — protobuf
/// has no 12-bit field, but it can state that a value is between 1000 and 1015.
/// </param>
public sealed record IrPrimitive(string Name, PrimitiveKind Host, int WireBits, NumericRange? Range);

/// <summary>An enum type referenced by one or more fields. Emitted as a first-class type by generators.</summary>
/// <param name="Name">The type's name, as the user declared it.</param>
/// <param name="Underlying">The primitive kind that holds a member's value on the host.</param>
/// <param name="IsFlags">Whether members combine as a bit set.</param>
/// <param name="Members">The declared members, in declaration order.</param>
/// <param name="WireBits">
/// The type's own wire size — its declared <c>WireBits</c>, or its underlying kind's natural width.
/// A binding may still override it, so this is the type's default, not a promise about every field.
/// </param>
/// <param name="Synthetic">
/// Whether the bus supplied these members rather than the user. Generators need no special case — by the
/// time they see it the members are filled in — but it explains an enum with no declaration behind it.
/// </param>
public sealed record IrEnum(
    string Name,
    PrimitiveKind Underlying,
    bool IsFlags,
    IReadOnlyList<IrEnumMember> Members,
    int WireBits,
    SyntheticEnum Synthetic = SyntheticEnum.None);

public sealed record IrEnumMember(string Name, long Value);

/// <summary>
/// A struct type referenced by one or more messages, in declaration order — a struct always appears
/// after everything it depends on, so a generator can emit the list top to bottom.
/// </summary>
/// <param name="Name">The type's name, as the user declared it.</param>
/// <param name="Members">The struct's members, in declaration order — which is wire order.</param>
/// <param name="WireBits">
/// What the struct occupies on the wire on its own, under the project's layout options. A struct's
/// members carry their own encodings, so this is a property of the struct rather than of where it is
/// used — but see the note in <see cref="IrBuilder"/>: bit packing means a struct that starts
/// mid-byte can still straddle differently, so this is the size to reserve, not a universal offset.
/// Zero when the struct has no size of its own.
/// </param>
public sealed record IrStruct(string Name, IReadOnlyList<IrMember> Members, int WireBits);

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
    string? Note,
    // The limits declared on the member's type, or on an array's element type. Null when unbounded.
    // A target that cannot reproduce the narrow encoding can still often state the constraint.
    NumericRange? Range = null,
    // The binding's assigned protobuf field number, or null when it has never been exported. Carried
    // because the number must be stable across regenerations — deriving one from declaration order
    // would renumber every field the moment one moved, silently breaking deployed protobuf peers.
    int? ProtoFieldNumber = null,
    // Fewest elements a valid message may carry, when this member is an array. Equals ArrayCapacity for
    // a fixed-count array. Carried as a number rather than left to be inferred from Note, because a
    // target that emits a lower bound must not depend on the wording of a human-readable comment.
    int? ArrayMinCount = null);

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
    // The element's primitive kind (integral / float / char). Meaningless when HasCompositeElement —
    // a struct element has no single kind, and nothing but a placeholder can be put here.
    PrimitiveKind ElementPrimitive,
    // Element enum index into ProtocolIr.Enums, if the element is an enum.
    int? ElementEnumIndex,
    // For CountFromField: index into IrMessage.Fields.
    int? CountFieldIndex,
    // For LengthPrefixed: width of the length prefix in bits.
    int PrefixBits,
    // For Terminated: sentinel bytes.
    IReadOnlyList<byte> Sentinel,
    // Null when the element is a single scalar or enum — the common case, which every field above
    // already describes. Non-null when the element is a struct: one entry per value inside one
    // element, each positioned from that element's own start. A generator loops over elements and,
    // inside that, over these.
    //
    // This exists because ElementPrimitive can only name one scalar kind, so before it there was no
    // shape in which "the element is a struct with four fields" could be said at all — which is why
    // arrays of structs were refused rather than merely unimplemented.
    IReadOnlyList<IrElementField>? ElementFields = null)
{
    /// <summary>True when the element is a struct, described by <see cref="ElementFields"/>.</summary>
    /// <remarks>
    /// Check this before reading <see cref="ElementPrimitive"/>, <see cref="ElementEnumIndex"/> or
    /// treating <see cref="ElementBits"/> as one value's width — for a struct element they are a
    /// placeholder, null, and the whole element stride respectively. <see cref="PrimitiveKind"/> has no
    /// "none" member to put there instead, so this property is the discriminator: reading the kind of an
    /// element that has none is how an array of structs silently became an array of <c>uint8_t</c> the
    /// first time round.
    /// </remarks>
    public bool HasCompositeElement => ElementFields is { Count: > 0 };
}

/// <summary>One value inside a composite array element.</summary>
/// <remarks>
/// <see cref="BitOffset"/> is measured from the start of a single element, not from the region — so a
/// generator positions it at <c>arrayBase + i * ElementBits + BitOffset</c>. That is exactly what the
/// layout engine already computes for nodes beneath an array; this record is how it reaches a
/// generator instead of being dropped.
/// </remarks>
public sealed record IrElementField(
    // Access path within one element, e.g. "discId". Nested struct members are dotted, e.g. "inner.x".
    string Name,
    PrimitiveKind Primitive,
    int? EnumIndex,
    int BitOffset,
    // Width of one value. For a fixed array member this is the width of a single item, not the whole.
    int BitWidth,
    Endianness Endianness,
    BitOrder BitOrder,
    ScalarTransform Transform,
    bool WireIsSigned,
    // Non-null when this member is itself a fixed-size array inside the element, e.g. a 64-byte
    // payload inside a serial-channel struct. Items are contiguous at BitWidth each.
    int? FixedArrayCount = null);
