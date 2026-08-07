using System.Text.RegularExpressions;
using ProtoDesigner.CodeGen.Proto;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// The protobuf schema target.
/// </summary>
/// <remarks>
/// There is no protoc in this test environment, so these assert structure rather than compilation: every
/// field numbered, numbers unique within a message, braces balanced, no import left unused. That is
/// weaker than the C target's cross-check and deliberately so — this target emits a schema for someone
/// else's toolchain to compile, not code we can run.
/// </remarks>
public class ProtoGeneratorTests
{
    private static readonly ProtoGenerator Generator = new();

    private static GeneratedFileSet Generate(
        (Project Project, Bus Bus) sample, bool protovalidate = true)
    {
        var ir = new IrBuilder().Build(sample.Project, sample.Bus);
        var options = new GeneratorOptions(Namespace: "telemetry")
            .With(ProtoGenerator.ProtovalidateOption, protovalidate ? "true" : "false");
        return Generator.Generate(ir, options);
    }

    private static string BusFile(GeneratedFileSet set) =>
        set.Files.Single(f => f.RelativePath.EndsWith(".proto", StringComparison.Ordinal)
                              && !f.RelativePath.EndsWith("_types.proto", StringComparison.Ordinal)).Contents;

    [Fact]
    public void The_target_declares_that_it_cannot_express_every_message()
    {
        // The whole gate hangs off this: CodeGenerationService narrows the scope only for a target that
        // says it borrows a foreign wire format.
        Assert.False(((IProtocolGenerator)Generator).CoversEveryMessage);
        Assert.True(((IProtocolGenerator)new CodeGen.C.CGenerator()).CoversEveryMessage);
    }

    [Fact]
    public void Every_field_gets_a_number_and_no_two_share_one()
    {
        // A reused number is protobuf's one unrecoverable mistake: an old peer decodes the wrong field
        // and reports nothing.
        foreach (var (name, factory) in Corpus.All())
        {
            var source = BusFile(Generate(factory()));

            foreach (var message in Messages(source))
            {
                var numbers = FieldNumbers(message.Body).ToList();
                Assert.True(numbers.Count > 0, $"{name}/{message.Name} declared no fields");
                Assert.Equal(numbers.Count, numbers.Distinct().Count());
                Assert.All(numbers, n => Assert.True(n >= 1, $"{name}/{message.Name} has field number {n}"));
            }
        }
    }

    [Fact]
    public void A_declared_range_becomes_a_constraint()
    {
        // The reason the target is worth having. Without this, 1000..1015 is bare uint32.
        var source = BusFile(Generate(Corpus.PackedBits()));

        Assert.Contains("(buf.validate.field).uint32.gte = 1000", source, StringComparison.Ordinal);
        Assert.Contains("(buf.validate.field).uint32.lte = 1015", source, StringComparison.Ordinal);
    }

