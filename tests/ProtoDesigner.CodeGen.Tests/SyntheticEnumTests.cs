using ProtoDesigner.CodeGen.C;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Enums the user puts on a field but does not fill in: the bus supplies the members.
/// </summary>
/// <remarks>
/// The point is a shared <c>Header</c> that carries "the message id" and means the right thing on every
/// bus that uses it. Storing the members instead would mean a hand-maintained list going stale the moment
/// a message was renamed — which is exactly the failure this type removes.
/// </remarks>
public class SyntheticEnumTests
{
    private static string Generate(SyntheticEnum kind, out Project project, out Bus bus)
    {
        project = new Project("Ids");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var idType = project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U32)
        {
            Synthetic = kind,
            WireBits = 32,
        });

        bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        bus.AddModule("Sensor");
        bus.AddModule("Controller");

        var alpha = new Message(MessageId.New(), "Alpha") { WireId = 7 };
        alpha.Fields.Add(new FieldBinding(FieldId.New(), "id", idType.Id));
        alpha.Fields.Add(new FieldBinding(FieldId.New(), "payload", u32.Id));
        bus.Messages.Add(alpha);

        var beta = new Message(MessageId.New(), "Beta") { WireId = 9 };
        beta.Fields.Add(new FieldBinding(FieldId.New(), "payload", u32.Id));
        bus.Messages.Add(beta);

        project.Buses.Add(bus);

        var ir = new IrBuilder().Build(project, bus);
        return new CGenerator().Generate(ir, new GeneratorOptions(Namespace: "app"))
            .Files.Single(f => f.RelativePath == "app_types.h").Contents;
    }

    [Fact]
    public void A_message_id_field_gets_every_message_on_the_bus()
    {
        var types = Generate(SyntheticEnum.MessageId, out _, out _);

        Assert.Contains("app_MessageId_NotAssigned = 0,", types, StringComparison.Ordinal);
        Assert.Contains("app_MessageId_Alpha = 7,", types, StringComparison.Ordinal);

        // Including messages that do not use the type themselves — the enum names the bus, not the field.
        Assert.Contains("app_MessageId_Beta = 9,", types, StringComparison.Ordinal);
    }

    [Fact]
    public void The_values_are_the_declared_wire_ids_not_positions()
    {
        // A message id is Message.WireId, which the user chose and a deployed peer reads off the wire, so
        // it must survive a reorder. Numbering by position would silently break every consumer.
        var types = Generate(SyntheticEnum.MessageId, out _, out _);

        Assert.DoesNotContain("app_MessageId_Alpha = 1,", types, StringComparison.Ordinal);
        Assert.Contains("app_MessageId_Alpha = 7,", types, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_id_field_gets_every_module_on_the_bus()
    {
        var types = Generate(SyntheticEnum.ModuleId, out _, out _);

        Assert.Contains("app_MessageId_NotAssigned = 0,", types, StringComparison.Ordinal);
        Assert.Contains("app_MessageId_Sensor = 1,", types, StringComparison.Ordinal);
        Assert.Contains("app_MessageId_Controller = 2,", types, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_a_message_updates_the_enum_with_nothing_left_stale()
    {
        // The whole reason the members are derived rather than stored.
        Generate(SyntheticEnum.MessageId, out var project, out var bus);
        bus.Messages.Single(m => m.Name == "Alpha").Name = "Renamed";

        var ir = new IrBuilder().Build(project, bus);
        var types = new CGenerator().Generate(ir, new GeneratorOptions(Namespace: "app"))
            .Files.Single(f => f.RelativePath == "app_types.h").Contents;

        Assert.Contains("app_MessageId_Renamed = 7,", types, StringComparison.Ordinal);
        Assert.DoesNotContain("app_MessageId_Alpha", types, StringComparison.Ordinal);
    }

    [Fact]
    public void The_field_is_a_real_field_of_the_message_struct()
    {
        // It has to be usable, not just declared: the header struct must actually carry it.
        var project = new Project("Ids");
        var idType = project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U32)
        {
            Synthetic = SyntheticEnum.MessageId,
            WireBits = 32,
        });

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Alpha") { WireId = 7 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "id", idType.Id));
        bus.Messages.Add(m);
        project.Buses.Add(bus);

        var header = new CGenerator().Generate(new IrBuilder().Build(project, bus),
                new GeneratorOptions(Namespace: "app"))
            .Files.Single(f => f.RelativePath == "main.h").Contents;

        Assert.Contains("app_MessageId id;", header, StringComparison.Ordinal);
    }
}
