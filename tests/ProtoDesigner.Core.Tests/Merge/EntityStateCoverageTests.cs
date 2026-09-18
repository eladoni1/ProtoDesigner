using System.Reflection;
using ProtoDesigner.Core.Merge;

// `Module` is a model entity here, never System.Reflection's.
using Module = ProtoDesigner.Core.Model.Module;

namespace ProtoDesigner.Core.Tests.Merge;

/// <summary>
/// Every property the merge has to notice, proven one property at a time.
/// </summary>
/// <remarks>
/// <para>
/// The merge decides "did this entity change?" by comparing a rendered state string. A property missing
/// from that string is not a cosmetic gap: the merge sees an edited entity as unchanged and discards the
/// edit as "the other side has nothing new". Nothing downstream reports it, because the result is a
/// perfectly valid project — just not the one either person saved.
/// </para>
/// <para>
/// So each case edits exactly one property on the remote side and asserts the merge both <em>saw</em> it
/// and <em>applied</em> it. Those are two separate failures: the state string can be missing a property,
/// or the copy routine that carries a change into the local copy can be. The second merge catches the
/// latter — once the change has landed, re-running against the same remote must have nothing left to do.
/// </para>
/// <para>
/// <see cref="Every_property_of_every_merged_entity_is_accounted_for"/> is what keeps this honest over
/// time. It reflects over the model rather than trusting the list below, so adding a property to
/// <c>FieldBinding</c> fails this suite until someone either covers it or writes down why it needs no
/// coverage. That is the whole point: the gap this guards against is invisible by construction, and a
/// hand-maintained list would grow the same hole it exists to close.
/// </para>
/// </remarks>
public class EntityStateCoverageTests
{
    private static readonly TypeId U8 = new(Guid.Parse("70000000-0000-0000-0000-000000000001"));
    private static readonly TypeId ModeEnum = new(Guid.Parse("70000000-0000-0000-0000-000000000002"));
    private static readonly TypeId Header = new(Guid.Parse("70000000-0000-0000-0000-000000000003"));
    private static readonly TypeId Samples = new(Guid.Parse("70000000-0000-0000-0000-000000000004"));

    private static readonly BusId TheBus = new(Guid.Parse("b0000000-0000-0000-0000-000000000001"));
    private static readonly ModuleId Sensor = new(Guid.Parse("c0000000-0000-0000-0000-000000000001"));
    private static readonly ModuleId Logger = new(Guid.Parse("c0000000-0000-0000-0000-000000000002"));
    private static readonly MessageId Status = new(Guid.Parse("d0000000-0000-0000-0000-000000000001"));
    private static readonly FieldId Mode = new(Guid.Parse("f0000000-0000-0000-0000-000000000001"));
    private static readonly FieldId Inner = new(Guid.Parse("f0000000-0000-0000-0000-000000000002"));

    /// <summary>One of every entity the merge walks, identical every time it is called.</summary>
    private static Project Build()
    {
        var project = new Project("Telemetry");
        project.Types.Add(new ParameterType(U8, "u8", PrimitiveKind.U8));
        project.Types.Add(new EnumType(ModeEnum, "Mode", PrimitiveKind.U8).With("Idle", 0));
        project.Types.Add(new StructType(Header, "Header").With(new FieldBinding(Inner, "version", U8)));
        project.Types.Add(new ArrayType(Samples, "Samples", U8, new ArrayLength.Fixed(4)));

        var message = new Message(Status, "Status") { WireId = 1 };
        message.Fields.Add(new FieldBinding(Mode, "mode", U8));

        var bus = new Bus(TheBus, "Main", Transport.Ethernet);
        bus.Modules.Add(new Module(Sensor, "Sensor"));
        bus.Modules.Add(new Module(Logger, "Logger"));
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return project;
    }

    private static Bus BusOf(Project p) => p.Buses[0];
    private static Message MessageOf(Project p) => p.Buses[0].Messages[0];
    private static FieldBinding FieldOf(Project p) => MessageOf(p).Fields[0];
    private static Module ModuleOf(Project p) => p.Buses[0].Modules[0];
    private static ParameterType ParameterOf(Project p) => (ParameterType)p.Types[U8];
    private static EnumType EnumOf(Project p) => (EnumType)p.Types[ModeEnum];
    private static StructType StructOf(Project p) => (StructType)p.Types[Header];
    private static ArrayType ArrayOf(Project p) => (ArrayType)p.Types[Samples];

    // ---- one edit per property ---------------------------------------------------------------------

    /// <summary>
    /// Keyed by "&lt;entity&gt;.&lt;property&gt;", so the reflection guard below can check the model
    /// against it name by name.
    /// </summary>
    private static readonly Dictionary<string, Action<Project>> Edits = BuildEdits();

