namespace ProtoDesigner.Core.Tests;

/// <summary>
/// The exact reproduction: add a message, and it must appear in the MessageId enum.
/// </summary>
/// <remarks>
/// This is the call the enum editor makes to fill its member list, so a failure here is what "MessageId
/// is empty" looks like one layer down. It was never wired to the dialog before — the dialog read
/// <c>EnumType.Members</c>, which is empty by design for these types.
/// </remarks>
public class SyntheticEnumMembersTests
{
    private static Bus BusWith(params (string Name, int? WireId)[] messages)
    {
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        foreach (var (name, id) in messages)
            bus.Messages.Add(new Message(MessageId.New(), name) { WireId = id });
        return bus;
    }

    [Fact]
    public void Adding_a_message_adds_it_to_the_message_id_enum()
    {
        var bus = BusWith(("Alpha", 7));

        Assert.Equal(["NotAssigned", "Alpha"],
            SyntheticEnumMembers.For(bus, SyntheticEnum.MessageId).Select(m => m.Name));

        bus.Messages.Add(new Message(MessageId.New(), "Beta") { WireId = 9 });

        Assert.Equal(["NotAssigned", "Alpha", "Beta"],
            SyntheticEnumMembers.For(bus, SyntheticEnum.MessageId).Select(m => m.Name));
    }

    [Fact]
    public void The_value_is_the_wire_id_the_user_typed()
    {
        var members = SyntheticEnumMembers.For(BusWith(("Alpha", 7)), SyntheticEnum.MessageId);

        Assert.Equal(0, members[0].Value);
        Assert.Equal(7, members[1].Value);
    }

    [Fact]
    public void Renaming_a_message_renames_the_member()
    {
        var bus = BusWith(("Alpha", 7));
        bus.Messages[0].Name = "Renamed";

        Assert.Contains(SyntheticEnumMembers.For(bus, SyntheticEnum.MessageId), m => m.Name == "Renamed");
        Assert.DoesNotContain(SyntheticEnumMembers.For(bus, SyntheticEnum.MessageId), m => m.Name == "Alpha");
    }

    [Fact]
    public void A_message_with_no_wire_id_is_left_out()
    {
        // It has nothing to be identified by, so there is no member to make.
        var members = SyntheticEnumMembers.For(BusWith(("Alpha", null)), SyntheticEnum.MessageId);

        Assert.Equal(["NotAssigned"], members.Select(m => m.Name));
    }

    [Fact]
    public void Adding_a_module_adds_it_to_the_module_id_enum()
    {
        var bus = BusWith(("Alpha", 1));
        bus.AddModule("Sensor");

        Assert.Equal(["NotAssigned", "Sensor"],
            SyntheticEnumMembers.For(bus, SyntheticEnum.ModuleId).Select(m => m.Name));

        bus.AddModule("Controller");

        var members = SyntheticEnumMembers.For(bus, SyntheticEnum.ModuleId);
        Assert.Equal(["NotAssigned", "Sensor", "Controller"], members.Select(m => m.Name));
        Assert.Equal([0, 1, 2], members.Select(m => m.Value));
    }
}
