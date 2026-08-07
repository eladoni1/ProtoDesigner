using ProtoDesigner.CodeGen.Proto;
using ProtoDesigner.Core.Validation;
using ProtoDesigner.Core.Validation.Rules;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Runs generated schemas through a real protovalidate runtime and checks that the constraints actually
/// reject what they claim to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the half that protoc cannot do.</b> protoc proves a <c>buf.validate</c> option parses and
/// resolves against the real extension — that the rule exists and is spelled correctly. It never
/// evaluates one. A constraint attached to the wrong field, or carrying a bound off by a factor of ten,
/// or scoped to the array when it belongs on the items, compiles perfectly and protects nothing.
/// </para>
/// <para>
/// So every case here states a bound and then sends a message that breaks it. If the constraints were
/// decorative, the violating payloads would be accepted and these tests would fail — which is the
/// property the suite was missing.
/// </para>
/// </remarks>
public sealed class ProtovalidateConformanceTests(ProtovalidateFixture fixture)
    : IClassFixture<ProtovalidateFixture>
{
    /// <summary>Validates a payload, or returns null when the run opted out of the toolchain.</summary>
    /// <remarks>
    /// Nullable rather than an early <c>Assert</c> so the opt-out has to be handled at every use site;
    /// a case that forgot would dereference null instead of quietly asserting nothing.
    /// </remarks>
    private ProtovalidateReport? Check(string message, string payload) =>
        fixture.Require() ? fixture.Harness!.Validate(fixture.Constrained!, $"pv.{message}", payload) : null;

    /// <summary>The same payload against the schema generated with protovalidate turned off.</summary>
    private ProtovalidateReport? CheckUnconstrained(string message, string payload) =>
        fixture.Require() ? fixture.Harness!.Validate(fixture.Unconstrained!, $"pv.{message}", payload) : null;

    // ---- the control: does anything happen at all? ------------------------------------------------

    [Fact]
    public void A_conforming_message_passes_every_constraint()
    {
        // If this ever goes red, the constraints are too strict rather than absent — a failure mode just
        // as real as the one below, and one a violation-only suite would never notice.
        if (Check("Limits", Payloads.ValidLimits) is not { } report) return;

        Assert.True(report.Valid, $"a message inside every declared range was rejected: {report}");
    }

    [Fact]
    public void The_constraints_are_what_does_the_rejecting()
    {
        // The negative control for the whole file. The same out-of-range payload, against the same schema
        // generated without protovalidate, must be accepted — otherwise something else is rejecting it and
        // every test here proves nothing about our constraints.
        const string outOfRange = """
            {"ratio":250,"tilt":0,"pressure":1000,"free":0}
            """;

        if (Check("Limits", outOfRange) is not { } constrained) return;

        Assert.False(constrained.Valid);
        Assert.True(CheckUnconstrained("Limits", outOfRange)!.Valid,
            "the payload was rejected even with constraints turned off, so these tests are measuring "
            + "something other than the constraints");
    }

    // ---- numeric ranges ---------------------------------------------------------------------------

    [Theory]
    // field, payload value, whether it should be accepted — bounds are inclusive on both ends.
    [InlineData("ratio", "0", true)]
    [InlineData("ratio", "100", true)]
    [InlineData("ratio", "101", false)]
    [InlineData("ratio", "250", false)]
    public void An_unsigned_range_is_enforced_at_both_ends(string field, string value, bool expected)
    {
        if (Check("Limits", Payloads.Limits(field, value)) is not { } report) return;

        Assert.Equal(expected, report.Valid);
        if (!expected) Assert.True(report.Mentions(field), $"the violation named the wrong field: {report}");
    }

    [Theory]
    [InlineData("-90", true)]
    [InlineData("90", true)]
    [InlineData("-91", false)]
    [InlineData("91", false)]
    public void A_signed_range_is_enforced_including_its_negative_bound(string value, bool expected)
    {
        // The negative bound is worth its own case: it reaches protovalidate as a two's-complement varint,
        // so a bound that survived protoc could still have arrived as a huge positive number.
        if (Check("Limits", Payloads.Limits("tilt", value)) is not { } report) return;

        Assert.Equal(expected, report.Valid);
    }

    [Theory]
    [InlineData("1000", true)]
    [InlineData("1015", true)]
    [InlineData("999", false)]
    [InlineData("1016", false)]
    public void An_offset_field_is_constrained_by_its_real_range_not_its_wire_code(string value, bool expected)
    {
        // The design decision this target rests on. `pressure` is 1000..1015 with an offset of 1000, so on
        // our wire it travels as 0..15. Protobuf carries the value, so the constraint must be the real
        // range — if the offset had leaked through, 0 would be accepted and 1000 refused, exactly
        // backwards.
        if (Check("Limits", Payloads.Limits("pressure", value)) is not { } report) return;

        Assert.Equal(expected, report.Valid);
    }

    [Fact]
    public void A_field_whose_range_is_its_type_s_full_span_carries_no_constraint()
    {
        // `free` is a u32 with no declared range, so uint32 already says everything there is to say.
        // Emitting 0..4294967295 would read as a designed limit when it is the absence of one — and
        // uint32's own maximum must still be accepted.
        if (Check("Limits", Payloads.Limits("free", "4294967295")) is not { } report) return;

        Assert.True(report.Valid, $"a full-span value was rejected: {report}");
    }

    // ---- the wider scalar groups ------------------------------------------------------------------

    [Theory]
    [InlineData("counter", "1000000000000", true)]
    [InlineData("counter", "1000000000001", false)]
    [InlineData("drift", "-5000000000", true)]
    [InlineData("drift", "-5000000001", false)]
    [InlineData("level", "1", true)]
    [InlineData("level", "1.5", false)]
    [InlineData("precise", "0.5", true)]
    [InlineData("precise", "1.5", false)]
    [InlineData("letter", "126", true)]
    [InlineData("letter", "127", false)]
    public void The_wider_rule_groups_are_enforced(string field, string value, bool expected)
    {
        // uint64, int64, float, double and char→uint32 all emit their own rule group. Each was previously
        // generated and compiled but never evaluated, so a group name that was merely plausible would
        // have passed unnoticed. The 64-bit bounds are past 2^32 deliberately: a bound truncated to 32
        // bits would still compile and would accept these.
        if (Check("Wide", Payloads.Wide(field, value)) is not { } report) return;

        Assert.Equal(expected, report.Valid);
        if (!expected) Assert.True(report.Mentions(field), $"the violation named the wrong field: {report}");
    }

    [Fact]
    public void A_bool_carries_no_numeric_constraint()
    {
        // Bool has a 0..1 range in the model and no rule group in protovalidate. Emitting `bool.gte` would
        // not compile, so this is really a check that RuleGroup keeps returning null for it.
        if (Check("Wide", Payloads.Wide("flag", "true")) is not { } set) return;

        Assert.True(set.Valid, $"a bool set to true was rejected: {set}");
        Assert.True(Check("Wide", Payloads.Wide("flag", "false"))!.Valid);
    }

    // ---- collections ------------------------------------------------------------------------------

    [Theory]
    [InlineData("[1,2,3,4,5,6,7,8]", true)]
    [InlineData("[]", true)]
    [InlineData("[1,2,3,4,5,6,7,8,9]", false)]
    public void A_dynamic_array_is_capped_at_its_declared_capacity(string samples, bool expected)
    {
        // Capacity 8, no declared minimum — so an empty array is legal. protobuf's `repeated` has no
        // length of its own, so without max_items the capacity a user designed is simply gone.
        if (Check("Collections", Payloads.Collections(samples: samples)) is not { } report) return;

        Assert.Equal(expected, report.Valid);
    }

    [Theory]
    [InlineData("[1,2]", true)]          // at the floor
    [InlineData("[1,2,3,4,5,6]", true)]  // at the ceiling
    [InlineData("[1]", false)]           // below it
    [InlineData("[]", false)]            // the case the whole feature exists for
    [InlineData("[1,2,3,4,5,6,7]", false)]
    public void A_dynamic_array_with_a_declared_minimum_is_bounded_at_both_ends(string readings, bool expected)
    {
        // `readings` is declared 2..6. A dynamic array without a minimum accepts an empty list — that is
        // what `A_dynamic_array_is_capped_at_its_declared_capacity` pins one test above. Declaring a floor
        // is the difference, and this is the only check that proves the floor reaches a consumer.
        if (Check("Bounded", Payloads.Bounded(readings)) is not { } report) return;

        Assert.Equal(expected, report.Valid);
        if (!expected) Assert.True(report.Mentions("readings"), $"the violation named the wrong field: {report}");
    }

    [Fact]
    public void A_declared_minimum_is_what_rejects_a_short_array()
    {
        // The negative control, matching the one at the top of the file: the same short array against the
        // same schema generated without protovalidate must be accepted, or something other than our
        // min_items is doing the rejecting.
        if (Check("Bounded", Payloads.Bounded("[]")) is not { } constrained) return;

        Assert.False(constrained.Valid);
        Assert.True(CheckUnconstrained("Bounded", Payloads.Bounded("[]"))!.Valid,
            "an empty array was rejected even with constraints turned off");
    }

    [Fact]
    public void An_element_range_is_enforced_on_the_items_not_the_array()
    {
        // `samples` elements are 0..100. The constraint has to sit under `repeated.items`; put it on the
        // array itself and protovalidate would be comparing a list against a number.
        if (Check("Collections", Payloads.Collections(samples: "[1,2,250]")) is not { } report) return;

        Assert.False(report.Valid, "an element outside the declared element range was accepted");
        Assert.True(report.Mentions("samples"), $"the violation named the wrong field: {report}");
    }

    [Theory]
    [InlineData("[\"MODE_IDLE\",\"MODE_RUN\",\"MODE_IDLE\"]", true)]
    [InlineData("[\"MODE_IDLE\",\"MODE_RUN\"]", false)]
    [InlineData("[1,2,3,4]", false)]
    public void A_fixed_array_is_pinned_to_exactly_its_count(string modes, bool expected)
    {
        if (Check("Collections", Payloads.Collections(modes: modes)) is not { } report) return;

        Assert.Equal(expected, report.Valid);
    }

    [Fact]
    public void An_undefined_enum_value_is_rejected()
    {
        // What an enum meant on the native wire and what protobuf otherwise lets straight through: an
        // unknown number is a legal proto3 enum value unless something says otherwise.
        if (Check("Collections", Payloads.Collections(mode: "77")) is not { } report) return;

        Assert.False(report.Valid, "a value outside the declared enum set was accepted");
        Assert.True(report.Mentions("mode"), $"the violation named the wrong field: {report}");
    }

    [Fact]
    public void An_undefined_enum_value_inside_an_array_is_rejected()
    {
        // The repeated-enum path, which is scoped differently from the scalar one: the rule belongs under
        // `repeated.items.enum`, and a rule written for a scalar enum silently guards nothing here.
        if (Check("Collections", Payloads.Collections(modes: "[\"MODE_IDLE\",77,\"MODE_RUN\"]"))
            is not { } report) return;

        Assert.False(report.Valid, "an undefined enum value inside a repeated field was accepted");
        Assert.True(report.Mentions("modes"), $"the violation named the wrong field: {report}");
    }

    // ---- the fixture itself -----------------------------------------------------------------------

    [Fact]
    public void Every_message_in_the_fixture_is_protobuf_exportable()
    {
        // If the gate refused one of these, the schema under test would quietly be missing a message and
        // its cases would fail for a reason that has nothing to do with protovalidate.
        var (project, _) = ProtovalidateFixture.BuildProject();
        var diagnostics = new Validator(ProtobufRules.All).Validate(project);

        Assert.DoesNotContain(diagnostics, d => d.Severity == Severity.Error);
    }
}