    private static Dictionary<string, Action<Project>> BuildEdits()
    {
        var edits = new Dictionary<string, Action<Project>>
        {
            ["Message.Name"] = p => MessageOf(p).Name = "Renamed",
            ["Message.WireId"] = p => MessageOf(p).WireId = 9,
            ["Message.Description"] = p => MessageOf(p).Description = "note",
            ["Message.Routes"] = p => MessageOf(p).Routes.Add(new MessageRoute(Sensor, Logger)),

            ["FieldBinding.Name"] = p => FieldOf(p).Name = "renamed",
            ["FieldBinding.TypeId"] = p => FieldOf(p).TypeId = ModeEnum,
            ["FieldBinding.Description"] = p => FieldOf(p).Description = "note",
            ["FieldBinding.DefaultValue"] = p => FieldOf(p).DefaultValue = 7,
            ["FieldBinding.ProtoFieldNumber"] = p => FieldOf(p).ProtoFieldNumber = 3,

            ["Bus.Name"] = p => BusOf(p).Name = "Renamed",
            ["Bus.Transport"] = p => BusOf(p).Transport = Transport.Uart,

            ["Module.Name"] = p => ModuleOf(p).Name = "Renamed",

            ["ParameterType.Name"] = p => ParameterOf(p).Name = "renamed",
            ["ParameterType.Description"] = p => ParameterOf(p).Description = "note",
            ["ParameterType.Kind"] = p => ParameterOf(p).Kind = PrimitiveKind.U16,
            ["ParameterType.Range"] = p => ParameterOf(p).Range = new NumericRange(0, 10),
            ["ParameterType.WireBits"] = p => ParameterOf(p).WireBits = 4,
            ["ParameterType.WireForm"] = p => ParameterOf(p).WireForm = WireForm.Signed,
            ["ParameterType.WireOffset"] = p => ParameterOf(p).WireOffset = 1m,
            ["ParameterType.WireScale"] = p => ParameterOf(p).WireScale = 2m,

            ["EnumType.Name"] = p => EnumOf(p).Name = "Renamed",
            ["EnumType.Description"] = p => EnumOf(p).Description = "note",
            ["EnumType.UnderlyingKind"] = p => EnumOf(p).UnderlyingKind = PrimitiveKind.U16,
            ["EnumType.IsFlags"] = p => EnumOf(p).IsFlags = true,
            ["EnumType.Synthetic"] = p => EnumOf(p).Synthetic = SyntheticEnum.MessageId,
            ["EnumType.Members"] = p => EnumOf(p).With("Active", 1),
            ["EnumType.WireBits"] = p => EnumOf(p).WireBits = 4,
            ["EnumType.WireForm"] = p => EnumOf(p).WireForm = WireForm.Signed,
            ["EnumType.WireOffset"] = p => EnumOf(p).WireOffset = 1m,
            ["EnumType.WireScale"] = p => EnumOf(p).WireScale = 2m,

            ["StructType.Name"] = p => StructOf(p).Name = "Renamed",
            ["StructType.Description"] = p => StructOf(p).Description = "note",
            ["StructType.Fields"] = p => StructOf(p).With(new FieldBinding("extra", U8)),

            ["ArrayType.Name"] = p => ArrayOf(p).Name = "Renamed",
            ["ArrayType.Description"] = p => ArrayOf(p).Description = "note",
            ["ArrayType.ElementTypeId"] = p => ArrayOf(p).ElementTypeId = ModeEnum,
            ["ArrayType.Length"] = p => ArrayOf(p).Length = new ArrayLength.Fixed(5),

            ["FieldEncoding.BitWidth"] = p => FieldOf(p).Encoding.BitWidth = 3,
            ["FieldEncoding.Endianness"] = p => FieldOf(p).Encoding.Endianness = Endianness.Big,
            ["FieldEncoding.BitOrder"] = p => FieldOf(p).Encoding.BitOrder = BitOrder.LsbFirst,
            ["FieldEncoding.AllowBitPacking"] = p => FieldOf(p).Encoding.AllowBitPacking = true,
            ["FieldEncoding.AlignmentBits"] = p => FieldOf(p).Encoding.AlignmentBits = 16,
            ["FieldEncoding.Transform"] = p => FieldOf(p).Encoding.Transform = new ScalarTransform(1, 2),
        };

        // A message and a bus each own a LayoutOptions, and each is rendered into its owner's state. One
        // list of edits, applied to both owners, so neither can be the one that was forgotten.
        var options = new Dictionary<string, Action<LayoutOptions>>
        {
            ["Endianness"] = o => o.Endianness = Endianness.Big,
            ["BitOrder"] = o => o.BitOrder = BitOrder.LsbFirst,
            ["DefaultAlignmentBits"] = o => o.DefaultAlignmentBits = 16,
            ["PackingMode"] = o => o.PackingMode = BitPackingMode.StorageUnit,
            ["PadToByteBoundary"] = o => o.PadToByteBoundary = false,
        };

        foreach (var (name, edit) in options)
        {
            edits[$"Message.Options.{name}"] = p => edit(MessageOf(p).Options);
            edits[$"Bus.Options.{name}"] = p => edit(BusOf(p).Options);
        }

        return edits;
    }

