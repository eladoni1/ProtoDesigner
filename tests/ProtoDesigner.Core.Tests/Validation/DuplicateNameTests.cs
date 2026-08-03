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

    // Ids are scoped to a bus, so the same number on two buses is not a collision — it is the norm.
    [Fact]
    public void The_same_wire_id_on_two_buses_is_not_a_duplicate()
    {
        var b = new ValidationBuilder();
        b.NewMessage("A").WireId = 1;

        var other = new Bus(BusId.New(), "Other", Transport.Ethernet);
        other.Messages.Add(new Message(MessageId.New(), "B") { WireId = 1 });
        b.Project.Buses.Add(other);

        b.Run().HasNo(DiagnosticCodes.DuplicateWireId);
    }

    // 0 is reserved for "not assigned", so it must never identify a real message: the generated
    // <Bus>MessageId enum returns NotAssigned = 0 for an id it does not recognise, and a message
    // holding 0 would be indistinguishable from that.
    [Fact]
    public void Wire_id_zero_reports_PD0063()
    {
        var b = new ValidationBuilder();
        b.NewMessage("A").WireId = 0;

        var d = b.Run().Has(DiagnosticCodes.WireIdNotAssigned);
        Assert.Equal(Severity.Error, d.Severity);
    }

    [Fact]
    public void The_lowest_valid_wire_id_is_one()
    {
        var b = new ValidationBuilder();
        b.NewMessage("A").WireId = 1;

        b.Run().HasNo(DiagnosticCodes.WireIdNotAssigned);
    }

    // A message that simply has no id yet is not the same as one claiming id 0.
    [Fact]
    public void An_unset_wire_id_is_not_reported()
    {
        var b = new ValidationBuilder();
        b.NewMessage("A").WireId = null;

        b.Run().HasNo(DiagnosticCodes.WireIdNotAssigned);
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
