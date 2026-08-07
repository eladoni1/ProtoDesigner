using ProtoDesigner.Core.Validation.Rules;

namespace ProtoDesigner.Core.Tests.Validation;

/// <summary>
/// The gate that decides whether a message can be exported as <c>.proto</c>.
/// </summary>
/// <remarks>
/// The load-bearing pair is <see cref="An_offset_only_transform_is_still_exportable"/> and
/// <see cref="A_scaled_field_is_not_exportable"/>. An offset merely narrows the wire width and protobuf
/// sends the value itself, so it costs nothing; a scale is lossy quantization and would make a protobuf
/// peer and a C peer disagree about the number. Getting that distinction backwards would either block
/// perfectly good messages or ship silently wrong ones.
/// </remarks>
public class ProtobufRulesTests
{
    private static IReadOnlyList<Diagnostic> Gate(ValidationBuilder b) =>
        new Validator(ProtobufRules.All).Validate(b.Project);

    [Fact]
    public void A_sub_byte_field_is_not_exportable()
    {
        var b = new ValidationBuilder();
        var mode = b.Enum("Mode", PrimitiveKind.U8, ("Idle", 0), ("Busy", 1));
        b.NewMessage("M", ValidationBuilder.F("mode", mode, FieldEncoding.Packed(4)));

        Gate(b).Has(DiagnosticCodes.ProtoSubByteField);
    }

    [Fact]
    public void A_thirty_two_bit_field_is_exportable()
    {
        // 32 and 64 are the only integer widths protobuf has, so they are the only ones that export.
        var b = new ValidationBuilder();
        var u32 = b.Prim("u32", PrimitiveKind.U32);
        b.NewMessage("M", ValidationBuilder.F("value", u32));

        Gate(b).NoErrors();
    }

    [Fact]
    public void A_two_byte_enum_is_not_exportable()
    {
        // The case that slipped through the first time. An enum keeps its named members across the export,
        // which made it look exempt — but protobuf sends an enum as a varint in the int32 domain, so a
        // 2-byte enum is widened exactly as a u16 is. Found in a real project whose Mode enum was 16 bits.
        var b = new ValidationBuilder();
        var mode = b.Enum("Mode", PrimitiveKind.U16, ("Idle", 0), ("Busy", 1));
        b.NewMessage("M", ValidationBuilder.F("mode", mode, new FieldEncoding { BitWidth = 16 }));

        Gate(b).Has(DiagnosticCodes.ProtoNarrowInteger);
    }

    [Fact]
    public void A_thirty_two_bit_enum_is_exportable()
    {
        var b = new ValidationBuilder();
        var mode = b.Enum("Mode", PrimitiveKind.U32, ("Idle", 0), ("Busy", 1));
        b.NewMessage("M", ValidationBuilder.F("mode", mode));

        Gate(b).NoErrors();
    }

    [Fact]
    public void A_bool_is_exportable_at_its_natural_width()
    {
        // The exemption that stays. protobuf's bool has no width to choose — you cannot declare a 32-bit
        // one — so refusing 8-bit bools would mean a protobuf export could never carry a boolean.
        var b = new ValidationBuilder();
        var flag = b.Prim("flag", PrimitiveKind.Bool);
        b.NewMessage("M", ValidationBuilder.F("flag", flag));

        Gate(b).NoErrors();
    }

    [Fact]
    public void A_two_byte_field_is_not_exportable()
    {
        // Whole bytes, so the sub-byte rule does not fire — but protobuf has no 16-bit integer, and
        // exporting one as uint32 would quietly make it a different size than it was designed as.
        var b = new ValidationBuilder();
        var u16 = b.Prim("u16", PrimitiveKind.U16);
        b.NewMessage("M", ValidationBuilder.F("value", u16));

        Gate(b).Has(DiagnosticCodes.ProtoNarrowInteger);
    }

    [Fact]
    public void An_offset_only_transform_is_still_exportable()
    {
        // 1000..1015 sent as codes 0..15. The offset exists only to narrow the width; protobuf sends the
        // value itself, so nothing is lost and the range survives as a protovalidate constraint.
        var b = new ValidationBuilder();
        var temperature = b.Prim("Temperature", PrimitiveKind.U32, new NumericRange(1000, 1015));
        b.NewMessage("M", ValidationBuilder.F("temperature", temperature,
            new FieldEncoding { BitWidth = 32, Transform = new ScalarTransform(1000, 1) }));

        var findings = Gate(b);
        findings.HasNo(DiagnosticCodes.ProtoScaledField);
        findings.NoErrors();
    }