/// <summary>
/// The payloads, kept together so a case reads as the value under test rather than as JSON.
/// </summary>
internal static class Payloads
{
    public const string ValidLimits = """
        {"ratio":50,"tilt":-45,"pressure":1008,"free":123456}
        """;

    /// <summary>A conforming <c>Limits</c> with one field replaced.</summary>
    public static string Limits(string field, string value)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ratio"] = "50", ["tilt"] = "-45", ["pressure"] = "1008", ["free"] = "123456",
        };
        values[field] = value;
        return Object(values);
    }

    /// <summary>A conforming <c>Wide</c> with one field replaced.</summary>
    public static string Wide(string field, string value)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["counter"] = "1", ["drift"] = "-1", ["level"] = "0.25", ["precise"] = "0.5",
            ["flag"] = "true", ["letter"] = "65",
        };
        values[field] = value;
        return Object(values);
    }

    /// <summary>A <c>Bounded</c> carrying the given readings, whose array is declared 2..6.</summary>
    /// <remarks>
    /// The count member is left unset deliberately. protobuf's <c>repeated</c> carries its own length, so
    /// the count field is redundant there and nothing cross-checks the two — only the array's own bounds
    /// are under test.
    /// </remarks>
    public static string Bounded(string readings) => $"{{\"readings\":{readings}}}";

    /// <summary>A conforming <c>Collections</c> with any part overridden.</summary>
    public static string Collections(string? mode = null, string? modes = null, string? samples = null) =>
        Object(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = mode ?? "\"MODE_RUN\"",
            ["modes"] = modes ?? "[\"MODE_IDLE\",\"MODE_RUN\",\"MODE_IDLE\"]",
            ["samples"] = samples ?? "[1,2,3]",
        });

    private static string Object(Dictionary<string, string> values) =>
        "{" + string.Join(",", values.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}";
}

