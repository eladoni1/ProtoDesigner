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
        var header = set.Files.Single(f => f.RelativePath.EndsWith(".h") && f.RelativePath != "protodesigner_runtime.h");

        var goldenPath = Path.Combine(GoldenDirectory(), $"{corpusName}.h");
        AssertMatchesGolden(goldenPath, header.Contents);
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

        Assert.Contains("struct Batch {", header, StringComparison.Ordinal);
        Assert.Contains("inline size_t ConvertToWire(const Batch& msg, uint8_t* wire, size_t cap)", header, StringComparison.Ordinal);
        Assert.Contains("ConvertToHost(const uint8_t* wire, size_t len, Batch& msg)", header, StringComparison.Ordinal);
        Assert.Contains("static constexpr uint32_t kWireId = 21u;", header, StringComparison.Ordinal);
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

        Assert.Contains("ConvertToWire(const Status& msg, uint8_t (&wire)[Status::kMaxBytes])", header, StringComparison.Ordinal);
        Assert.Contains("ConvertToHost(const uint8_t (&wire)[Status::kMaxBytes], Status& msg)", header, StringComparison.Ordinal);
    }

    [Fact]
    public void A_variable_size_message_gets_no_wire_to_host_array_overload()
    {
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "bulk.h").Contents;

        Assert.Contains("ConvertToWire(const Batch& msg, uint8_t (&wire)[Batch::kMaxBytes])", header, StringComparison.Ordinal);
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
        var header = Generator.Generate(ir, new GeneratorOptions()).Files
            .Single(f => f.RelativePath == "control.h").Contents;

        Assert.Contains("enum class Mode : uint32_t {", header, StringComparison.Ordinal);
        Assert.Contains("Idle = 0,", header, StringComparison.Ordinal);
        Assert.Contains("Fault = 10,", header, StringComparison.Ordinal);
        // Emitted exactly once even though several fields could reference it.
        var occurrences = header.Split("enum class Mode").Length - 1;
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

    private static void AssertMatchesGolden(string path, string actual)
    {
        actual = actual.Replace("\r\n", "\n");

        if (ShouldUpdate || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            Assert.Fail($"Golden file written to {path}. Review the diff and re-run without PROTODESIGNER_UPDATE_GOLDEN.");
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(expected, actual);
    }
}
