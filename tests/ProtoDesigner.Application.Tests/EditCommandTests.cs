namespace ProtoDesigner.Application.Tests;

/// <summary>
/// Covers the editing operations the UI depends on. These are the paths that were previously unreachable
/// from the app — creating a message, renaming a bus, reordering fields — so they get direct tests rather
/// than relying on the UI to exercise them.
/// </summary>
public class EditCommandTests
{
    private static (Project Project, Bus Bus, ParameterType U8) NewProject()
    {
        var project = new Project("Test");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        project.Buses.Add(bus);
        return (project, bus, u8);
    }

    // ---- create / delete -------------------------------------------------------------------------

    [Fact]
    public void Adding_a_message_puts_it_on_the_bus_and_undo_removes_it()
    {
        var (project, bus, _) = NewProject();
        var journal = new CommandJournal(project);
        var message = new Message(MessageId.New(), "Telemetry");

        journal.Do(new AddMessageCommand(bus, message));
        Assert.Single(bus.Messages);
        Assert.True(journal.IsDirty);

        journal.Undo();
        Assert.Empty(bus.Messages);
    }

    [Fact]
    public void Deleting_a_bus_restores_it_at_the_same_index_on_undo()
    {
        var (project, first, _) = NewProject();
        var second = new Bus(BusId.New(), "Second", Transport.Uart);
        var third = new Bus(BusId.New(), "Third", Transport.Uart);
        project.Buses.Add(second);
        project.Buses.Add(third);

        var journal = new CommandJournal(project);
        journal.Do(new RemoveBusCommand(second));

        Assert.Equal(new[] { "Main", "Third" }, project.Buses.Select(b => b.Name));

        journal.Undo();
        Assert.Equal(new[] { "Main", "Second", "Third" }, project.Buses.Select(b => b.Name));
    }

    [Fact]
    public void Removing_a_type_and_undoing_puts_it_back_in_the_library()
    {
        var (project, _, u8) = NewProject();
        var journal = new CommandJournal(project);

        journal.Do(new RemoveTypeCommand(u8));
        Assert.False(project.Types.Contains(u8.Id));

        journal.Undo();
        Assert.True(project.Types.Contains(u8.Id));
    }

    // ---- rename ---------------------------------------------------------------------------------

    [Fact]
    public void Renaming_a_bus_is_undoable()
    {
        var (project, bus, _) = NewProject();
        var journal = new CommandJournal(project);

        journal.Do(new RenameBusCommand(bus, "Powertrain"));
        Assert.Equal("Powertrain", bus.Name);

        journal.Undo();
        Assert.Equal("Main", bus.Name);
    }

    [Fact]
    public void Renaming_a_type_does_not_break_the_fields_that_use_it()
    {
        var (project, bus, u8) = NewProject();
        var message = new Message(MessageId.New(), "M");
        var field = new FieldBinding(FieldId.New(), "count", u8.Id);
        message.Fields.Add(field);
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        journal.Do(new RenameTypeCommand(u8, "byte"));

        // References are by id, so the binding still resolves after the rename.
        Assert.Equal("byte", u8.Name);
        Assert.Equal(u8.Id, field.TypeId);
        Assert.True(project.Types.TryGet(field.TypeId, out var resolved));
        Assert.Equal("byte", resolved!.Name);
    }

    // ---- reorder --------------------------------------------------------------------------------

    [Fact]
    public void Moving_a_field_changes_wire_order_and_undo_restores_it()
    {
        var (project, bus, u8) = NewProject();
        var message = new Message(MessageId.New(), "M");
        var a = new FieldBinding(FieldId.New(), "a", u8.Id);
        var b = new FieldBinding(FieldId.New(), "b", u8.Id);
        var c = new FieldBinding(FieldId.New(), "c", u8.Id);
        message.Fields.Add(a); message.Fields.Add(b); message.Fields.Add(c);
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        journal.Do(new MoveFieldCommand(message, c, 0));

        Assert.Equal(new[] { "c", "a", "b" }, message.Fields.Select(f => f.Name));

        journal.Undo();
        Assert.Equal(new[] { "a", "b", "c" }, message.Fields.Select(f => f.Name));
    }

