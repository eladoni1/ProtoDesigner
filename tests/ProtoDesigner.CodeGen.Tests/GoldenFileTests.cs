using ProtoDesigner.CodeGen.C;
using ProtoDesigner.CodeGen.Proto;
using ProtoDesigner.Core.Validation;
using ProtoDesigner.Core.Validation.Rules;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Generates the C++ for each corpus protocol and diffs it against a checked-in expected file, so any
/// change to generator output shows up in review as a concrete diff rather than an invisible behaviour
/// change.
/// </summary>
/// <remarks>
/// To accept new output after an intentional generator change, set the environment variable
/// <c>PROTODESIGNER_UPDATE_GOLDEN=1</c> and run the tests once; the files are rewritten and the run
/// fails loudly so the update can't be mistaken for a pass.
/// </remarks>
public class GoldenFileTests
{
    private static readonly CGenerator Generator = new();

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Corpus.All()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void The_generated_cpp_matches_the_golden_file(string corpusName)
    {
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();   // one call — project and bus must come from the same instance
        var ir = new IrBuilder().Build(project, bus);

        var set = Generator.Generate(ir, new GeneratorOptions(Namespace: "proto"));

        // Two goldens per corpus: the shared declarations and the bus's own messages. Both matter —
        // a type moving between them is exactly the kind of change that should show up in review. They
        // go through one call so that an update pass writes both; failing on the first would leave the
        // second stale and need a second run to notice.
        AssertMatchGoldens(
            (Path.Combine(GoldenDirectory(), $"{corpusName}_types.h"),
             set.Files.Single(f => f.RelativePath == "proto_types.h").Contents),
            (Path.Combine(GoldenDirectory(), $"{corpusName}.h"),
             BusHeader(set).Contents));
    }

    /// <summary>The one bus header in a set: not the runtime, not the shared declarations.</summary>
    private static GeneratedFile BusHeader(GeneratedFileSet set) => set.Files.Single(f =>
        f.RelativePath.EndsWith(".h", StringComparison.Ordinal) &&
        f.RelativePath != "protodesigner_runtime.h" &&
        !f.RelativePath.EndsWith("_types.h", StringComparison.Ordinal));

    // ---- the protobuf target ----------------------------------------------------------------------

    private static readonly ProtoGenerator ProtoGenerator = new();