    [Fact]
    public void An_array_capacity_becomes_a_constraint()
    {
        var source = BusFile(Generate(Corpus.DynamicArray()));

        Assert.Contains("repeated uint32 payload", source, StringComparison.Ordinal);
        Assert.Contains("(buf.validate.field).repeated.max_items = 32", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fixed_array_is_pinned_to_its_exact_count()
    {
        var source = BusFile(Generate(Corpus.StructAndArray()));

        Assert.Contains("min_items: 4, max_items: 4", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Turning_protovalidate_off_removes_every_constraint_and_its_import()
    {
        var source = BusFile(Generate(Corpus.PackedBits(), protovalidate: false));

        Assert.DoesNotContain("buf.validate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("import \"buf/validate", source, StringComparison.Ordinal);
        Assert.Contains("message Status", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_validate_import_appears_only_where_constraints_do()
    {
        // protoc warns about an unused import, so a file with nothing to constrain must not carry one.
        foreach (var (name, factory) in Corpus.All())
        {
            var set = Generate(factory());

            foreach (var file in set.Files.Where(f => f.RelativePath.EndsWith(".proto", StringComparison.Ordinal)))
            {
                var imports = file.Contents.Contains("import \"buf/validate", StringComparison.Ordinal);
                var uses = file.Contents.Contains("(buf.validate.field)", StringComparison.Ordinal);
                Assert.Equal(uses, imports);
            }
        }
    }

    [Fact]
    public void A_full_width_range_is_not_restated_as_a_constraint()
    {
        // A u32 spanning 0..uint.MaxValue says exactly what `uint32` already says. Emitting it would read
        // as a designed limit rather than the absence of one.
        var (project, bus) = Corpus.Scalars();
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "wide", PrimitiveKind.U32,
            PrimitiveKind.U32.NaturalRange()));
        bus.Messages[0].Fields.Add(new FieldBinding(FieldId.New(), "wide", u32.Id));

        var source = BusFile(Generate((project, bus)));

        Assert.Contains("uint32 wide", source, StringComparison.Ordinal);
        Assert.DoesNotContain("4294967295", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_narrow_host_keeps_its_limits_because_protobuf_widened_it()
    {
        // The counterpart to the test above: a u8 becomes uint32, which can hold 70000. Its 0..255 span
        // is therefore real information the protobuf type no longer carries, and worth restating.
        var project = new Project("Narrow");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8,
            PrimitiveKind.U8.NaturalRange()));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Reading") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "level", u8.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var source = BusFile(Generate((project, bus)));

        Assert.Contains("(buf.validate.field).uint32.lte = 255", source, StringComparison.Ordinal);
    }

    [Fact]
    public void An_assigned_field_number_is_used_verbatim()
    {
        // Stability is the entire point: a regeneration must not renumber a field that peers already agreed on.
        var (project, bus) = Corpus.Scalars();
        var fields = bus.Messages[0].Fields;
        fields[0].ProtoFieldNumber = 7;
        fields[1].ProtoFieldNumber = 12;

        var source = BusFile(Generate((project, bus)));

        Assert.Contains($"{ProtoNaming.FieldName(fields[0].Name)} = 7", source, StringComparison.Ordinal);
        Assert.Contains($"{ProtoNaming.FieldName(fields[1].Name)} = 12", source, StringComparison.Ordinal);

        // The unnumbered third field must not collide with either.
        var numbers = FieldNumbers(Messages(source).Single().Body).ToList();
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
    }

    [Fact]
    public void An_enum_gets_a_zero_value_because_proto3_requires_one()
    {
        var source = Generate(Corpus.PackedBits()).Files
            .Single(f => f.RelativePath.EndsWith("_types.proto", StringComparison.Ordinal)).Contents;

        var enumBody = Regex.Match(source, @"enum\s+Mode\s*\{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("= 0;", enumBody, StringComparison.Ordinal);

        // Idle is already 0, so no synthetic UNSPECIFIED is needed.
        Assert.DoesNotContain("UNSPECIFIED", enumBody, StringComparison.Ordinal);
    }

    [Fact]
    public void An_enum_with_no_zero_member_gets_an_unspecified_entry()
    {
        var project = new Project("Sample");
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Level", PrimitiveKind.U8)
            .With("Low", 1).With("High", 2));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Reading") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "level", mode.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var source = Generate((project, bus)).Files
            .Single(f => f.RelativePath.EndsWith("_types.proto", StringComparison.Ordinal)).Contents;

        Assert.Contains("LEVEL_UNSPECIFIED = 0;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Braces_balance_in_every_generated_file()
    {
        foreach (var (name, factory) in Corpus.All())
            foreach (var file in Generate(factory()).Files
                         .Where(f => f.RelativePath.EndsWith(".proto", StringComparison.Ordinal)))
            {
                var opens = file.Contents.Count(c => c == '{');
                var closes = file.Contents.Count(c => c == '}');
                Assert.True(opens == closes,
                    $"{name}/{file.RelativePath}: {opens} '{{' but {closes} '}}'");
            }
    }

    [Fact]
    public void Every_file_says_this_is_a_different_wire_format()
    {
        // Someone reading only the generated file must not conclude these bytes match the C target's.
        var set = Generate(Corpus.Scalars());

        foreach (var file in set.Files.Where(f => f.RelativePath.EndsWith(".proto", StringComparison.Ordinal)))
            Assert.Contains("DIFFERENT WIRE FORMAT", file.Contents, StringComparison.OrdinalIgnoreCase);

        var readme = set.Find("README.md");
        Assert.NotNull(readme);
        Assert.Contains("not the same wire format", readme!.Contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_wire_sizes_or_lengths_are_published()
    {
        // Our byte counts are meaningless here and printing them beside protobuf's would be the exact
        // confusion the gate exists to prevent.
        foreach (var file in Generate(Corpus.DynamicArray()).Files
                     .Where(f => f.RelativePath.EndsWith(".proto", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("OnWireLength", file.Contents, StringComparison.Ordinal);
            Assert.DoesNotContain("ON_WIRE_BYTES", file.Contents, StringComparison.Ordinal);
            Assert.DoesNotContain("MAX_BYTES", file.Contents, StringComparison.Ordinal);
        }
    }

    // ---- crude structural parsing ---------------------------------------------------------------

    private static IEnumerable<(string Name, string Body)> Messages(string source)
    {
        foreach (Match m in Regex.Matches(source, @"^message\s+(\w+)\s*\{(.*?)^\}",
                     RegexOptions.Singleline | RegexOptions.Multiline))
            yield return (m.Groups[1].Value, m.Groups[2].Value);
    }

    /// <summary>Field numbers in a message body: the integer after '=' at the end of a declaration.</summary>
    private static IEnumerable<int> FieldNumbers(string body)
    {
        foreach (Match m in Regex.Matches(body, @"^\s{2}(?:repeated\s+)?[\w.]+\s+\w+\s*=\s*(\d+)",
                     RegexOptions.Multiline))
            yield return int.Parse(m.Groups[1].Value);
    }
}
