using ProtoDesigner.CodeGen.C;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// What changes when the bus changes: the built-in id enums are derived on every build, so every one of
/// these has to follow with nothing left behind.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SyntheticEnumTests"/> covers a message being renamed. These are the other four edits that
/// move the emitted output, and they were the deferred half of the coverage: renaming the <em>bus</em>,
/// renaming a module, changing a wire id, and the two always-on enums that are emitted whether or not any
/// field is typed as one.
/// </para>
/// <para>
/// Renaming the bus is the one worth having most. The emitted name carries the bus with it
/// (<c>MessageId</c> becomes <c>MainMessageId</c>) precisely so that one C project including headers from
/// two buses does not end up with two different enums under one name — so the bus's name is part of the
/// output, and nothing stored could disagree with it.
/// </para>
/// </remarks>
public class SyntheticEnumChangeTests
{
    private static (Project Project, Bus Bus) Build()
    {
        var project = new Project("Ids");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var idType = project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U32)
        {
            Synthetic = SyntheticEnum.MessageId,
            WireBits = 32,
        });

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        bus.AddModule("Sensor");
        bus.AddModule("Controller");

        var alpha = new Message(MessageId.New(), "Alpha") { WireId = 7 };
        alpha.Fields.Add(new FieldBinding(FieldId.New(), "id", idType.Id));
        alpha.Fields.Add(new FieldBinding(FieldId.New(), "payload", u32.Id));
        bus.Messages.Add(alpha);

        project.Buses.Add(bus);
        return (project, bus);
    }

    private static string TypesHeader(Project project, Bus bus) =>
        new CGenerator()
            .Generate(new IrBuilder().Build(project, bus), new GeneratorOptions(Namespace: "app"))
            .Files.Single(f => f.RelativePath == "app_types.h").Contents;

    /// <summary>
    /// The per-bus header, found by exclusion rather than by name: the file is named after the bus, so
    /// renaming the bus renames it too.
    /// </summary>
    private static string BusHeader(Project project, Bus bus) =>
        new CGenerator()
            .Generate(new IrBuilder().Build(project, bus), new GeneratorOptions(Namespace: "app"))
            .Files.Single(f => f.RelativePath.EndsWith(".h", StringComparison.Ordinal)
                            && f.RelativePath != "app_types.h"
                            && f.RelativePath != "protodesigner_runtime.h").Contents;

    [Fact]
    public void Renaming_the_bus_renames_the_enum_it_names()
    {
        var (project, bus) = Build();
        Assert.Contains("app_MainMessageId_Alpha = 7,", TypesHeader(project, bus), StringComparison.Ordinal);

        bus.Name = "Backbone";
        var types = TypesHeader(project, bus);

        Assert.Contains("app_BackboneMessageId_Alpha = 7,", types, StringComparison.Ordinal);
        Assert.DoesNotContain("MainMessageId", types, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_a_wire_id_changes_the_member_value()
    {
        var (project, bus) = Build();

        bus.Messages.Single(m => m.Name == "Alpha").WireId = 21;
        var types = TypesHeader(project, bus);

        Assert.Contains("app_MainMessageId_Alpha = 21,", types, StringComparison.Ordinal);
        Assert.DoesNotContain("app_MainMessageId_Alpha = 7,", types, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message that loses its wire id leaves the enum entirely — there is no id for a peer to match on,
    /// so naming one would be inventing it.
    /// </summary>
    [Fact]
    public void Clearing_a_wire_id_removes_the_member()
    {
        var (project, bus) = Build();

        bus.Messages.Single(m => m.Name == "Alpha").WireId = null;
        var types = TypesHeader(project, bus);

        Assert.DoesNotContain("app_MainMessageId_Alpha", types, StringComparison.Ordinal);
        Assert.Contains("app_MainMessageId_NotAssigned = 0,", types, StringComparison.Ordinal);
    }

    // ---- the always-on enums, in the per-bus header ------------------------------------------------

    [Fact]
    public void Renaming_a_module_renames_it_in_the_module_id_enum()
    {
        var (project, bus) = Build();
        Assert.Contains("app_MainModuleId_Sensor = 1,", BusHeader(project, bus), StringComparison.Ordinal);

        bus.Modules[0].Name = "Probe";
        var main = BusHeader(project, bus);

        Assert.Contains("app_MainModuleId_Probe = 1,", main, StringComparison.Ordinal);
        Assert.DoesNotContain("app_MainModuleId_Sensor", main, StringComparison.Ordinal);

        // The name lookup is generated from the same list, so it cannot disagree with the enum.
        Assert.Contains("return \"Probe\";", main, StringComparison.Ordinal);
        Assert.DoesNotContain("return \"Sensor\";", main, StringComparison.Ordinal);
    }

    /// <summary>
    /// A module id is declaration order, not declared state. Removing one from the middle renumbers the
    /// rest, which is harmless between two ends built from the same header and is the documented reason
    /// these are not a wire format.
    /// </summary>
    [Fact]
    public void Removing_a_module_renumbers_the_ones_after_it()
    {
        var (project, bus) = Build();
        bus.AddModule("Logger");
        Assert.Contains("app_MainModuleId_Logger = 3,", BusHeader(project, bus), StringComparison.Ordinal);

        bus.Modules.RemoveAt(0);
        var main = BusHeader(project, bus);

        Assert.Contains("app_MainModuleId_Controller = 1,", main, StringComparison.Ordinal);
        Assert.Contains("app_MainModuleId_Logger = 2,", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_the_bus_renames_the_always_on_module_enum_too()
    {
        var (project, bus) = Build();

        bus.Name = "Backbone";
        var main = BusHeader(project, bus);

        Assert.Contains("app_BackboneModuleId_Sensor = 1,", main, StringComparison.Ordinal);
        Assert.DoesNotContain("MainModuleId", main, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bus header always carries a message-id enum, and a field typed as the synthetic enum produces
    /// one with the same identifier. Emitting both would not compile, so the always-on one is skipped.
    /// </summary>
    [Fact]
    public void The_message_id_enum_is_declared_exactly_once_when_a_field_uses_it()
    {
        var (project, bus) = Build();

        var files = new CGenerator()
            .Generate(new IrBuilder().Build(project, bus), new GeneratorOptions(Namespace: "app"))
            .Files;

        var declarations = files.Sum(f => Occurrences(f.Contents, "typedef enum app_MainMessageId {"));
        Assert.Equal(1, declarations);
    }

    [Fact]
    public void The_message_id_enum_is_still_emitted_when_no_field_uses_it()
    {
        // Nothing references the synthetic type here, but a caller still has to be able to name the
        // message it just read off the wire.
        var project = new Project("Ids");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var alpha = new Message(MessageId.New(), "Alpha") { WireId = 7 };
        alpha.Fields.Add(new FieldBinding(FieldId.New(), "payload", u32.Id));
        bus.Messages.Add(alpha);
        project.Buses.Add(bus);

        var main = BusHeader(project, bus);

        Assert.Contains("app_MainMessageId_Alpha = 7,", main, StringComparison.Ordinal);
        Assert.Contains("app_Main_MessageIdFromWire", main, StringComparison.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
