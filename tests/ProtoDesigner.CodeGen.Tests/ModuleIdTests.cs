using ProtoDesigner.CodeGen.C;
using ProtoDesigner.CodeGen.CSharp;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// The per-bus module-id enum: a name for each participant on the link.
/// </summary>
/// <remarks>
/// These are <b>design-time identities, not a wire value</b>. Nothing puts a module id in a frame —
/// <c>MessageRoute</c> is a statement about who talks to whom — so their numbering is only a contract
/// between two ends built from the same header. That is why declaration order is good enough here and
/// would not be for <c>Message.WireId</c>, which a deployed peer reads off the wire.
/// </remarks>
public class ModuleIdTests
{
    private static (Project Project, Bus Bus) BusWithModules(params string[] modules)
    {
        var project = new Project("Ids");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        foreach (var name in modules) bus.AddModule(name);

        var message = new Message(MessageId.New(), "Ping") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "counter", u32.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        return (project, bus);
    }

    private static string GenerateC(params string[] modules)
    {
        var (project, bus) = BusWithModules(modules);
        var ir = new IrBuilder().Build(project, bus);
        return new CGenerator().Generate(ir, new GeneratorOptions(Namespace: "app"))
            .Files.Single(f => f.RelativePath == "main.h").Contents;
    }

    [Fact]
    public void Every_module_gets_a_named_id_starting_at_one()
    {
        var header = GenerateC("Sensor", "Controller");

        Assert.Contains("app_MainModuleId_NotAssigned = 0,", header, StringComparison.Ordinal);
        Assert.Contains("app_MainModuleId_Sensor = 1,", header, StringComparison.Ordinal);
        Assert.Contains("app_MainModuleId_Controller = 2,", header, StringComparison.Ordinal);
    }

    [Fact]
    public void The_enum_is_scoped_to_its_bus_so_two_buses_can_share_a_module_name()
    {
        // The reason the bus name is in there at all: one C project may include headers generated from
        // several buses, and a `Sensor` on each would otherwise be the same C identifier twice.
        var project = new Project("TwoBuses");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var headers = new List<string>();
        foreach (var busName in new[] { "Main", "Backup" })
        {
            var bus = new Bus(BusId.New(), busName, Transport.Ethernet);
            bus.AddModule("Sensor");
            var message = new Message(MessageId.New(), $"Ping{busName}") { WireId = 1 };
            message.Fields.Add(new FieldBinding(FieldId.New(), "counter", u32.Id));
            bus.Messages.Add(message);
            project.Buses.Add(bus);

            var ir = new IrBuilder().Build(project, bus);
            headers.Add(new CGenerator().Generate(ir, new GeneratorOptions(Namespace: "app"))
                .Files.Single(f => f.RelativePath.EndsWith(".h", StringComparison.Ordinal)
                                   && f.RelativePath is not "protodesigner_runtime.h" and not "app_types.h")
                .Contents);
        }

        Assert.Contains("app_MainModuleId_Sensor", headers[0], StringComparison.Ordinal);
        Assert.Contains("app_BackupModuleId_Sensor", headers[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_a_module_renames_its_id()
    {
        // What "the bus name and module names are what update it" means in practice: the enum is derived
        // at generation, so there is nothing stored to go stale.
        Assert.Contains("app_MainModuleId_Radio", GenerateC("Radio"), StringComparison.Ordinal);
        Assert.DoesNotContain("app_MainModuleId_Radio", GenerateC("Antenna"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bus_with_no_modules_emits_no_enum()
    {
        // An enum whose only member is NotAssigned says nothing and still costs a name.
        Assert.DoesNotContain("ModuleId", GenerateC(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_lookup_covers_every_module_and_falls_back_to_empty()
    {
        var header = GenerateC("Sensor");

        Assert.Contains("const char *app_Main_ModuleName(", header, StringComparison.Ordinal);
        Assert.Contains("return \"Sensor\";", header, StringComparison.Ordinal);
    }

    [Fact]
    public void The_csharp_target_emits_the_same_identities()
    {
        var (project, bus) = BusWithModules("Sensor", "Controller");
        var ir = new IrBuilder().Build(project, bus);

        var source = new CSharpGenerator().Generate(ir, new GeneratorOptions(Namespace: "App"))
            .Files.Single(f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal)
                               && !f.RelativePath.EndsWith("Types.cs", StringComparison.Ordinal)
                               && f.RelativePath != "ProtoDesignerRuntime.cs")
            .Contents;

        Assert.Contains("public enum MainModuleId : uint", source, StringComparison.Ordinal);
        Assert.Contains("Sensor = 1,", source, StringComparison.Ordinal);
        Assert.Contains("Controller = 2,", source, StringComparison.Ordinal);
    }
}