    public static TheoryData<string> EveryEdit()
    {
        var data = new TheoryData<string>();
        foreach (var name in Edits.Keys.OrderBy(k => k, StringComparer.Ordinal)) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryEdit))]
    public void A_remote_edit_to_one_property_is_both_seen_and_applied(string property)
    {
        var baseline = Build();
        var local = Build();
        var remote = Build();

        Edits[property](remote);

        var first = ProjectMerge.Merge(baseline, local, remote);

        Assert.Empty(first.Conflicts);
        Assert.True(first.Applied.Count > 0,
            $"Editing {property} remotely changed nothing the merge could see, so a real edit would be "
            + "silently discarded. The property is missing from EntityState.");

        // `local` now carries the merge. Running the same merge again must find nothing left, which fails
        // if the change was noticed but only partly copied across.
        var second = ProjectMerge.Merge(baseline, local, remote);

        Assert.Empty(second.Conflicts);
        Assert.True(second.Applied.Count == 0,
            $"The merge saw the edit to {property} but did not carry it into the local copy: "
            + $"[{string.Join("; ", second.Applied)}]");
    }

    // ---- the guard that keeps the list above complete -----------------------------------------------

    /// <summary>
    /// Properties that carry no state of their own, each with the reason it needs no case.
    /// </summary>
    /// <remarks>
    /// An entry here is a decision, not a silence. The two kinds are identity (mutating an id makes a
    /// different entity, which is an add plus a remove, not a change) and containers whose contents are
    /// entities in their own right — folding those into the owner's state would make every field edit
    /// read as a message edit, and turn two people editing different fields of one message into a
    /// conflict.
    /// </remarks>
    private static readonly Dictionary<string, string> NotState = new()
    {
        ["Message.Id"] = "identity",
        ["FieldBinding.Id"] = "identity",
        ["Bus.Id"] = "identity",
        ["Module.Id"] = "identity",
        ["ParameterType.Id"] = "identity",
        ["EnumType.Id"] = "identity",
        ["StructType.Id"] = "identity",
        ["ArrayType.Id"] = "identity",

        ["Message.Fields"] = "merged as field entities; the list's order is merged separately",
        ["Bus.Messages"] = "merged as message entities",
        ["Bus.Modules"] = "merged as module entities",

        ["Message.Options"] = "covered property by property, as Message.Options.*",
        ["Bus.Options"] = "covered property by property, as Bus.Options.*",
        ["FieldBinding.Encoding"] = "covered property by property, as FieldEncoding.*",

        ["ParameterType.IsConstant"] = "derived from Range",
        ["EnumType.MemberRange"] = "derived from Members",
    };

    public static TheoryData<string> EveryMergedEntity() => new()
    {
        nameof(Message), nameof(FieldBinding), nameof(Bus), nameof(Module),
        nameof(ParameterType), nameof(EnumType), nameof(StructType), nameof(ArrayType),
        nameof(FieldEncoding),
    };

    [Theory]
    [MemberData(nameof(EveryMergedEntity))]
    public void Every_property_of_every_merged_entity_is_accounted_for(string entity)
    {
        var type = typeof(Message).Assembly.GetTypes().Single(t => t.Name == entity && t.IsPublic);

        var uncovered = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => $"{entity}.{p.Name}")
            .Where(key => !Edits.ContainsKey(key) && !NotState.ContainsKey(key))
            .ToList();

        Assert.True(uncovered.Count == 0,
            $"{string.Join(", ", uncovered)} is neither edited by a case above nor listed in NotState. "
            + "A property the merge cannot see is an edit the merge will discard — give it a case, or "
            + "write down why it carries no state.");
    }

    /// <summary>The same guard for the options bag, which is reached through two different owners.</summary>
    [Fact]
    public void Every_layout_option_is_accounted_for_on_both_of_its_owners()
    {
        var uncovered = typeof(LayoutOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(p => new[] { $"Message.Options.{p.Name}", $"Bus.Options.{p.Name}" })
            .Where(key => !Edits.ContainsKey(key))
            .ToList();

        Assert.True(uncovered.Count == 0, string.Join(", ", uncovered));
    }
}
