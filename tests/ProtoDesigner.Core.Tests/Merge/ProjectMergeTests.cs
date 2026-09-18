using ProtoDesigner.Core.Merge;

namespace ProtoDesigner.Core.Tests.Merge;

/// <summary>
/// Two people editing one project, merged by identity rather than by line.
/// </summary>
/// <remarks>
/// <para>
/// Every case builds the same project three times from fixed ids — baseline, local, remote — the way
/// three checkouts of one file would be. That shared identity is what makes the assertions mean anything:
/// a finding is about the change, not about two models that were never the same thing.
/// </para>
/// <para>
/// The first case is the one that motivated all of this. Two branches each adding a different message to
/// one bus <em>conflict under git</em>, verified by running the real merge, because both insertions land
/// at the same textual anchor. Here it is the trivial case it always was.
/// </para>
/// </remarks>
public class ProjectMergeTests
{
    private static readonly BusId TheBus = new(Guid.Parse("b0000000-0000-0000-0000-000000000001"));
    private static readonly MessageId First = new(Guid.Parse("d0000000-0000-0000-0000-000000000001"));
    private static readonly TypeId U8 = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
    private static readonly FieldId Mode = new(Guid.Parse("f0000000-0000-0000-0000-000000000001"));
    private static readonly FieldId Level = new(Guid.Parse("f0000000-0000-0000-0000-000000000002"));

    /// <summary>One bus, one message, two fields — identical every time it is called.</summary>
    private static Project Build()
    {
        var project = new Project("Telemetry");
        project.Types.Add(new ParameterType(U8, "u8", PrimitiveKind.U8));

        var message = new Message(First, "Status") { WireId = 1 };
        message.Fields.Add(new FieldBinding(Mode, "mode", U8));
        message.Fields.Add(new FieldBinding(Level, "level", U8));

        var bus = new Bus(TheBus, "Main", Transport.Ethernet);
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return project;
    }

    private static Message MessageOf(Project p) => p.Buses[0].Messages[0];

    private static Message NewMessage(string name, int wireId, Guid id)
    {
        var m = new Message(new MessageId(id), name) { WireId = wireId };
        m.Fields.Add(new FieldBinding(FieldId.New(), "payload", U8));
        return m;
    }

    // ---- the case git cannot do -------------------------------------------------------------------

    [Fact]
    public void Two_people_adding_a_different_message_each_merge_cleanly()
    {
        var baseline = Build();
        var local = Build();
        var remote = Build();

        local.Buses[0].Messages.Add(NewMessage("Alice", 10, Guid.Parse("d0000000-0000-0000-0000-0000000000a1")));
        remote.Buses[0].Messages.Add(NewMessage("Bob", 11, Guid.Parse("d0000000-0000-0000-0000-0000000000b1")));

        var result = ProjectMerge.Merge(baseline, local, remote);

        Assert.True(result.Succeeded, string.Join("; ", result.Conflicts));
        Assert.Equal(new[] { "Status", "Alice", "Bob" }, local.Buses[0].Messages.Select(m => m.Name));
    }

    [Fact]
    public void An_unchanged_pair_merges_to_nothing()
    {
        var local = Build();

        var result = ProjectMerge.Merge(Build(), local, Build());

        Assert.True(result.Succeeded);
        Assert.Empty(result.Applied);
    }

    // ---- one side moved ---------------------------------------------------------------------------