    [Fact]
    public void Moving_a_field_to_the_end_works()
    {
        var (project, bus, u8) = NewProject();
        var message = new Message(MessageId.New(), "M");
        var a = new FieldBinding(FieldId.New(), "a", u8.Id);
        var b = new FieldBinding(FieldId.New(), "b", u8.Id);
        message.Fields.Add(a); message.Fields.Add(b);
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        journal.Do(new MoveFieldCommand(message, a, 1));

        Assert.Equal(new[] { "b", "a" }, message.Fields.Select(f => f.Name));
    }

    [Fact]
    public void Adding_a_field_at_an_index_inserts_rather_than_appends()
    {
        var (project, bus, u8) = NewProject();
        var message = new Message(MessageId.New(), "M");
        message.Fields.Add(new FieldBinding(FieldId.New(), "a", u8.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "c", u8.Id));
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        journal.Do(new AddFieldCommand(message, new FieldBinding(FieldId.New(), "b", u8.Id), index: 1));

        Assert.Equal(new[] { "a", "b", "c" }, message.Fields.Select(f => f.Name));
    }

    // ---- journal semantics -----------------------------------------------------------------------

    [Fact]
    public void Redo_reapplies_an_undone_edit()
    {
        var (project, bus, _) = NewProject();
        var journal = new CommandJournal(project);
        journal.Do(new AddMessageCommand(bus, new Message(MessageId.New(), "Ping")));

        journal.Undo();
        Assert.Empty(bus.Messages);

        journal.Redo();
        Assert.Single(bus.Messages);
    }

    [Fact]
    public void A_new_edit_clears_the_redo_stack()
    {
        var (project, bus, _) = NewProject();
        var journal = new CommandJournal(project);

        journal.Do(new AddMessageCommand(bus, new Message(MessageId.New(), "First")));
        journal.Undo();
        Assert.True(journal.CanRedo);

        journal.Do(new AddMessageCommand(bus, new Message(MessageId.New(), "Second")));
        Assert.False(journal.CanRedo);
        Assert.Equal("Second", bus.Messages.Single().Name);
    }

    [Fact]
    public void Marking_saved_clears_dirty_but_keeps_history()
    {
        var (project, bus, _) = NewProject();
        var journal = new CommandJournal(project);
        journal.Do(new AddMessageCommand(bus, new Message(MessageId.New(), "Ping")));

        journal.MarkSaved();
        Assert.False(journal.IsDirty);
        Assert.True(journal.CanUndo);
    }

    [Fact]
    public void Changing_an_encoding_is_undoable_as_a_unit()
    {
        var (project, bus, u8) = NewProject();
        var message = new Message(MessageId.New(), "M");
        var field = new FieldBinding(FieldId.New(), "temp", u8.Id);
        message.Fields.Add(field);
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        var updated = new FieldEncoding
        {
            BitWidth = 5,
            AllowBitPacking = true,
            Transform = new ScalarTransform(-40m, 110m / 31m),
        };

        journal.Do(new ChangeEncodingCommand(field, updated));
        Assert.Equal(5, field.Encoding.BitWidth);
        Assert.NotNull(field.Encoding.Transform);

        journal.Undo();
        Assert.Null(field.Encoding.BitWidth);
        Assert.Null(field.Encoding.Transform);
    }

    // ---- modules and routes ----------------------------------------------------------------------

