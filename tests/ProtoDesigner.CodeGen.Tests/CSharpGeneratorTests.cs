using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ProtoDesigner.CodeGen.CSharp;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Exercises the C# target. The headline test compiles the generated source with Roslyn — the same
/// standard the C++ target is held to by tests/cpp-conformance, and the only one that catches an
/// emitter that produces plausible-looking text which does not actually build.
/// </summary>
/// <remarks>
/// This target emits declarations only; encode/decode is Phase 5. The tests therefore assert the shape
/// of the structure and that it compiles — not round-tripped bytes, which there is no C# codec to
/// produce yet. <see cref="The_output_says_plainly_that_conversion_code_is_missing"/> is what stops the
/// gap being quietly forgotten.
/// </remarks>
public class CSharpGeneratorTests
{
    private static readonly CSharpGenerator Generator = new();

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Corpus.All()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void The_generated_csharp_compiles(string corpusName)
    {
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();   // one call — project and bus must come from the same instance
        var ir = new IrBuilder().Build(project, bus);

        var set = Generator.Generate(ir, new GeneratorOptions(Namespace: "Proto"));

        AssertCompiles(set);
    }

    [Fact]
    public void Two_buses_sharing_a_struct_declare_it_once()
    {
        // Declaring it per bus would be two types of the same name in one namespace — code that cannot
        // compile at all, which is why the shared declarations file exists.
        var (project, first) = Corpus.TwoBusesSharingAStruct();
        var builder = new IrBuilder();
        var irs = project.Buses.Select(b => builder.Build(project, b)).ToList();

        Assert.True(irs.Count > 1, "the corpus entry is supposed to have more than one bus");

        var set = Generator.Generate(irs, new GeneratorOptions(Namespace: "Proto"));

        AssertCompiles(set);
        Assert.Equal(irs.Count + 2, set.Files.Count);   // one per bus, plus shared types and the README
    }

    [Fact]
    public void Every_message_becomes_a_class_carrying_its_size()
    {
        var (project, bus) = Corpus.DynamicArray();
        var ir = new IrBuilder().Build(project, bus);
        var message = ir.Messages[0];

        var source = BusFile(Generator.Generate(ir, new GeneratorOptions(Namespace: "Proto")), ir);

        Assert.Contains($"public sealed class {message.Name}", source, StringComparison.Ordinal);
        Assert.Contains($"public const int MinBits = {message.MinBits};", source, StringComparison.Ordinal);
        Assert.Contains($"public const int MaxBits = {message.MaxBits};", source, StringComparison.Ordinal);
        Assert.Contains($"public const int MaxBytes = {(message.MaxBits + 7) / 8};", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wire_layout_of_every_field_appears_as_a_comment()
    {
        // The layout comment is the whole point of a structure-only target: without conversion code it is
        // the only place the reader learns where a field actually sits.
        var (project, bus) = Corpus.PackedBits();
        var ir = new IrBuilder().Build(project, bus);

        var source = BusFile(Generator.Generate(ir, new GeneratorOptions(Namespace: "Proto")), ir);

        foreach (var field in ir.Messages.SelectMany(m => m.Fields))
            Assert.Contains(field.Path, source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dynamic_array_gets_its_capacity_and_the_count_stays_single_sourced()
    {
        var (project, bus) = Corpus.DynamicArray();
        var ir = new IrBuilder().Build(project, bus);
        var source = BusFile(Generator.Generate(ir, new GeneratorOptions(Namespace: "Proto")), ir);

        var array = ir.Messages.SelectMany(m => m.Members).First(m => m.ArrayCapacity is not null);
        Assert.Contains($"[{array.ArrayCapacity}]", source, StringComparison.Ordinal);

        // A count-from-field array must NOT get a second count member: two copies of the same number can
        // disagree, and the field the user declared is the one the wire format reads.
        if (!array.NeedsCountMember)
            Assert.DoesNotContain($"{CapitalizeFirst(array.Name)}Count;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_says_plainly_that_conversion_code_is_missing()
    {
        // Shipping declarations that look complete is the failure mode worth guarding: someone builds
        // against them, finds no encoder, and assumes the tool is broken rather than unfinished.
        var ir = Corpus.BuildIr(Corpus.Scalars());
        var set = Generator.Generate(ir, new GeneratorOptions(Namespace: "Proto"));

        foreach (var file in set.Files.Where(f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal)))
            Assert.Contains("Encode/decode is not emitted for C# yet", file.Contents, StringComparison.Ordinal);

        Assert.Contains("no encode/decode", Generator.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_catalog_offers_both_targets()
    {
        Assert.Equal(new[] { "cpp", "csharp" }, GeneratorCatalog.All.Select(g => g.Id));
        Assert.NotNull(GeneratorCatalog.Find("CSHARP"));   // ids match case-insensitively
        Assert.Null(GeneratorCatalog.Find("rust"));
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string BusFile(GeneratedFileSet set, ProtocolIr ir) =>
        set.Files.Single(f => f.RelativePath.Equals($"{ir.BusName}.cs", StringComparison.Ordinal)).Contents;

    private static string CapitalizeFirst(string raw) =>
        raw.Length == 0 ? raw : char.ToUpperInvariant(raw[0]) + raw[1..];

    /// <summary>
    /// Compiles every .cs file in the set as one assembly and fails with the compiler's own diagnostics.
    /// </summary>
    private static void AssertCompiles(GeneratedFileSet set)
    {
        var sources = set.Files
            .Where(f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Contents, path: f.RelativePath))
            .ToList();

        Assert.NotEmpty(sources);

        var compilation = CSharpCompilation.Create(
            "GeneratedProtocol",
            sources,
            ReferenceAssemblies(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            "generated C# did not compile:\n" + string.Join("\n", errors.Select(e => $"  {e}")));
    }

    /// <summary>
    /// The framework assemblies this test process is running against. Taking them from the runtime rather
    /// than naming files keeps the test working on whatever SDK the machine has.
    /// </summary>
    private static IEnumerable<MetadataReference> ReferenceAssemblies() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
}