    [Fact]
    public void A_scaled_field_is_not_exportable()
    {
        // Quantization is lossy. Protobuf would carry full precision, so the two peers would disagree.
        var b = new ValidationBuilder();
        var battery = b.Prim("Battery", PrimitiveKind.F32, new NumericRange(0, 100));
        b.NewMessage("M", ValidationBuilder.F("battery", battery,
            new FieldEncoding { BitWidth = 8, Transform = new ScalarTransform(0, 0.5m) }));

        Gate(b).Has(DiagnosticCodes.ProtoScaledField);
    }

    [Fact]
    public void Big_endian_is_reported_but_does_not_block()
    {
        var b = new ValidationBuilder();
        var u32 = b.Prim("u32", PrimitiveKind.U32);
        b.NewMessage("M", ValidationBuilder.F("value", u32,
            new FieldEncoding { Endianness = Endianness.Big }));

        var findings = Gate(b);
        var note = findings.Has(DiagnosticCodes.ProtoEndiannessIgnored);
        Assert.Equal(Severity.Warning, note.Severity);
        findings.NoErrors();
    }

    [Fact]
    public void A_single_byte_field_has_no_endianness_to_lose()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.NewMessage("M", ValidationBuilder.F("value", u8,
            new FieldEncoding { Endianness = Endianness.Big }));

        Gate(b).HasNo(DiagnosticCodes.ProtoEndiannessIgnored);
    }

    [Fact]
    public void Two_fields_claiming_one_number_is_an_error()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var first = ValidationBuilder.F("a", u8);
        var second = ValidationBuilder.F("b", u8);
        first.ProtoFieldNumber = 1;
        second.ProtoFieldNumber = 1;
        b.NewMessage("M", first, second);

        Gate(b).Has(DiagnosticCodes.ProtoDuplicateFieldNumber);
    }

    [Fact]
    public void Distinct_numbers_are_fine()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var first = ValidationBuilder.F("a", u8);
        var second = ValidationBuilder.F("b", u8);
        first.ProtoFieldNumber = 1;
        second.ProtoFieldNumber = 2;
        b.NewMessage("M", first, second);

        Gate(b).HasNo(DiagnosticCodes.ProtoDuplicateFieldNumber);
    }

    [Fact]
    public void Unassigned_numbers_are_not_duplicates_of_each_other()
    {
        // Every field starts null. Treating null as a clash would flag every project before its first
        // export, which is every project.
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.NewMessage("M", ValidationBuilder.F("a", u8), ValidationBuilder.F("b", u8));

        Gate(b).HasNo(DiagnosticCodes.ProtoDuplicateFieldNumber);
    }

    [Fact]
    public void A_duplicate_inside_a_shared_struct_is_caught()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var first = ValidationBuilder.F("a", u8);
        var second = ValidationBuilder.F("b", u8);
        first.ProtoFieldNumber = 3;
        second.ProtoFieldNumber = 3;
        var header = b.Struct("Header", first, second);
        b.NewMessage("M", ValidationBuilder.F("header", header));

        Gate(b).Has(DiagnosticCodes.ProtoDuplicateFieldNumber);
    }

    /// <summary>
    /// The rules must never reach the shipped catalogue. A bit-packed message is exactly what this tool
    /// is for; if these codes could block ordinary generation the C target would stop working the moment
    /// someone packed a field.
    /// </summary>
    [Fact]
    public void The_gate_is_not_part_of_the_default_rule_set()
    {
        var b = new ValidationBuilder();
        var mode = b.Enum("Mode", PrimitiveKind.U8, ("Idle", 0), ("Busy", 1));
        b.NewMessage("M", ValidationBuilder.F("mode", mode, FieldEncoding.Packed(4)));

        var shipped = b.Run();
        shipped.HasNo(DiagnosticCodes.ProtoSubByteField);
        shipped.NoErrors();

        // ...but the gate itself still sees it.
        Gate(b).Has(DiagnosticCodes.ProtoSubByteField);
    }
}
