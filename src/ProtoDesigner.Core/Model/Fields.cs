namespace ProtoDesigner.Core.Model;

/// <summary>
/// How a field is represented on the wire, independent of what it means. Every property is nullable:
/// null inherits from the message, then the bus, then the project, then the built-in default.
/// </summary>
public sealed class FieldEncoding
{
    /// <summary>
    /// Serialized width in bits. Null uses the natural width of the type. For an array binding this is the
    /// width of one *element*, not the whole array.
    /// </summary>
    public int? BitWidth { get; set; }

    public Endianness? Endianness { get; set; }

    public BitOrder? BitOrder { get; set; }

    /// <summary>When true the field may share a storage byte with its neighbours and is not aligned.</summary>
    public bool AllowBitPacking { get; set; }

    /// <summary>Forces the field to start on a multiple of this many bits. Overrides the packing rules.</summary>
    public int? AlignmentBits { get; set; }

    /// <summary>Affine value-to-wire-code mapping. Null means identity.</summary>
    public ScalarTransform? Transform { get; set; }

    public static FieldEncoding Natural() => new();

    /// <summary>Bit-packed at an explicit width — the "enum with 11 values in 4 bits" case.</summary>
    public static FieldEncoding Packed(int bits) => new() { BitWidth = bits, AllowBitPacking = true };

    /// <summary>Explicit width, still aligned per the message default.</summary>
    public static FieldEncoding Sized(int bits) => new() { BitWidth = bits };

    public static FieldEncoding AlignedTo(int alignmentBits) => new() { AlignmentBits = alignmentBits };

    public FieldEncoding Clone() => (FieldEncoding)MemberwiseClone();
}

/// <summary>
/// A named occurrence of a type inside a message or struct. Order within the containing list *is* the
/// wire order. The binding owns the encoding, so one type may appear at different widths in different places.
/// </summary>
public sealed class FieldBinding
{
    public FieldBinding(FieldId id, string name, TypeId typeId, FieldEncoding? encoding = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Field name must not be empty.", nameof(name));
        Id = id;
        Name = name;
        TypeId = typeId;
        Encoding = encoding ?? FieldEncoding.Natural();
    }

    public FieldBinding(string name, TypeId typeId, FieldEncoding? encoding = null)
        : this(FieldId.New(), name, typeId, encoding) { }

    public FieldId Id { get; }

    /// <summary>Display and codegen name. Mutable; references to this field are by <see cref="Id"/>.</summary>
    public string Name { get; set; }

    public TypeId TypeId { get; set; }

    public FieldEncoding Encoding { get; set; }

    public object? DefaultValue { get; set; }

    public string? Description { get; set; }

    public override string ToString() => $"{Name}: {TypeId}";
}
