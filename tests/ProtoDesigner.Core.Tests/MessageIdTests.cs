namespace ProtoDesigner.Core.Tests;

/// <summary>
/// Message id allocation. Ids are scoped to a bus and 0 is reserved to mean "not assigned", so the
/// allocator must never return it — a message holding 0 would be indistinguishable from the generated
/// id enum's <c>NotAssigned</c> sentinel.
/// </summary>
public class MessageIdTests
{
    private static Bus NewBus() => new(BusId.New(), "Main", Transport.Ethernet);

    private static Message WithId(string name, int? id) =>
        new(MessageId.New(), name) { WireId = id };

    [Fact]
    public void The_first_message_on_a_bus_gets_id_one_not_zero()
    {
        var bus = NewBus();
        Assert.Equal(1, bus.NextMessageId());
    }

    [Fact]
    public void Ids_are_handed_out_in_ascending_order()
    {
        var bus = NewBus();

        bus.Messages.Add(WithId("A", bus.NextMessageId()));
        bus.Messages.Add(WithId("B", bus.NextMessageId()));
        bus.Messages.Add(WithId("C", bus.NextMessageId()));

        Assert.Equal(new int?[] { 1, 2, 3 }, bus.Messages.Select(m => m.WireId).ToArray());
    }

    /// <summary>Deleting a message frees its id, so the next one fills the hole rather than growing.</summary>
    [Fact]
    public void A_gap_is_reused()
    {
        var bus = NewBus();
        bus.Messages.Add(WithId("A", 1));
        bus.Messages.Add(WithId("C", 3));

        Assert.Equal(2, bus.NextMessageId());
    }

    // Someone may have typed 0 by hand before the rule existed; the allocator must still skip it.
    [Fact]
    public void An_existing_zero_does_not_make_the_allocator_return_zero()
    {
        var bus = NewBus();
        bus.Messages.Add(WithId("Legacy", 0));

        Assert.Equal(1, bus.NextMessageId());
    }

    [Fact]
    public void Messages_without_an_id_do_not_reserve_one()
    {
        var bus = NewBus();
        bus.Messages.Add(WithId("A", null));
        bus.Messages.Add(WithId("B", null));

        Assert.Equal(1, bus.NextMessageId());
    }

    // The whole point of per-bus scoping: what another bus uses is none of this bus's business.
    [Fact]
    public void Ids_on_another_bus_do_not_affect_this_one()
    {
        var project = new Project("P");
        var first = NewBus();
        var second = new Bus(BusId.New(), "Second", Transport.Uart);
        project.Buses.Add(first);
        project.Buses.Add(second);

        first.Messages.Add(WithId("A", 1));
        first.Messages.Add(WithId("B", 2));

        Assert.Equal(1, second.NextMessageId());
    }
}
