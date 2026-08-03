using ProtoDesigner.CodeGen.Cpp;

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
    private static readonly CppGenerator Generator = new();

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

        Assert.Contains("struct Batch {", header, StringComparison.Ordinal);
        Assert.Contains("inline size_t Batch_ConvertToWire(const Batch& msg, uint8_t* wire, size_t cap)", header, StringComparison.Ordinal);
        Assert.Contains("Batch_ConvertToHost(const uint8_t* wire, size_t len, Batch& msg)", header, StringComparison.Ordinal);
        Assert.Contains("static constexpr uint32_t kWireId = 21u;", header, StringComparison.Ordinal);
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
        var set = new CppGenerator().Generate(ir, new GeneratorOptions(Namespace: "proto"));
        var types = set.Files.Single(f => f.RelativePath == "proto_types.h").Contents;
        var header = set.Files.Single(f => f.RelativePath == "shared.h").Contents;

        // Declared once, in the shared header rather than in the bus's.
        Assert.Equal(1, types.Split("struct Header {").Length - 1);
        Assert.DoesNotContain("struct Header {", header, StringComparison.Ordinal);

        // An inner struct is declared before the outer one that contains it.
        Assert.True(types.IndexOf("struct Header {", StringComparison.Ordinal)
                    < types.IndexOf("struct Envelope {", StringComparison.Ordinal),
            "Header must be declared before Envelope, which contains it.");

        // Used by name in both messages, rather than inlined into either.
        Assert.Contains("    Header header;", header, StringComparison.Ordinal);
        Assert.DoesNotContain("header_messageId", header, StringComparison.Ordinal);

        // And the conversion addresses the nested member, however deep.
        Assert.Contains("msg.header.messageId", header, StringComparison.Ordinal);
        Assert.Contains("msg.envelope.head.timestamp", header, StringComparison.Ordinal);
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

        var set = new CppGenerator().Generate(irs, new GeneratorOptions(Namespace: "proto"));

        var types = set.Files.Single(f => f.RelativePath == "proto_types.h").Contents;
        Assert.Equal(1, types.Split("struct Header {").Length - 1);

        // One header per bus, each including the shared declarations rather than repeating them.
        foreach (var ir in irs)
        {
            var busHeader = set.Files.Single(f => f.RelativePath == $"{ir.BusName.ToLowerInvariant()}.h").Contents;
            Assert.Contains("#include \"proto_types.h\"", busHeader, StringComparison.Ordinal);
            Assert.DoesNotContain("struct Header {", busHeader, StringComparison.Ordinal);
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
        var header = new CppGenerator().Generate(ir, new GeneratorOptions(Namespace: "proto")).Files
            .Single(f => f.RelativePath == "shared.h").Contents;

        Assert.Contains("enum class SharedMessageId : uint32_t {", header, StringComparison.Ordinal);
        Assert.Contains("    NotAssigned = 0,", header, StringComparison.Ordinal);
        Assert.Contains("inline SharedMessageId Shared_MessageIdFromWire(uint32_t id)", header, StringComparison.Ordinal);
        Assert.Contains("return SharedMessageId::NotAssigned;", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// The array overload takes its capacity from the type, so the caller cannot pass a wrong one. It is
    /// only safe in the wire-to-host direction for a fixed-size message: for a variable-size one the array
    /// length is the maximum rather than what actually arrived.
    /// </summary>
    [Fact]
    public void A_fixed_size_message_gets_array_overloads_in_both_directions()
    {
        var ir = Corpus.BuildIr(Corpus.PackedBits());
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "control.h").Contents;

        Assert.Contains("Status_ConvertToWire(const Status& msg, uint8_t (&wire)[Status::kMaxBytes])", header, StringComparison.Ordinal);
        Assert.Contains("Status_ConvertToHost(const uint8_t (&wire)[Status::kMaxBytes], Status& msg)", header, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_size_message_gets_no_wire_to_host_array_overload()
    {
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "bulk.h").Contents;

        Assert.Contains("Batch_ConvertToWire(const Batch& msg, uint8_t (&wire)[Batch::kMaxBytes])", header, StringComparison.Ordinal);
        Assert.DoesNotContain("ConvertToHost(const uint8_t (&wire)", header, StringComparison.Ordinal);
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

        Assert.Contains("enum class Mode : uint32_t {", types, StringComparison.Ordinal);
        Assert.Contains("Idle = 0,", types, StringComparison.Ordinal);
        Assert.Contains("Fault = 10,", types, StringComparison.Ordinal);
        // Emitted exactly once even though several fields could reference it.
        var occurrences = types.Split("enum class Mode").Length - 1;
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
