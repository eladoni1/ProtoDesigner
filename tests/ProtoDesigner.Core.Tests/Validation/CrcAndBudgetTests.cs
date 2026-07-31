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

    [Fact]
    public void Unreferenced_type_reports_PD0060_info()
    {
        var b = new ValidationBuilder();
        b.Prim("Unused", PrimitiveKind.U8);

        var d = b.Run().Has(DiagnosticCodes.UnreferencedType);
        Assert.Equal(Severity.Info, d.Severity);
    }
}
