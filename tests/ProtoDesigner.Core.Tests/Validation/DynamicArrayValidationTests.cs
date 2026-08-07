namespace ProtoDesigner.Core.Tests.Validation;

public class DynamicArrayValidationTests
{
    [Fact]
    public void Count_field_after_the_array_reports_PD0030()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8);
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, 4));
        b.NewMessage("M", ValidationBuilder.F("samples", samples), count);

        b.Run().Has(DiagnosticCodes.CountFieldMissing);
    }

    [Fact]
    public void Signed_count_field_reports_PD0032()
    {
        var b = new ValidationBuilder();
        var i8 = b.Prim("i8", PrimitiveKind.I8);
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", i8);
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, 4));
        b.NewMessage("M", count, ValidationBuilder.F("samples", samples));

        b.Run().Has(DiagnosticCodes.CountFieldNotInteger);
    }

    [Fact]
    public void Count_field_too_narrow_reports_PD0033()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8, FieldEncoding.Packed(3));
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, 20));
        b.NewMessage("M", count, ValidationBuilder.F("samples", samples));

        b.Run().Has(DiagnosticCodes.CountFieldTooNarrow);
    }

    [Fact]
    public void Length_prefix_too_narrow_for_capacity_reports_PD0034()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var payload = b.Array("Payload", u8, new ArrayLength.LengthPrefixed(PrefixBits: 3, MaxCount: 20));
        b.NewMessage("M", ValidationBuilder.F("payload", payload));

        b.Run().Has(DiagnosticCodes.LengthPrefixTooNarrow);
    }

    [Fact]
    public void Valid_dynamic_array_produces_no_dynamic_array_diagnostics()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8);
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, 100));
        b.NewMessage("M", count, ValidationBuilder.F("samples", samples));

        var findings = b.Run();
        findings.HasNo(DiagnosticCodes.CountFieldMissing);
        findings.HasNo(DiagnosticCodes.CountFieldNotInteger);
        findings.HasNo(DiagnosticCodes.CountFieldTooNarrow);
    }

    [Fact]
    public void Count_field_inside_an_earlier_struct_is_accepted()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8);
        var header = b.Struct("Header", ValidationBuilder.F("tag", u8), count);
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, 4));
        b.NewMessage("M", ValidationBuilder.F("header", header), ValidationBuilder.F("samples", samples));

        b.Run().HasNo(DiagnosticCodes.CountFieldMissing);
    }

    // ---- declared minimums -------------------------------------------------------------------------

    [Fact]
    public void A_minimum_above_the_capacity_is_reported()
    {
        // The layout engine also refuses this, but by throwing. A user who typed 10 into a box that holds
        // 8 should get a diagnostic naming the array, not an exception from the byte map.
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8);
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, MaxCount: 8, MinCount: 10));
        b.NewMessage("M", count, ValidationBuilder.F("samples", samples));

        b.Run().Has(DiagnosticCodes.ArrayMinCountUnreachable);
    }

    [Fact]
    public void A_negative_minimum_is_reported()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8);
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, MaxCount: 8, MinCount: -1));
        b.NewMessage("M", count, ValidationBuilder.F("samples", samples));

        b.Run().Has(DiagnosticCodes.ArrayMinCountUnreachable);
    }

    [Fact]
    public void A_minimum_within_the_capacity_is_accepted()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("count", u8);
        var samples = b.Array("Samples", u8, new ArrayLength.CountFromField(count.Id, MaxCount: 8, MinCount: 1));
        b.NewMessage("M", count, ValidationBuilder.F("samples", samples));

        b.Run().HasNo(DiagnosticCodes.ArrayMinCountUnreachable);
    }

    [Fact]
    public void A_minimum_equal_to_the_capacity_is_accepted()
    {
        // Pinning both ends is legal — it is a variable-length rule carrying a constant count, which is
        // different from a Fixed array only in that the count still travels on the wire.
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var samples = b.Array("Samples", u8,
            new ArrayLength.LengthPrefixed(PrefixBits: 8, MaxCount: 4, MinCount: 4));
        b.NewMessage("M", ValidationBuilder.F("samples", samples));

        b.Run().HasNo(DiagnosticCodes.ArrayMinCountUnreachable);
    }
}