    /// <summary>
    /// The corpus entries the compatibility gate lets through, which are the only ones a golden should
    /// exist for.
    /// </summary>
    /// <remarks>
    /// Generating a schema for a refused message is possible — the generator does not re-check the gate,
    /// because the caller narrows the scope — but checking one in would enshrine output we would never
    /// ship, and reviewers would start treating it as correct.
    /// </remarks>
    public static TheoryData<string> ExportableCorpusNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Exportable()) data.Add(name);
        return data;
    }

    private static IEnumerable<string> Exportable() =>
        Corpus.All()
            .Where(c => !new Validator(ProtobufRules.All).Validate(c.Factory().Item1)
                .Any(d => d.Severity == Severity.Error))
            .Select(c => c.Name);

    [Theory]
    [MemberData(nameof(ExportableCorpusNames))]
    public void The_generated_proto_matches_the_golden_file(string corpusName)
    {
        // Constraints on, because that is the default and the interesting half. protoc proves the schema
        // compiles and protovalidate proves the rules bite; this is what makes a change to either
        // *visible* — a dropped constraint or a renumbered field otherwise passes both while silently
        // changing what consumers see.
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();
        var ir = new IrBuilder().Build(project, bus);

        var set = ProtoGenerator.Generate(ir, new GeneratorOptions(Namespace: "proto"));

        AssertMatchGoldens(
            (Path.Combine(GoldenDirectory(), $"{corpusName}_types.proto"),
             set.Files.Single(f => f.RelativePath == "proto_types.proto").Contents),
            (Path.Combine(GoldenDirectory(), $"{corpusName}.proto"),
             BusProto(set).Contents));
    }

    /// <summary>The bus's own schema: the one .proto that is not the shared declarations.</summary>
    private static GeneratedFile BusProto(GeneratedFileSet set) => set.Files.Single(f =>
        f.RelativePath.EndsWith(".proto", StringComparison.Ordinal) &&
        !f.RelativePath.EndsWith("_types.proto", StringComparison.Ordinal));

    [Fact]
    public void The_gate_withholds_exactly_the_corpus_entries_protobuf_cannot_express()
    {
        // Pins which entries have no golden, and why. Without this the golden set could quietly shrink —
        // a gate that started refusing everything would look like a passing suite with fewer files.
        var withheld = Corpus.All().Select(c => c.Name).Except(Exportable()).Order().ToArray();

        // Nearly all of it, and that is not a regression. The corpus is built from u8 and u16 because it
        // exists to exercise *wire layouts*, and protobuf's integers are 32- and 64-bit — so the
        // narrow-integer rule withholds almost every entry. `raw-floats` survives because a float is a
        // float in both worlds.
        //
        // `biased-signed` would be withheld anyway: its transform is `MinimumScale(-100..100, 8)`, which
        // is 200/255 rather than 1, so it is quantization and not the offset-only case protobuf carries.
        //
        // Proto output is still covered where it matters — `constraints.proto` below is goldened from a
        // fixture built for protobuf's rule groups, which is the right place for that coverage. Widening
        // the corpus to suit protobuf would drag the C golden and cross-check suites along for a
        // different axis entirely.
        Assert.Equal(
            new[]
            {
                "biased-signed", "dynamic-array", "packed-bits", "quantized", "scalars", "shared-struct",
                "struct-and-array",
            },
            withheld);
    }

    [Fact]
    public void The_constraint_rich_schema_matches_its_golden_file()
    {
        // The corpus is shaped for wire layouts and declares almost no ranges, so its goldens would show
        // nothing if every protovalidate constraint vanished. This fixture is shaped for the rule groups
        // instead — every constraint the generator can emit appears in it, which makes a dropped or
        // rescoped rule a visible diff rather than a silent one.
        var (project, bus) = ProtovalidateFixture.BuildProject();
        var ir = new IrBuilder().Build(project, bus);

        var set = ProtoGenerator.Generate(ir, new GeneratorOptions(Namespace: "pv"));

        AssertMatchGoldens(
            (Path.Combine(GoldenDirectory(), "constraints_types.proto"),
             set.Files.Single(f => f.RelativePath == "pv_types.proto").Contents),
            (Path.Combine(GoldenDirectory(), "constraints.proto"),
             BusProto(set).Contents));
    }

    [Fact]
    public void The_runtime_header_matches_its_golden_file()
    {
        var ir = Corpus.BuildIr(Corpus.Scalars());
        var set = Generator.Generate(ir, new GeneratorOptions());
        var runtime = set.Files.Single(f => f.RelativePath == "protodesigner_runtime.h");

        AssertMatchesGolden(Path.Combine(GoldenDirectory(), "protodesigner_runtime.h"), runtime.Contents);
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var ir = Corpus.BuildIr(Corpus.StructAndArray());
        var first = Generator.Generate(ir, new GeneratorOptions());
        var second = Generator.Generate(ir, new GeneratorOptions());

        Assert.Equal(first.Files.Count, second.Files.Count);
        for (var i = 0; i < first.Files.Count; i++)
        {
            Assert.Equal(first.Files[i].RelativePath, second.Files[i].RelativePath);
            Assert.Equal(first.Files[i].Contents, second.Files[i].Contents);
        }
    }

    [Fact]
    public void Every_message_produces_a_host_struct_with_both_conversions()
    {
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "bulk.h").Contents;

        Assert.Contains("typedef struct proto_Batch {", header, StringComparison.Ordinal);
        Assert.Contains("PD_INLINE size_t proto_Batch_ConvertToWire(const proto_Batch* msg, uint8_t* wire, size_t cap)", header, StringComparison.Ordinal);
        Assert.Contains("proto_Batch_ConvertToHost(const uint8_t* wire, size_t len, proto_Batch* msg)", header, StringComparison.Ordinal);

        // A C struct cannot carry constants, so they become macros ahead of it.
        Assert.Contains("#define PROTO_BATCH_WIRE_ID 21u", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// The header has to compile as C and as C++, which is the whole reason this target is not a C++
    /// generator. The guards are what make that true, so their absence is worth failing on.
    /// </summary>
    [Fact]
    public void Every_header_guards_itself_for_cplusplus()
    {
        var ir = Corpus.BuildIr(Corpus.SharedStruct());
        var set = Generator.Generate(ir, new GeneratorOptions());

        foreach (var file in set.Files.Where(f => f.RelativePath.EndsWith(".h", StringComparison.Ordinal)))
        {
            Assert.Contains("#ifdef __cplusplus", file.Contents, StringComparison.Ordinal);
            Assert.Contains("extern \"C\" {", file.Contents, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// C has no overloading, so every emitted function name must be unique. The C++ target used to lean
    /// on overloads for the fixed-capacity convenience variants; carrying that habit across would produce
    /// a header that does not compile at all.
    /// </summary>
    [Fact]
    public void No_function_name_is_emitted_twice()
    {
        var ir = Corpus.BuildIr(Corpus.PackedBits());
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "control.h").Contents;

        var definitions = header
            .Split('\n')
            .Where(l => l.Contains("PD_INLINE ", StringComparison.Ordinal))
            .Select(l => l[..l.IndexOf('(', StringComparison.Ordinal)].Split(' ').Last())
            .ToList();

        Assert.NotEmpty(definitions);
        Assert.Equal(definitions.Count, definitions.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A struct type is declared once and referenced by name. It used to be flattened into every message
    /// that used it, so a Header shared by ten messages appeared as ten copies of its members and the
    /// shape the user designed vanished from the output.
    /// </summary>
    [Fact]
    public void A_shared_struct_is_declared_once_and_referenced_by_name()
    {
        var ir = Corpus.BuildIr(Corpus.SharedStruct());
        var set = new CGenerator().Generate(ir, new GeneratorOptions(Namespace: "proto"));
        var types = set.Files.Single(f => f.RelativePath == "proto_types.h").Contents;
        var header = set.Files.Single(f => f.RelativePath == "shared.h").Contents;

        // Declared once, in the shared header rather than in the bus's.
        Assert.Equal(1, types.Split("typedef struct proto_Header {").Length - 1);
        Assert.DoesNotContain("typedef struct proto_Header {", header, StringComparison.Ordinal);

        // An inner struct is declared before the outer one that contains it.
        Assert.True(types.IndexOf("typedef struct proto_Header {", StringComparison.Ordinal)
                    < types.IndexOf("typedef struct proto_Envelope {", StringComparison.Ordinal),
            "Header must be declared before Envelope, which contains it.");

        // Used by name in both messages, rather than inlined into either.
        Assert.Contains("    proto_Header header;", header, StringComparison.Ordinal);
        Assert.DoesNotContain("header_messageId", header, StringComparison.Ordinal);

        // And the conversion addresses the nested member, however deep.
        Assert.Contains("msg->header.messageId", header, StringComparison.Ordinal);
        Assert.Contains("msg->envelope.head.timestamp", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two buses sharing a struct must be includable in the same translation unit. Declaring the struct
    /// in each bus header made that a redefinition error the moment generated code stopped flattening.
    /// </summary>
    [Fact]
    public void Types_shared_across_buses_are_declared_once_for_the_whole_set()
    {
        var (project, _) = Corpus.TwoBusesSharingAStruct();
        var irs = project.Buses.Select(b => new IrBuilder().Build(project, b)).ToList();

        var set = new CGenerator().Generate(irs, new GeneratorOptions(Namespace: "proto"));

        var types = set.Files.Single(f => f.RelativePath == "proto_types.h").Contents;
        Assert.Equal(1, types.Split("typedef struct proto_Header {").Length - 1);

        // One header per bus, each including the shared declarations rather than repeating them.
        foreach (var ir in irs)
        {
            var busHeader = set.Files.Single(f => f.RelativePath == $"{ir.BusName.ToLowerInvariant()}.h").Contents;
            Assert.Contains("#include \"proto_types.h\"", busHeader, StringComparison.Ordinal);
            Assert.DoesNotContain("typedef struct proto_Header {", busHeader, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Ids are unique within a bus and start at 1, so NotAssigned = 0 can never collide with a real
    /// message — which is what makes it usable as "I do not recognise this frame".
    /// </summary>
    [Fact]
    public void A_bus_gets_a_message_id_enum_and_a_lookup()
    {
        var ir = Corpus.BuildIr(Corpus.SharedStruct());
        var header = new CGenerator().Generate(ir, new GeneratorOptions(Namespace: "proto")).Files
            .Single(f => f.RelativePath == "shared.h").Contents;

        Assert.Contains("typedef enum proto_SharedMessageId {", header, StringComparison.Ordinal);
        Assert.Contains("    proto_SharedMessageId_NotAssigned = 0,", header, StringComparison.Ordinal);
        Assert.Contains("PD_INLINE proto_SharedMessageId proto_Shared_MessageIdFromWire(uint32_t id)", header, StringComparison.Ordinal);
        Assert.Contains("return proto_SharedMessageId_NotAssigned;", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// A buffer sized from the message's own macro is the safe way to call the encoder. C cannot check
    /// that for you the way a C++ array reference could, so the macro and the capacity argument are what
    /// the caller is given instead.
    /// </summary>
    [Fact]
    public void Every_message_publishes_the_buffer_size_its_encoder_needs()
    {
        var ir = Corpus.BuildIr(Corpus.PackedBits());
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "control.h").Contents;

        Assert.Contains("#define PROTO_STATUS_MAX_BYTES", header, StringComparison.Ordinal);
        Assert.Contains("uint8_t* wire, size_t cap", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// A derived scale carries far more digits than float's ~7. Emitting it with an `f` suffix put the top
    /// of a -40..70 range at 254.99999 of 255 codes, which the cast to an integer then truncated to 254.
    /// </summary>
    [Fact]
    public void A_fractional_transform_is_emitted_as_a_double_literal()
    {
        var ir = Corpus.BuildIr(Corpus.Quantized());
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "sensors.h").Contents;

        // The literal must name the same double the C# side computes from the same decimal.
        var expected = ((double)(110m / 255m)).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(expected, header, StringComparison.Ordinal);
        Assert.DoesNotContain(expected + "f", header, StringComparison.Ordinal);

        // A float literal would round 110/255 to ~7 digits; a double keeps ~17.
        Assert.True(expected.Length >= 18, $"Expected a full-precision literal, got '{expected}'.");
    }

    [Fact]
    public void An_enum_is_emitted_once_with_its_declared_members()
    {
        var ir = Corpus.BuildIr(Corpus.PackedBits());
        var types = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "proto_types.h").Contents;

        Assert.Contains("typedef enum proto_Mode {", types, StringComparison.Ordinal);

        // C enum members share the enclosing scope, so the type name is part of the member's name or two
        // enums with an 'Idle' member could not coexist in one translation unit.
        Assert.Contains("proto_Mode_Idle = 0,", types, StringComparison.Ordinal);
        Assert.Contains("proto_Mode_Fault = 10,", types, StringComparison.Ordinal);

        // Emitted exactly once even though several fields could reference it.
        var occurrences = types.Split("typedef enum proto_Mode").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void A_readme_is_emitted_when_requested_and_omitted_when_not()
    {
        var ir = Corpus.BuildIr(Corpus.Scalars());

        var withReadme = Generator.Generate(ir, new GeneratorOptions(IncludeReadme: true));
        Assert.NotNull(withReadme.Find("README.md"));

        var without = Generator.Generate(ir, new GeneratorOptions(IncludeReadme: false));
        Assert.Null(without.Find("README.md"));
    }

    // ---- golden helpers -------------------------------------------------------------------------

    /// <summary>
    /// Locates the Golden directory next to the test .csproj, so goldens live in source control rather
    /// than in bin/. Anchoring on the .csproj matters: the build copies Golden/ into the output
    /// directory, and a naive "first Golden folder above me" walk would find that copy and silently
    /// compare a stale artefact against itself.
    /// </summary>
    private static string GoldenDirectory()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.GetFiles(dir, "*.csproj").Length > 0)
            {
                var golden = Path.Combine(dir, "Golden");
                Directory.CreateDirectory(golden);
                return golden;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate the test project directory holding Golden/.");
    }

    private static bool ShouldUpdate =>
        Environment.GetEnvironmentVariable("PROTODESIGNER_UPDATE_GOLDEN") is "1" or "true";

    private static void AssertMatchesGolden(string path, string actual) =>
        AssertMatchGoldens((path, actual));

    /// <summary>
    /// Compares several outputs against their golden files, or rewrites them all in update mode.
    /// </summary>
    /// <remarks>
    /// Writing every file before failing matters: with one assert per file, an update pass would write
    /// the first, fail, and leave the rest stale — and the run after that would report a mismatch on a
    /// file the update was supposed to have refreshed.
    /// </remarks>
    private static void AssertMatchGoldens(params (string Path, string Actual)[] goldens)
    {
        var normalised = goldens
            .Select(g => (g.Path, Actual: g.Actual.Replace("\r\n", "\n")))
            .ToArray();

        var written = new List<string>();
        foreach (var (path, actual) in normalised)
        {
            if (!ShouldUpdate && File.Exists(path)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            written.Add(path);
        }

        if (written.Count > 0)
            Assert.Fail($"Golden file(s) written:\n  {string.Join("\n  ", written)}\n"
                        + "Review the diff and re-run without PROTODESIGNER_UPDATE_GOLDEN.");

        foreach (var (path, actual) in normalised)
        {
            var expected = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal(expected, actual);
        }
    }
}
