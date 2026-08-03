namespace ProtoDesigner.Core.Tests.Validation;

public class CrcAndBudgetTests
{
    [Fact]
    public void Crc_field_covered_by_its_own_span_reports_PD0040()
    {
        var b = new ValidationBuilder();
        var u16 = b.Prim("u16", PrimitiveKind.U16);
        var crcField = ValidationBuilder.F("crc", u16);
        crcField.Crc = new CrcSpec(CrcAlgorithm.Crc16Ccitt, new CrcCoverage(FromFieldId: null, ToFieldId: crcField.Id));
        b.NewMessage("M", ValidationBuilder.F("payload", u16), crcField);

        b.Run().Has(DiagnosticCodes.CrcCoversItself);
    }

    [Fact]
    public void Whole_message_coverage_excludes_the_crc_and_is_fine()
    {
        var b = new ValidationBuilder();
        var u16 = b.Prim("u16", PrimitiveKind.U16);
        var crcField = ValidationBuilder.F("crc", u16);
        crcField.Crc = new CrcSpec(CrcAlgorithm.Crc16Ccitt);
        b.NewMessage("M", ValidationBuilder.F("payload", u16), crcField);

        b.Run().HasNo(DiagnosticCodes.CrcCoversItself);
    }

    [Fact]
    public void A_message_that_overflows_the_uart_budget_reports_PD0050()
    {
        var b = new ValidationBuilder(Transport.Uart);
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var big = b.Array("Big", u8, new ArrayLength.Fixed(400));
        b.NewMessage("Big", ValidationBuilder.F("data", big));

        b.Run().Has(DiagnosticCodes.MtuExceeded);
    }

    [Fact]
    public void A_small_message_produces_no_mtu_diagnostic()
    {
        var b = new ValidationBuilder(Transport.Uart);
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.NewMessage("Small", ValidationBuilder.F("x", u8));

        b.Run().HasNo(DiagnosticCodes.MtuExceeded);
    }

    // A composed type nobody uses is worth saying: somebody built it and then did not wire it up.
    [Fact]
    public void An_unreferenced_struct_reports_PD0060_info()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.Struct("Unused", ValidationBuilder.F("x", u8));
        b.NewMessage("M", ValidationBuilder.F("y", u8));

        var d = b.Run().Has(DiagnosticCodes.UnreferencedType);
        Assert.Equal(Severity.Info, d.Severity);
        Assert.Contains("Unused", d.Message);
    }

    [Fact]
    public void An_unreferenced_enum_reports_PD0060_info()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.Enum("Unused", PrimitiveKind.U8, ("A", 0));
        b.NewMessage("M", ValidationBuilder.F("y", u8));

        b.Run().Has(DiagnosticCodes.UnreferencedType);
    }

    /// <summary>
    /// Primitives are vocabulary, not design. A project seeds bool, char and every integer width so one
    /// is there when a field needs it; reporting each unused u64 put a dozen notes in the pane about
    /// types the user never chose to create, and buried the findings that mattered.
    /// </summary>
    [Fact]
    public void An_unreferenced_primitive_is_not_reported()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.Prim("i64", PrimitiveKind.I64);
        b.Prim("bool", PrimitiveKind.Bool);
        b.NewMessage("M", ValidationBuilder.F("y", u8));

        b.Run().HasNo(DiagnosticCodes.UnreferencedType);
    }
}
