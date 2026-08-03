using ProtoDesigner.Core.Ir;

namespace ProtoDesigner.Application.Tests;

/// <summary>
/// Choosing what to generate: a whole project, one bus, a subset of its messages, or everything a module
/// touches. The subset cases matter most — asking for one message must not drag its neighbours' types
/// into the output.
/// </summary>
public class GenerationScopeTests
{
    /// <summary>
    /// Two buses. Sensor sits on both; Controller and Logger sit on one each. Alpha and Gamma involve
    /// Sensor, Beta does not.
    /// </summary>
    private static (Project Project, Bus Main, Bus Aux, Module Sensor) Sample()
    {
        var project = new Project("Scopes");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U8)
            .With("Idle", 0).With("Busy", 1));

        var main = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var sensor = main.AddModule("Sensor");
        var controller = main.AddModule("Controller");

        var alpha = new Message(MessageId.New(), "Alpha") { WireId = 1 };
        alpha.Fields.Add(new FieldBinding(FieldId.New(), "x", u8.Id));
        alpha.Routes.Add(new MessageRoute(sensor.Id, controller.Id));

        // Beta is the only user of the Mode enum, and Sensor has nothing to do with it.
        var beta = new Message(MessageId.New(), "Beta") { WireId = 2 };
        beta.Fields.Add(new FieldBinding(FieldId.New(), "mode", mode.Id));
        beta.Routes.Add(new MessageRoute(controller.Id, controller.Id));

        main.Messages.Add(alpha);
        main.Messages.Add(beta);

        var aux = new Bus(BusId.New(), "Aux", Transport.Uart);
        var sensorOnAux = aux.AddModule("Sensor");   // same box, second bus: a separate Module object
        var logger = aux.AddModule("Logger");

        var gamma = new Message(MessageId.New(), "Gamma") { WireId = 1 };   // ids restart per bus
        gamma.Fields.Add(new FieldBinding(FieldId.New(), "y", u8.Id));
        gamma.Routes.Add(new MessageRoute(sensorOnAux.Id, logger.Id));
        aux.Messages.Add(gamma);

        project.Buses.Add(main);
        project.Buses.Add(aux);
        return (project, main, aux, sensor);
    }

    [Fact]
    public void The_whole_project_covers_every_bus()
    {
        var (project, _, _, _) = Sample();

        var scopes = GenerationScopes.ForProject(project);

        Assert.Equal(2, scopes.Count);
        Assert.Equal(3, scopes.Sum(s => s.Messages.Count));
    }

    [Fact]
    public void One_bus_covers_only_its_own_messages()
    {
        var (_, main, _, _) = Sample();

        var scope = Assert.Single(GenerationScopes.ForBus(main));

        Assert.Equal(new[] { "Alpha", "Beta" }, scope.Messages.Select(m => m.Name));
    }

    [Fact]
    public void A_subset_keeps_the_buses_own_order()
    {
        var (_, main, _, _) = Sample();
        var reversed = main.Messages.Select(m => m.Id).Reverse();

        var scope = Assert.Single(GenerationScopes.ForMessages(main, reversed));

        // Asked for them backwards; generating the same selection twice must produce the same file.
        Assert.Equal(new[] { "Alpha", "Beta" }, scope.Messages.Select(m => m.Name));
    }

    [Fact]
    public void An_empty_selection_yields_no_scope_rather_than_an_empty_one()
    {
        var (_, main, _, _) = Sample();

        Assert.Empty(GenerationScopes.ForMessages(main, Array.Empty<MessageId>()));
    }

    // ---- modules ---------------------------------------------------------------------------------

    [Fact]
    public void A_module_covers_the_messages_it_sends_and_the_ones_it_receives()
    {
        var (project, _, _, sensor) = Sample();

        var scope = Assert.Single(GenerationScopes.ForModule(project, sensor.Id));

        // Alpha involves Sensor; Beta is Controller-only.
        Assert.Equal(new[] { "Alpha" }, scope.Messages.Select(m => m.Name));
    }

    /// <summary>
    /// The "give me everything for module X" case: one build covering every bus that module sits on.
    /// </summary>
    [Fact]
    public void A_module_named_across_two_buses_yields_a_scope_for_each()
    {
        var (project, _, _, _) = Sample();

        var scopes = GenerationScopes.ForModuleNamed(project, "Sensor");

        Assert.Equal(2, scopes.Count);
        Assert.Equal(new[] { "Main", "Aux" }, scopes.Select(s => s.Bus.Name));
        Assert.Equal(new[] { "Alpha" }, scopes[0].Messages.Select(m => m.Name));
        Assert.Equal(new[] { "Gamma" }, scopes[1].Messages.Select(m => m.Name));
    }

    [Fact]
    public void A_module_on_one_bus_only_yields_one_scope()
    {
        var (project, _, _, _) = Sample();

        var scope = Assert.Single(GenerationScopes.ForModuleNamed(project, "Logger"));

        Assert.Equal("Aux", scope.Bus.Name);
    }

    [Fact]
    public void A_module_that_no_route_mentions_yields_nothing()
    {
        var (project, main, _, _) = Sample();
        main.AddModule("Spare");

        Assert.Empty(GenerationScopes.ForModuleNamed(project, "Spare"));
    }

    [Fact]
    public void An_unknown_module_name_yields_nothing()
    {
        var (project, _, _, _) = Sample();

        Assert.Empty(GenerationScopes.ForModuleNamed(project, "Nope"));
    }

    [Fact]
    public void Module_names_are_listed_once_across_buses()
    {
        var (project, _, _, _) = Sample();

        // Sensor is on both buses but is one name to pick from.
        Assert.Equal(new[] { "Controller", "Logger", "Sensor" }, GenerationScopes.ModuleNames(project));
    }

    // ---- what the filter does to the IR ----------------------------------------------------------

    /// <summary>
    /// Narrowing to one message must narrow the type tables too. Beta is the only user of the Mode enum,
    /// so a build of Alpha alone has no business declaring it.
    /// </summary>
    [Fact]
    public void Filtering_to_a_subset_drops_the_types_only_the_others_used()
    {
        var (project, main, _, _) = Sample();
        var alpha = main.Messages.Single(m => m.Name == "Alpha");

        var full = new IrBuilder().Build(project, main);
        var narrowed = new IrBuilder().Build(project, main, new HashSet<MessageId> { alpha.Id });

        Assert.Equal(2, full.Messages.Count);
        Assert.Single(full.Enums);

        Assert.Single(narrowed.Messages);
        Assert.Equal("Alpha", narrowed.Messages[0].Name);
        Assert.Empty(narrowed.Enums);
    }

    [Fact]
    public void A_null_filter_still_means_everything()
    {
        var (project, main, _, _) = Sample();

        var ir = new IrBuilder().Build(project, main, only: null);

        Assert.Equal(2, ir.Messages.Count);
    }
}