    [Fact]
    public void A_remote_rename_is_taken()
    {
        var local = Build();
        var remote = Build();
        MessageOf(remote).Name = "Health";

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded);
        Assert.Equal("Health", MessageOf(local).Name);
    }

    [Fact]
    public void A_local_rename_is_kept()
    {
        var local = Build();
        MessageOf(local).Name = "Health";

        var result = ProjectMerge.Merge(Build(), local, Build());

        Assert.True(result.Succeeded);
        Assert.Equal("Health", MessageOf(local).Name);
    }

    /// <summary>
    /// People reach the same edit independently more often than is comfortable; stopping them would be
    /// noise rather than safety.
    /// </summary>
    [Fact]
    public void The_same_change_on_both_sides_is_not_a_conflict()
    {
        var local = Build();
        var remote = Build();
        MessageOf(local).Name = "Health";
        MessageOf(remote).Name = "Health";

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded);
        Assert.Equal("Health", MessageOf(local).Name);
    }

    [Fact]
    public void Renaming_different_things_never_meets()
    {
        // Identity is an id, so a rename is a property change rather than a delete plus an add.
        var local = Build();
        var remote = Build();
        MessageOf(local).Fields[0].Name = "operatingMode";
        remote.Buses[0].Name = "Backbone";

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded, string.Join("; ", result.Conflicts));
        Assert.Equal("operatingMode", MessageOf(local).Fields[0].Name);
        Assert.Equal("Backbone", local.Buses[0].Name);
    }

    [Fact]
    public void Two_people_editing_different_fields_of_one_message_merge_cleanly()
    {
        // Fields are entities in their own right, so a message is not one lock.
        var local = Build();
        var remote = Build();
        MessageOf(local).Fields[0].Encoding.BitWidth = 4;
        MessageOf(remote).Fields[1].Encoding.BitWidth = 4;

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded, string.Join("; ", result.Conflicts));
        Assert.Equal(4, MessageOf(local).Fields[0].Encoding.BitWidth);
        Assert.Equal(4, MessageOf(local).Fields[1].Encoding.BitWidth);
    }

    // ---- both sides moved -------------------------------------------------------------------------

    [Fact]
    public void The_same_message_changed_differently_is_a_conflict()
    {
        var local = Build();
        var remote = Build();
        MessageOf(local).WireId = 2;
        MessageOf(remote).WireId = 3;

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Conflicts, c => c.Code == MergeCodes.MessageChangedOnBothSides);

        // Nothing applied: a half-merged working copy is a state neither person chose.
        Assert.Equal(2, MessageOf(local).WireId);
    }

    [Fact]
    public void The_same_field_changed_differently_is_a_conflict()
    {
        var local = Build();
        var remote = Build();
        MessageOf(local).Fields[0].Encoding.BitWidth = 4;
        MessageOf(remote).Fields[0].Encoding.BitWidth = 2;

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.Contains(result.Conflicts, c => c.Code == MergeCodes.FieldChangedOnBothSides);
    }

    [Fact]
    public void Removed_on_one_side_and_changed_on_the_other_is_a_conflict()
    {
        var local = Build();
        var remote = Build();
        local.Buses[0].Messages.Clear();
        MessageOf(remote).Name = "Health";

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.Contains(result.Conflicts, c => c.Code == MergeCodes.MessageChangedOnBothSides);
    }

    [Fact]
    public void A_removal_nobody_else_touched_is_taken()
    {
        var local = Build();
        var remote = Build();
        remote.Buses[0].Messages.Clear();

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded);
        Assert.Empty(local.Buses[0].Messages);
    }

    // ---- field order, the one it must refuse -------------------------------------------------------

    /// <summary>
    /// The conflict worth having. Both results are valid protocols and they are different ones: appending
    /// one field each gives a layout neither person designed, and every check downstream would call it
    /// correct.
    /// </summary>
    [Fact]
    public void Both_sides_appending_a_field_to_one_message_is_a_conflict()
    {
        var local = Build();
        var remote = Build();
        MessageOf(local).Fields.Add(new FieldBinding(FieldId.New(), "fromAlice", U8));
        MessageOf(remote).Fields.Add(new FieldBinding(FieldId.New(), "fromBob", U8));

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.Contains(result.Conflicts, c => c.Code == MergeCodes.FieldOrderChangedOnBothSides);
        Assert.Equal(3, MessageOf(local).Fields.Count);   // untouched
    }

    [Fact]
    public void Both_sides_reordering_the_same_message_is_a_conflict()
    {
        var local = Build();
        var remote = Build();
        var lf = MessageOf(local).Fields;
        (lf[0], lf[1]) = (lf[1], lf[0]);
        MessageOf(remote).Fields.Add(new FieldBinding(FieldId.New(), "extra", U8));

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.Contains(result.Conflicts, c => c.Code == MergeCodes.FieldOrderChangedOnBothSides);
    }

    [Fact]
    public void One_side_appending_a_field_is_taken()
    {
        var local = Build();
        var remote = Build();
        MessageOf(remote).Fields.Add(new FieldBinding(FieldId.New(), "extra", U8));

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded, string.Join("; ", result.Conflicts));
        Assert.Equal(new[] { "mode", "level", "extra" }, MessageOf(local).Fields.Select(f => f.Name));
    }

    /// <summary>
    /// A remote reorder carries local property edits with it: the list comes from the remote side, the
    /// fields themselves stay the objects the local side was editing.
    /// </summary>
    [Fact]
    public void A_remote_reorder_keeps_local_edits_to_the_fields_that_moved()
    {
        var local = Build();
        var remote = Build();
        MessageOf(local).Fields[1].Name = "batteryLevel";

        var rf = MessageOf(remote).Fields;
        (rf[0], rf[1]) = (rf[1], rf[0]);

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded, string.Join("; ", result.Conflicts));
        Assert.Equal(new[] { Level, Mode }, MessageOf(local).Fields.Select(f => f.Id));
        Assert.Equal("batteryLevel", MessageOf(local).Fields[0].Name);
    }

    // ---- types and buses ---------------------------------------------------------------------------

    [Fact]
    public void A_remote_type_change_is_taken()
    {
        var local = Build();
        var remote = Build();
        remote.Types.All.OfType<ParameterType>().Single().Range = new NumericRange(0, 100);

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded);
        Assert.Equal(new NumericRange(0, 100), local.Types.All.OfType<ParameterType>().Single().Range);
    }

    [Fact]
    public void The_same_type_changed_differently_is_a_conflict()
    {
        var local = Build();
        var remote = Build();
        local.Types.All.OfType<ParameterType>().Single().Range = new NumericRange(0, 100);
        remote.Types.All.OfType<ParameterType>().Single().Range = new NumericRange(0, 200);

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.Contains(result.Conflicts, c => c.Code == MergeCodes.TypeChangedOnBothSides);
    }

    [Fact]
    public void A_remote_bus_is_added()
    {
        var local = Build();
        var remote = Build();
        var second = new Bus(new BusId(Guid.Parse("b0000000-0000-0000-0000-000000000002")),
                             "Secondary", Transport.Uart);
        remote.Buses.Add(second);

        var result = ProjectMerge.Merge(Build(), local, remote);

        Assert.True(result.Succeeded, string.Join("; ", result.Conflicts));
        Assert.Equal(new[] { "Main", "Secondary" }, local.Buses.Select(b => b.Name));
    }

    // ---- the validation gate -----------------------------------------------------------------------

    /// <summary>
    /// The trap a clean merge walks into. Two people each add a message with wire id 10: they touched
    /// different entities, so nothing conflicts, and the result is a bus no receiver can decode.
    /// </summary>
    [Fact]
    public void A_clean_merge_that_collides_wire_ids_is_reported_as_invalid()
    {
        var baseline = Build();
        var local = Build();
        var remote = Build();

        local.Buses[0].Messages.Add(NewMessage("Alice", 10, Guid.Parse("d0000000-0000-0000-0000-0000000000a1")));
        remote.Buses[0].Messages.Add(NewMessage("Bob", 10, Guid.Parse("d0000000-0000-0000-0000-0000000000b1")));

        var result = ProjectMerge.Merge(baseline, local, remote);

        Assert.Empty(result.Conflicts);          // nothing to conflict over — different entities
        Assert.False(result.Succeeded);          // and yet the result is not shippable
        Assert.Contains(result.Validation, d => d.Code == DiagnosticCodes.DuplicateWireId);
    }

    // ---- all or nothing ----------------------------------------------------------------------------

    /// <summary>
    /// A merge that conflicts anywhere applies nothing, including the parts that were perfectly clean.
    /// </summary>
    /// <remarks>
    /// The alternative is worse than it sounds. A half-applied merge leaves the working copy holding a
    /// mixture neither person wrote: the conflicted entity is still the local one, the clean entities are
    /// already the remote ones, and nothing on screen says which is which. Undo cannot describe it either,
    /// because the journal never saw it. So the whole merge is planned first and written only if it is
    /// clean — this pins that, with one conflicting message beside one that would otherwise merge.
    /// </remarks>
    [Fact]
    public void A_conflict_anywhere_leaves_the_local_copy_completely_untouched()
    {
        var baseline = Build();
        var local = Build();
        var remote = Build();

        // Clean on its own: only the remote side adds this message, so a merge would take it.
        remote.Buses[0].Messages.Add(NewMessage("Bob", 11, Guid.Parse("d0000000-0000-0000-0000-0000000000b1")));

        // And a conflict beside it, in a sibling the clean step does not depend on: both sides rename the
        // same field, differently. A bus- or message-level conflict would not prove anything here, since
        // those stop the merge descending and the clean step would never have been planned at all.
        MessageOf(local).Fields[0].Name = "left";
        MessageOf(remote).Fields[0].Name = "right";

        var result = ProjectMerge.Merge(baseline, local, remote);

        Assert.False(result.Succeeded);
        Assert.Empty(result.Applied);

        // The clean addition did not land, and the local rename still stands.
        Assert.Equal(new[] { "Status" }, local.Buses[0].Messages.Select(m => m.Name));
        Assert.Equal("left", MessageOf(local).Fields[0].Name);
    }
}
