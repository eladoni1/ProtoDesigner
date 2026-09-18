namespace ProtoDesigner.Wpf.Tests;

/// <summary>
/// Every view-model edit reaches the command journal.
/// </summary>
/// <remarks>
/// <para>
/// This is the first automated coverage the WPF layer has had, and it starts here because this is where
/// the layer's only shipped defects were. Renaming the project and changing a bus transport wrote
/// straight to the model, so the journal never saw them and <c>IsDirty</c> stayed false — and
/// <c>MainViewModel.ConfirmDiscardIfDirty</c> returns early when the project is clean, so closing the app
/// discarded the edit with no prompt.
/// </para>
/// <para>
/// The dirty assertion is the point of each case. Undo is worth having; not losing the edit is worth
/// more, and the two are checked by the same flag.
/// </para>
/// </remarks>
public class EditsReachTheJournalTests
{
    private static ProjectViewModel NewProject()
    {
        var vm = new ProjectViewModel(new Project("Test"));
        vm.Journal.MarkSaved();     // a freshly opened project is not dirty
        return vm;
    }

    [Fact]
    public void Renaming_the_project_marks_it_dirty_and_is_undoable()
    {
        var vm = NewProject();

        vm.Name = "Telemetry";

        Assert.Equal("Telemetry", vm.Project.Name);
        Assert.True(vm.IsDirty);

        vm.Journal.Undo();
        Assert.Equal("Test", vm.Project.Name);
    }

    [Fact]
    public void Renaming_the_project_to_the_same_name_changes_nothing()
    {
        var vm = NewProject();

        vm.Name = "Test";

        Assert.False(vm.IsDirty);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void Renaming_the_project_to_blank_is_ignored()
    {
        var vm = NewProject();

        vm.Name = "   ";

        Assert.Equal("Test", vm.Project.Name);
        Assert.False(vm.IsDirty);
    }

    /// <summary>
    /// The transport was left unjournalled as a "rare edit, low risk". The risk was never undo — it was
    /// that the edit never reached disk, and the transport sets the frame budget every message is
    /// measured against.
    /// </summary>
    [Fact]
    public void Changing_a_bus_transport_marks_the_project_dirty_and_is_undoable()
    {
        var vm = NewProject();
        var bus = vm.AddBus("Main", Transport.Ethernet);
        vm.Journal.MarkSaved();

        bus.Transport = Transport.Uart;

        Assert.Equal(Transport.Uart, bus.Bus.Transport);
        Assert.True(vm.IsDirty);

        vm.Journal.Undo();
        Assert.Equal(Transport.Ethernet, bus.Bus.Transport);
    }

    [Fact]
    public void Setting_a_bus_transport_to_what_it_already_is_changes_nothing()
    {
        var vm = NewProject();
        var bus = vm.AddBus("Main", Transport.Ethernet);
        vm.Journal.MarkSaved();

        bus.Transport = Transport.Ethernet;

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void Renaming_a_bus_marks_the_project_dirty_and_is_undoable()
    {
        var vm = NewProject();
        var bus = vm.AddBus("Main");
        vm.Journal.MarkSaved();

        bus.Name = "Primary";

        Assert.Equal("Primary", bus.Bus.Name);
        Assert.True(vm.IsDirty);

        vm.Journal.Undo();
        Assert.Equal("Main", bus.Bus.Name);
    }

    [Fact]
    public void Adding_a_bus_is_journalled()
    {
        var vm = NewProject();

        vm.AddBus("Main");

        Assert.True(vm.IsDirty);
        Assert.Single(vm.Project.Buses);

        vm.Journal.Undo();
        Assert.Empty(vm.Project.Buses);
    }

    [Fact]
    public void Adding_a_message_is_journalled()
    {
        var vm = NewProject();
        var bus = vm.AddBus("Main");
        vm.Journal.MarkSaved();

        bus.AddMessage("Telemetry");

        Assert.True(vm.IsDirty);
        Assert.Single(bus.Bus.Messages);

        vm.Journal.Undo();
        Assert.Empty(bus.Bus.Messages);
    }

    /// <summary>
    /// Saving clears the flag; it does not clear the history. That is what lets someone save, notice the
    /// mistake, and undo it.
    /// </summary>
    [Fact]
    public void Saving_clears_dirty_but_keeps_the_undo_history()
    {
        var vm = NewProject();
        vm.AddBus("Main");

        vm.Journal.MarkSaved();

        Assert.False(vm.IsDirty);
        Assert.True(vm.CanUndo);
    }
}
