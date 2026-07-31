namespace ProtoDesigner.Core.Tests.Validation;

public class DuplicateNameTests
{
    [Fact]
    public void Duplicate_bus_name_reports_PD0003()
    {
        var b = new ValidationBuilder();
        b.Project.Buses.Add(new Bus("TestBus", Transport.Uart));

        b.Run().Has(DiagnosticCodes.DuplicateBusName);
    }

    [Fact]
    public void A_single_bus_produces_no_duplicate_bus_diagnostic()
    {
        var b = new ValidationBuilder();
        b.Run().HasNo(DiagnosticCodes.DuplicateBusName);
    }

    [Fact]
    public void Duplicate_message_name_reports_PD0002()
    {
        var b = new ValidationBuilder();
        b.NewMessage("Ping");
        b.NewMessage("Ping");

        b.Run().Has(DiagnosticCodes.DuplicateMessageName);
    }

    [Fact]
    public void Same_message_name_on_different_buses_is_fine()
    {
        var b = new ValidationBuilder();
        b.NewMessage("Ping");

        var second = new Bus("Second", Transport.Ethernet);
        second.Messages.Add(new Message("Ping"));
        b.Project.Buses.Add(second);

        b.Run().HasNo(DiagnosticCodes.DuplicateMessageName);
    }

    [Fact]
    public void Duplicate_type_name_reports_PD0005_as_a_warning()
    {
        var b = new ValidationBuilder();
        b.Prim("Byte", PrimitiveKind.U8);
        b.Prim("Byte", PrimitiveKind.U8);

        var diagnostic = b.Run().Has(DiagnosticCodes.DuplicateTypeName);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void Duplicate_wire_id_within_a_bus_reports_PD0004()
    {
        var b = new ValidationBuilder();
        b.NewMessage("A").WireId = 7;
        b.NewMessage("B").WireId = 7;

        b.Run().Has(DiagnosticCodes.DuplicateWireId);
    }

    [Fact]
    public void Distinct_wire_ids_are_fine()
    {
        var b = new ValidationBuilder();
        b.NewMessage("A").WireId = 1;
        b.NewMessage("B").WireId = 2;

        b.Run().HasNo(DiagnosticCodes.DuplicateWireId);
    }

    [Fact]
    public void Duplicate_field_name_in_a_message_reports_PD0001()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.NewMessage("M", ValidationBuilder.F("x", u8), ValidationBuilder.F("x", u8));

        b.Run().Has(DiagnosticCodes.DuplicateFieldName);
    }

    [Fact]
    public void Duplicate_member_name_in_a_struct_reports_PD0001()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.Struct("Duo", ValidationBuilder.F("x", u8), ValidationBuilder.F("x", u8));

        b.Run().Has(DiagnosticCodes.DuplicateFieldName);
    }
}
