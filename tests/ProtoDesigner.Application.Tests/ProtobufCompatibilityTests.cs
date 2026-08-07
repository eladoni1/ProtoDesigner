using ProtoDesigner.Application.Commands;
using ProtoDesigner.CodeGen;

namespace ProtoDesigner.Application.Tests;

/// <summary>
/// The gate that decides which messages a protobuf export covers, and the narrowing that applies it.
/// </summary>
/// <remarks>
/// The narrowing lives in <see cref="CodeGenerationService"/> so the CLI and the editor cannot disagree
/// about what was exported. These tests pin both halves: that the query names the right messages, and
/// that a message it rejects never reaches a generator.
/// </remarks>
public class ProtobufCompatibilityTests
{
    /// <summary>One exportable message and one that is bit-packed, on the same bus.</summary>
    private static (Project Project, Bus Bus) Mixed()
    {
        var project = new Project("Mixed");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
            .With("Idle", 0).With("Busy", 1));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);

        var plain = new Message(MessageId.New(), "Plain") { WireId = 1 };
        plain.Fields.Add(new FieldBinding(FieldId.New(), "counter", u32.Id));

        var packed = new Message(MessageId.New(), "Packed") { WireId = 2 };
        packed.Fields.Add(new FieldBinding(FieldId.New(), "mode", mode.Id, FieldEncoding.Packed(4)));

        bus.Messages.Add(plain);
        bus.Messages.Add(packed);
        project.Buses.Add(bus);

        return (project, bus);
    }

    [Fact]
    public void Eligibility_separates_the_exportable_from_the_rest()
    {
        var (project, bus) = Mixed();

        var results = ProtobufCompatibility.ForBus(project, bus);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.Message.Name == "Plain").IsEligible);
        Assert.False(results.Single(r => r.Message.Name == "Packed").IsEligible);
    }

    [Fact]
    public void An_ineligible_message_carries_a_reason_naming_the_field()
    {
        // "Your message is not supported" sends the user hunting; naming the field does not.
        var (project, bus) = Mixed();

        var packed = ProtobufCompatibility.ForBus(project, bus).Single(r => r.Message.Name == "Packed");

        Assert.NotNull(packed.Reason);
        Assert.Contains("mode", packed.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrowing_keeps_only_what_can_be_exported()
    {
        var (project, bus) = Mixed();

        var narrowed = ProtobufCompatibility.Narrow(project, GenerationScopes.ForBus(bus));

        var kept = Assert.Single(narrowed);
        Assert.Equal("Plain", Assert.Single(kept.Messages).Name);
    }

    [Fact]
    public void A_bus_with_nothing_exportable_drops_out_entirely()
    {
        var project = new Project("AllPacked");
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
            .With("Idle", 0).With("Busy", 1));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var packed = new Message(MessageId.New(), "Packed") { WireId = 1 };
        packed.Fields.Add(new FieldBinding(FieldId.New(), "mode", mode.Id, FieldEncoding.Packed(4)));
        bus.Messages.Add(packed);
        project.Buses.Add(bus);

        Assert.Empty(ProtobufCompatibility.Narrow(project, GenerationScopes.ForBus(bus)));
    }

    [Fact]
    public void Generating_for_protobuf_skips_what_it_cannot_express_and_says_so()
    {
        var (project, bus) = Mixed();
        var proto = GeneratorCatalog.Find("proto")!;

        var result = CodeGenerationService.Generate(
            project, proto, GenerationScopes.ForBus(bus), new GeneratorOptions(Namespace: "mixed"));

        Assert.False(result.Refused);
        Assert.Equal(1, result.MessageCount);

        // The exclusion travels with the result: a message dropped in silence is the failure this whole
        // path exists to avoid.
        var excluded = Assert.Single(result.Excluded);
        Assert.Equal("Packed", excluded.Message.Name);

        var source = result.Files.Files.Single(f => f.RelativePath == "main.proto").Contents;
        Assert.Contains("message Plain", source, StringComparison.Ordinal);
        Assert.DoesNotContain("message Packed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Generating_for_C_covers_everything_and_excludes_nothing()
    {
        // The gate must never reach a target whose wire format is ours. A bit-packed message is exactly
        // what the C target is for.
        var (project, bus) = Mixed();
        var c = GeneratorCatalog.Find("c")!;

        var result = CodeGenerationService.Generate(
            project, c, GenerationScopes.ForBus(bus), new GeneratorOptions(Namespace: "mixed"));

        Assert.Equal(2, result.MessageCount);
        Assert.Empty(result.Excluded);
    }

    // ---- field numbers ---------------------------------------------------------------------------

    [Fact]
    public void Assigning_numbers_fills_only_the_gaps()
    {
        var (project, bus) = Mixed();
        var fields = bus.Messages[0].Fields;
        fields[0].ProtoFieldNumber = 5;

        new AssignProtoFieldNumbersCommand().Apply(project);

        // An existing number is never reassigned; new ones continue above the highest in use so a field
        // added later cannot claim a retired number.
        Assert.Equal(5, fields[0].ProtoFieldNumber);
        Assert.All(project.Buses.SelectMany(b => b.Messages).SelectMany(m => m.Fields),
            f => Assert.NotNull(f.ProtoFieldNumber));
    }

    [Fact]
    public void Assigning_numbers_is_undoable()
    {
        // Assignment is an edit command precisely so it lands in the journal rather than happening as a
        // side effect of generating.
        var (project, bus) = Mixed();
        var command = new AssignProtoFieldNumbersCommand();

        command.Apply(project);
        Assert.NotNull(bus.Messages[0].Fields[0].ProtoFieldNumber);

        command.Undo(project);
        Assert.Null(bus.Messages[0].Fields[0].ProtoFieldNumber);
    }

    [Fact]
    public void Numbers_are_unique_within_each_message()
    {
        var (project, _) = Mixed();

        new AssignProtoFieldNumbersCommand().Apply(project);

        foreach (var message in project.Buses.SelectMany(b => b.Messages))
        {
            var numbers = message.Fields.Select(f => f.ProtoFieldNumber!.Value).ToList();
            Assert.Equal(numbers.Count, numbers.Distinct().Count());
        }
    }

    [Fact]
    public void A_number_follows_its_field_when_the_field_moves()
    {
        // The entire reason numbers are persisted rather than derived. Reordering fields changes the wire
        // layout of the C target by design — but a protobuf peer keyed on field 1 must still find the same
        // field afterwards, or a reorder in the editor silently breaks every deployed consumer.
        var project = new Project("Reorder");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "M") { WireId = 1 };
        var alpha = new FieldBinding(FieldId.New(), "alpha", u32.Id);
        var beta = new FieldBinding(FieldId.New(), "beta", u32.Id);
        message.Fields.Add(alpha);
        message.Fields.Add(beta);
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        new AssignProtoFieldNumbersCommand().Apply(project);
        var alphaNumber = alpha.ProtoFieldNumber;
        var betaNumber = beta.ProtoFieldNumber;
        Assert.NotEqual(alphaNumber, betaNumber);

        // Move beta to the front — a normal edit, and one that changes the C wire layout.
        message.MoveField(beta.Id, 0);
        Assert.Equal("beta", message.Fields[0].Name);

        // Re-running assignment must not renumber anything: both already have numbers.
        new AssignProtoFieldNumbersCommand().Apply(project);

        Assert.Equal(alphaNumber, alpha.ProtoFieldNumber);
        Assert.Equal(betaNumber, beta.ProtoFieldNumber);
    }

    [Fact]
    public void A_field_added_after_assignment_gets_a_fresh_number()
    {
        // It must not reuse a number, and must not disturb the ones already agreed.
        var project = new Project("Grow");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "M") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "a", u32.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "b", u32.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        new AssignProtoFieldNumbersCommand().Apply(project);
        var existing = message.Fields.Select(f => f.ProtoFieldNumber!.Value).ToList();

        // Insert at the front, which is where a naive ordinal scheme would collide hardest.
        var inserted = new FieldBinding(FieldId.New(), "c", u32.Id);
        message.Fields.Insert(0, inserted);
        new AssignProtoFieldNumbersCommand().Apply(project);

        Assert.NotNull(inserted.ProtoFieldNumber);
        Assert.DoesNotContain(inserted.ProtoFieldNumber!.Value, existing);
        Assert.Equal(existing, message.Fields.Where(f => f != inserted).Select(f => f.ProtoFieldNumber!.Value));
    }

    // ---- struct-level findings -------------------------------------------------------------------

    /// <summary>
    /// A shared struct with clashing field numbers, one message using it directly, one reaching it through
    /// another struct, and one that never touches it.
    /// </summary>
    private static (Project Project, Bus Bus) SharedStructWithClashingNumbers()
    {
        var project = new Project("Shared");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        // Two fields claiming field number 1 — a struct-level defect, so the diagnostic carries a type
        // path and no message name at all.
        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "messageId", u32.Id) { ProtoFieldNumber = 1 },
                  new FieldBinding(FieldId.New(), "version", u32.Id) { ProtoFieldNumber = 1 }));

        var envelope = project.Types.Add(new StructType(TypeId.New(), "Envelope")
            .With(new FieldBinding(FieldId.New(), "head", header.Id),
                  new FieldBinding(FieldId.New(), "sequence", u32.Id)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);

        var direct = new Message(MessageId.New(), "Direct") { WireId = 1 };
        direct.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));

        var nested = new Message(MessageId.New(), "Nested") { WireId = 2 };
        nested.Fields.Add(new FieldBinding(FieldId.New(), "envelope", envelope.Id));

        var unrelated = new Message(MessageId.New(), "Unrelated") { WireId = 3 };
        unrelated.Fields.Add(new FieldBinding(FieldId.New(), "counter", u32.Id));

        bus.Messages.Add(direct);
        bus.Messages.Add(nested);
        bus.Messages.Add(unrelated);
        project.Buses.Add(bus);

        return (project, bus);
    }

    [Fact]
    public void A_struct_level_finding_blocks_every_message_that_uses_the_struct()
    {
        // The finding names a type, not a message, so without attribution it would belong to nobody and a
        // broken struct would export silently. Both users must be blocked, including the one that reaches
        // it through another struct.
        var (project, bus) = SharedStructWithClashingNumbers();

        var results = ProtobufCompatibility.ForBus(project, bus);

        Assert.False(results.Single(r => r.Message.Name == "Direct").IsEligible);
        Assert.False(results.Single(r => r.Message.Name == "Nested").IsEligible);
    }

    [Fact]
    public void A_sub_byte_field_inside_a_struct_is_named_by_its_path_not_its_container()
    {
        // MessageLayout.Values() yields the struct node as well as its leaves, and a struct holding a
        // 4-bit field spans 52 bits — not a whole number of bytes either. Reporting the container refuses
        // the right message while pointing at the wrong thing, which defeats the reason this gate names
        // fields at all. A struct is perfectly representable as a nested message; only its scalars fail.
        var project = new Project("Nested");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var narrow = project.Types.Add(new ParameterType(TypeId.New(), "Narrow", PrimitiveKind.U32,
            new NumericRange(0, 15)));

        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "messageId", u32.Id),
                  new FieldBinding(FieldId.New(), "randomType", narrow.Id, FieldEncoding.Packed(4))));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Telemetry") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var result = ProtobufCompatibility.ForBus(project, bus).Single();

        Assert.False(result.IsEligible);
        Assert.Contains("header.randomType", result.Reason!, StringComparison.Ordinal);
        Assert.Contains("4 bits", result.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_struct_of_byte_aligned_fields_is_exportable_however_wide_it_is()
    {
        // The other half. Nothing about a struct's own total width matters to protobuf — it becomes a
        // nested message, and its fields are what have to be expressible.
        var project = new Project("Wide");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var u64 = project.Types.Add(new ParameterType(TypeId.New(), "u64", PrimitiveKind.U64));

        // 96 bits, which is neither 32 nor 64 — the widths the narrow-integer rule cares about. The rule
        // must not be looking at the struct at all.
        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "messageId", u32.Id),
                  new FieldBinding(FieldId.New(), "timestamp", u64.Id)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Telemetry") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var result = ProtobufCompatibility.ForBus(project, bus).Single();

        Assert.True(result.IsEligible, $"a byte-aligned struct was refused: {result.Reason}");
    }

    [Fact]
    public void A_struct_level_finding_leaves_messages_that_do_not_use_it_alone()
    {
        // The other half, and the one that fails if attribution is too eager: marking every message on the
        // bus would also "pass" the test above while blocking exports that are perfectly fine.
        var (project, bus) = SharedStructWithClashingNumbers();

        var unrelated = ProtobufCompatibility.ForBus(project, bus).Single(r => r.Message.Name == "Unrelated");

        Assert.True(unrelated.IsEligible, $"an unaffected message was blocked: {unrelated.Reason}");
    }

    [Fact]
    public void A_fully_numbered_project_reports_nothing_to_assign()
    {
        var (project, _) = Mixed();

        Assert.True(AssignProtoFieldNumbersCommand.HasUnassigned(project));
        new AssignProtoFieldNumbersCommand().Apply(project);
        Assert.False(AssignProtoFieldNumbersCommand.HasUnassigned(project));
    }
}
