namespace ProtoDesigner.Core.Tests.Validation;

/// <summary>
/// A hand-crafted corpus of projects that must be free of any Error diagnostics. Serves two roles at once:
/// a regression net (new rules can't turn shipping projects red) and an executable set of examples showing
/// how a well-formed model looks. When adding a new rule, add a variant that trips it on top of a copy of
/// one of these; the corpus itself stays clean.
/// </summary>
public class ValidCorpusTests
{
    [Fact]
    public void An_empty_project_has_no_errors()
    {
        var project = new Project("Empty");
        new Validator().Validate(project).NoErrors();
    }

    [Fact]
    public void A_single_bus_with_a_simple_message_is_clean()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.NewMessage("Ping", ValidationBuilder.F("id", u8));

        b.Run().NoErrors();
    }

    [Fact]
    public void A_realistic_telemetry_message_is_clean()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var u16 = b.Prim("u16", PrimitiveKind.U16);
        var u32 = b.Prim("u32", PrimitiveKind.U32);
        var temperature = b.Prim("Temperature", PrimitiveKind.U16, new NumericRange(1000, 1015));

        var mode = b.Enum("Mode", PrimitiveKind.U32,
            ("Idle", 0), ("Arming", 1), ("Running", 5), ("Fault", 10));

        var header = b.Struct("Header",
            ValidationBuilder.F("messageId", u8),
            ValidationBuilder.F("flags", u8),
            ValidationBuilder.F("timestamp", u32));

        var samples = b.Array("Samples", u16, new ArrayLength.Fixed(4));

        var telemetry = b.NewMessage("Telemetry",
            ValidationBuilder.F("header", header),
            ValidationBuilder.F("mode", mode, FieldEncoding.Packed(4)),
            ValidationBuilder.F("temperature", temperature,
                new FieldEncoding { BitWidth = 4, AllowBitPacking = true, Transform = new ScalarTransform(1000, 1) }),
            ValidationBuilder.F("samples", samples),
            ValidationBuilder.F("checksum", u16));
        telemetry.WireId = 42;

        b.Run().NoErrors();
    }

    [Fact]
    public void A_dynamic_array_driven_by_a_count_field_is_clean()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8);
        var payload = b.Array("Payload", u8, new ArrayLength.CountFromField(count.Id, 200));
        b.NewMessage("Batch", count, ValidationBuilder.F("payload", payload));

        b.Run().NoErrors();
    }
}
