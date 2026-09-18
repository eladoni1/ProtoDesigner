using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Application.Tests;

/// <summary>
/// Filling in the message ids nobody typed.
/// </summary>
/// <remarks>
/// The thing worth guarding is what this does <em>not</em> do. A wire id is what a deployed peer matches
/// on, so an id that already exists is never moved — renumbering would break every such peer silently,
/// because the frame still arrives and is simply not recognised. Filling gaps is a convenience; rewriting
/// the space is a wire change and stays the user's to make.
/// </remarks>
public class AssignWireIdsTests
{
    private static (Project Project, Bus Bus) NewBus(params int?[] ids)
    {
        var project = new Project("Test");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);

        for (var i = 0; i < ids.Length; i++)
        {
            var message = new Message(MessageId.New(), $"M{i}") { WireId = ids[i] };
            message.Fields.Add(new FieldBinding(FieldId.New(), "value", u8.Id));
            bus.Messages.Add(message);
        }

        project.Buses.Add(bus);
        return (project, bus);
    }

    private static int?[] Ids(Bus bus) => bus.Messages.Select(m => m.WireId).ToArray();

    [Fact]
    public void Every_message_without_an_id_gets_the_lowest_free_one()
    {
        var (project, bus) = NewBus(null, null, null);
        var journal = new CommandJournal(project);

        journal.Do(new AssignWireIdsCommand());

        Assert.Equal(new int?[] { 1, 2, 3 }, Ids(bus));
    }

    /// <summary>The whole point: an assigned id is a contract, and this fills around it.</summary>
    [Fact]
    public void An_existing_id_is_never_moved()
    {
        var (project, bus) = NewBus(null, 7, null, 1);
        var journal = new CommandJournal(project);

        journal.Do(new AssignWireIdsCommand());

        // 1 and 7 were taken, so the gaps get 2 and 3 — and the two declared ids are exactly where
        // they were.
        Assert.Equal(new int?[] { 2, 7, 3, 1 }, Ids(bus));
    }

    /// <summary>Zero means "not assigned", so it is a gap rather than a value to preserve.</summary>
    [Fact]
    public void Id_zero_counts_as_unassigned()
    {
        var (project, bus) = NewBus(0, 4);
        var journal = new CommandJournal(project);

        journal.Do(new AssignWireIdsCommand());

        Assert.Equal(new int?[] { 1, 4 }, Ids(bus));
    }

    [Fact]
    public void Ids_are_scoped_to_their_own_bus()
    {
        // A receiver matches within one bus, which is also the scope PD0004 checks, so two buses may
        // legitimately both use id 1.
        var (project, first) = NewBus(null, null);
        var second = new Bus(BusId.New(), "Second", Transport.Uart);
        second.Messages.Add(new Message(MessageId.New(), "Other"));
        project.Buses.Add(second);

        new CommandJournal(project).Do(new AssignWireIdsCommand());

        Assert.Equal(new int?[] { 1, 2 }, Ids(first));
        Assert.Equal(new int?[] { 1 }, Ids(second));
    }

    [Fact]
    public void Undo_restores_every_id_including_the_nulls()
    {
        var (project, bus) = NewBus(null, 7, 0);
        var journal = new CommandJournal(project);

        journal.Do(new AssignWireIdsCommand());
        Assert.Equal(new int?[] { 1, 7, 2 }, Ids(bus));

        journal.Undo();

        Assert.Equal(new int?[] { null, 7, 0 }, Ids(bus));
    }

    [Fact]
    public void Assigning_marks_the_project_dirty()
    {
        // The array, not a one-element array, is what a bare `null` binds to here — hence the explicit form.
        var (project, _) = NewBus(new int?[] { null });
        var journal = new CommandJournal(project);
        journal.MarkSaved();

        journal.Do(new AssignWireIdsCommand());

        Assert.True(journal.IsDirty);
    }

    [Fact]
    public void HasUnassigned_lets_a_caller_skip_a_no_op()
    {
        Assert.True(AssignWireIdsCommand.HasUnassigned(NewBus(null, 2).Project));
        Assert.True(AssignWireIdsCommand.HasUnassigned(NewBus(0, 2).Project));
        Assert.False(AssignWireIdsCommand.HasUnassigned(NewBus(1, 2).Project));
        Assert.False(AssignWireIdsCommand.HasUnassigned(new Project("Empty")));
    }

    /// <summary>
    /// The result has to satisfy the rules that made it worth assigning: no zero, no duplicates.
    /// </summary>
    [Fact]
    public void The_result_passes_the_validator_rules_it_exists_to_clear()
    {
        var (project, _) = NewBus(null, 0, null, 3);

        new CommandJournal(project).Do(new AssignWireIdsCommand());

        var diagnostics = new Validator().Validate(project);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.WireIdNotAssigned);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.DuplicateWireId);
    }
}