    [Fact]
    public void Adding_a_module_and_a_route_undoes_cleanly()
    {
        var (project, bus, u8) = NewProject();
        var message = new Message(MessageId.New(), "Telemetry");
        message.Fields.Add(new FieldBinding(FieldId.New(), "x", u8.Id));
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        var sensor = new Module("Sensor");
        var controller = new Module("Controller");

        journal.Do(new AddModuleCommand(bus, sensor));
        journal.Do(new AddModuleCommand(bus, controller));
        journal.Do(new AddRouteCommand(message, new MessageRoute(sensor.Id, controller.Id)));

        Assert.Equal(2, bus.Modules.Count);
        Assert.Single(message.Routes);

        journal.Undo();
        Assert.Empty(message.Routes);

        journal.Redo();
        Assert.Single(message.Routes);
        Assert.Equal(sensor.Id, message.Routes[0].From);
    }

    // Routes have to travel with the module, or the ones left behind name a module that is gone.
    [Fact]
    public void Removing_a_module_takes_its_routes_and_undo_brings_both_back()
    {
        var (project, bus, u8) = NewProject();
        var sensor = bus.AddModule("Sensor");
        var controller = bus.AddModule("Controller");
        var logger = bus.AddModule("Logger");

        var message = new Message(MessageId.New(), "Telemetry");
        message.Fields.Add(new FieldBinding(FieldId.New(), "x", u8.Id));
        message.Routes.Add(new MessageRoute(sensor.Id, controller.Id));
        message.Routes.Add(new MessageRoute(sensor.Id, logger.Id));
        message.Routes.Add(new MessageRoute(logger.Id, controller.Id));
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        journal.Do(new RemoveModuleCommand(bus, logger));

        Assert.Equal(2, bus.Modules.Count);
        Assert.Single(message.Routes);
        Assert.Equal(controller.Id, message.Routes[0].To);

        journal.Undo();

        Assert.Equal(3, bus.Modules.Count);
        Assert.Equal(2, bus.Modules.IndexOf(logger));   // restored at its original position
        Assert.Equal(3, message.Routes.Count);
        Assert.Equal(new MessageRoute(sensor.Id, controller.Id), message.Routes[0]);
        Assert.Equal(new MessageRoute(sensor.Id, logger.Id), message.Routes[1]);
        Assert.Equal(new MessageRoute(logger.Id, controller.Id), message.Routes[2]);
    }

    [Fact]
    public void Changing_one_endpoint_of_a_route_replaces_it_in_place()
    {
        var (project, bus, u8) = NewProject();
        var sensor = bus.AddModule("Sensor");
        var controller = bus.AddModule("Controller");
        var logger = bus.AddModule("Logger");

        var message = new Message(MessageId.New(), "Telemetry");
        message.Fields.Add(new FieldBinding(FieldId.New(), "x", u8.Id));
        message.Routes.Add(new MessageRoute(sensor.Id, controller.Id));
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        journal.Do(new ChangeRouteCommand(message, 0, new MessageRoute(sensor.Id, logger.Id)));

        Assert.Equal(logger.Id, message.Routes[0].To);

        journal.Undo();
        Assert.Equal(controller.Id, message.Routes[0].To);
    }

    // Identity is the whole reason modules are not plain strings.
    [Fact]
    public void Renaming_a_module_keeps_every_route_pointing_at_it()
    {
        var (project, bus, u8) = NewProject();
        var sensor = bus.AddModule("Sensor");
        var controller = bus.AddModule("Controller");

        var message = new Message(MessageId.New(), "Telemetry");
        message.Fields.Add(new FieldBinding(FieldId.New(), "x", u8.Id));
        message.Routes.Add(new MessageRoute(sensor.Id, controller.Id));
        bus.Messages.Add(message);

        var journal = new CommandJournal(project);
        journal.Do(new RenameModuleCommand(sensor, "TempSensor"));

        Assert.Equal("TempSensor", bus.FindModule(message.Routes[0].From)!.Name);

        journal.Undo();
        Assert.Equal("Sensor", bus.FindModule(message.Routes[0].From)!.Name);
    }
}