/// <summary>
/// Generates the fixture schema once, compiles it to a descriptor set, and builds the Go harness.
/// </summary>
/// <remarks>
/// A class fixture rather than per-test setup: <c>go build</c> and a protoc run cost far more than the
/// validations they enable, and every case shares one schema by design — the point is that a single
/// generated schema behaves correctly across all of them.
/// </remarks>
public sealed class ProtovalidateFixture : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"pd-protovalidate-{Guid.NewGuid():N}");

    /// <summary>Descriptor set for the schema generated with constraints, or null when unavailable.</summary>
    public string? Constrained { get; }

    /// <summary>The same schema generated without them — the negative control.</summary>
    public string? Unconstrained { get; }

    // Internal because the harness type is: xunit needs the fixture public, not its plumbing.
    internal ProtovalidateHarness? Harness { get; }

    /// <summary>Why the checks cannot run, or null when they can.</summary>
    public string? Unavailable { get; }

    public ProtovalidateFixture()
    {
        var protoc = ProtocLocator.Find();
        if (protoc is null)
        {
            Unavailable = "protoc was not found, so the schema could not be compiled to a descriptor set";
            return;
        }

        if (!protoc.HasValidateProto)
        {
            Unavailable = "Buf's validate.proto was not found, so the constraints could not be compiled";
            return;
        }

        Harness = GoLocator.Find();
        if (Harness is null)
        {
            Unavailable = GoLocator.Failure ?? "the protovalidate harness could not be built";
            return;
        }

        var (project, bus) = BuildProject();
        var ir = new IrBuilder().Build(project, bus);
        var generator = new ProtoGenerator();

        Constrained = Compile(protoc, generator, ir, protovalidate: true, "constrained");
        Unconstrained = Compile(protoc, generator, ir, protovalidate: false, "plain");
    }

    private string Compile(ProtocToolchain protoc, ProtoGenerator generator, ProtocolIr ir,
        bool protovalidate, string label)
    {
        var set = generator.Generate(ir, new GeneratorOptions(Namespace: "pv")
            .With(ProtoGenerator.ProtovalidateOption, protovalidate ? "true" : "false"));

        var dir = Path.Combine(_directory, label);
        var descriptor = Path.Combine(dir, "schema.desc");

        var (exitCode, output) = protoc.CompileDescriptorSet(set, dir, descriptor);
        if (exitCode != 0)
            throw new InvalidOperationException($"protoc rejected the {label} fixture schema:\n{output}");

        return descriptor;
    }

    /// <summary>
    /// Fails the calling test when the toolchain is missing, unless the run explicitly opted out.
    /// </summary>
    /// <remarks>
    /// Same policy as protoc and MSVC: absent tooling is a red test, not a quiet pass. A suite that
    /// validated nothing while reporting success is the outcome this whole file exists to prevent.
    /// </remarks>
    /// <returns>True when the case should run; false when the run explicitly opted out.</returns>
    public bool Require()
    {
        if (Unavailable is null) return true;

        Assert.True(GoLocator.SkipRequested,
            $"the protovalidate runtime check could not run: {Unavailable}. Install Go and protoc, or "
            + $"set {GoLocator.SkipVariable}=1 to accept a suite that proves the constraints compile but "
            + "never proves one rejects anything.");

        return false;
    }

    /// <summary>
    /// The fixture protocol: one message per constraint family, sized so every rule the generator can
    /// emit is exercised by something.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <see cref="Corpus"/> entry. The corpus exists to cover wire layouts and is
    /// shared with the C golden and cross-check suites; this one is shaped by protobuf's rule groups,
    /// which is a different axis, and widening the corpus for it would drag every other suite along.
    /// </remarks>
    internal static (Project Project, Bus Bus) BuildProject()
    {
        var p = new Project("Protovalidate");

        // Every integer here is 32 bits: protobuf's integers are 32- and 64-bit, and anything narrower is
        // refused by the gate rather than silently widened.
        var u32 = p.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var ratio = p.Types.Add(new ParameterType(TypeId.New(), "Ratio", PrimitiveKind.U32,
            new NumericRange(0, 100)));
        var tilt = p.Types.Add(new ParameterType(TypeId.New(), "Tilt", PrimitiveKind.I32,
            new NumericRange(-90, 90)));
        var pressure = p.Types.Add(new ParameterType(TypeId.New(), "Pressure", PrimitiveKind.U32,
            new NumericRange(1000, 1015)));

        var counter = p.Types.Add(new ParameterType(TypeId.New(), "Counter", PrimitiveKind.U64,
            new NumericRange(0, 1_000_000_000_000)));
        var drift = p.Types.Add(new ParameterType(TypeId.New(), "Drift", PrimitiveKind.I64,
            new NumericRange(-5_000_000_000, 5_000_000_000)));
        var level = p.Types.Add(new ParameterType(TypeId.New(), "Level", PrimitiveKind.F32,
            new NumericRange(-1, 1)));
        var precise = p.Types.Add(new ParameterType(TypeId.New(), "Precise", PrimitiveKind.F64,
            new NumericRange(0, 1)));
        var flag = p.Types.Add(new ParameterType(TypeId.New(), "Flag", PrimitiveKind.Bool));
        var letter = p.Types.Add(new ParameterType(TypeId.New(), "Letter", PrimitiveKind.Char,
            new NumericRange(32, 126)) { WireBits = 32 });

        var mode = p.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
            .With("Idle", 0).With("Run", 5));
        var modes = p.Types.Add(new ArrayType(TypeId.New(), "Modes", mode.Id, new ArrayLength.Fixed(3)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);

        var limits = new Message(MessageId.New(), "Limits") { WireId = 1 };
        limits.Fields.Add(new FieldBinding(FieldId.New(), "ratio", ratio.Id));
        limits.Fields.Add(new FieldBinding(FieldId.New(), "tilt", tilt.Id));
        // The offset case: 1000..1015 travels as 0..15 on our wire and as 1000..1015 in protobuf.
        limits.Fields.Add(new FieldBinding(FieldId.New(), "pressure", pressure.Id,
            new FieldEncoding { Transform = new ScalarTransform(1000, 1) }));
        limits.Fields.Add(new FieldBinding(FieldId.New(), "free", u32.Id));

        var wide = new Message(MessageId.New(), "Wide") { WireId = 2 };
        wide.Fields.Add(new FieldBinding(FieldId.New(), "counter", counter.Id));
        wide.Fields.Add(new FieldBinding(FieldId.New(), "drift", drift.Id));
        wide.Fields.Add(new FieldBinding(FieldId.New(), "level", level.Id));
        wide.Fields.Add(new FieldBinding(FieldId.New(), "precise", precise.Id));
        wide.Fields.Add(new FieldBinding(FieldId.New(), "flag", flag.Id));
        // char is 8 bits by nature, so it needs widening explicitly to survive the gate. WireBits on
        // the type is only the default a new binding starts from; this fixture builds bindings directly.
        wide.Fields.Add(new FieldBinding(FieldId.New(), "letter", letter.Id,
            new FieldEncoding { BitWidth = 32 }));

        var count = new FieldBinding(FieldId.New(), "count", u32.Id);
        var samples = p.Types.Add(new ArrayType(TypeId.New(), "Samples", ratio.Id,
            new ArrayLength.CountFromField(count.Id, 8)));

        var collections = new Message(MessageId.New(), "Collections") { WireId = 3 };
        collections.Fields.Add(new FieldBinding(FieldId.New(), "mode", mode.Id));
        collections.Fields.Add(new FieldBinding(FieldId.New(), "modes", modes.Id));
        collections.Fields.Add(count);
        collections.Fields.Add(new FieldBinding(FieldId.New(), "samples", samples.Id));

        // A variable array with both ends declared — the case a plain capacity cannot express, and the
        // only one that proves a lower bound survives as far as a consumer. Count-driven rather than
        // length-prefixed because IrBuilder rejects the synthetic `__length` node a prefix produces, so
        // that rule has never reached a generator.
        var readingCount = new FieldBinding(FieldId.New(), "readingCount", u32.Id);
        var readings = p.Types.Add(new ArrayType(TypeId.New(), "Readings", ratio.Id,
            new ArrayLength.CountFromField(readingCount.Id, MaxCount: 6, MinCount: 2)));

        var bounded = new Message(MessageId.New(), "Bounded") { WireId = 4 };
        bounded.Fields.Add(readingCount);
        bounded.Fields.Add(new FieldBinding(FieldId.New(), "readings", readings.Id));

        bus.Messages.Add(limits);
        bus.Messages.Add(wide);
        bus.Messages.Add(collections);
        bus.Messages.Add(bounded);
        p.Buses.Add(bus);

        return (p, bus);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
