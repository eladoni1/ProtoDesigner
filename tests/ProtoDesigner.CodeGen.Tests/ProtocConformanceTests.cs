using System.Diagnostics;
using System.Text.RegularExpressions;
using ProtoDesigner.CodeGen.Proto;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Compiles the generated <c>.proto</c> with the real protobuf compiler.
/// </summary>
/// <remarks>
/// <para>
/// This is to the protobuf target what the MSVC cross-check is to the C one: proof the output is a real
/// schema rather than text that looks like one. protoc validates syntax, package and import resolution,
/// message and enum nesting, field numbering, <c>repeated</c> shape, and proto3's rule that an enum must
/// have a zero value first.
/// </para>
/// <para>
/// It also checks the <c>buf.validate</c> constraints, which is the part worth having. protoc resolves
/// an option only when it can resolve the extension that declares it, so this compiles against the real
/// <c>validate.proto</c> from Buf rather than a stub — a stub would only check the generator against a
/// guess at protovalidate's shape, and would pass for exactly the reason it was wrong. Every option path
/// the generator emits (<c>uint32.gte</c>, <c>repeated.max_items</c>, <c>repeated.items…</c>,
/// <c>enum.defined_only</c>) is therefore verified against the real extension definitions.
/// </para>
/// </remarks>
public class ProtocConformanceTests
{
    private static readonly ProtoGenerator Generator = new();

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Corpus.All()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void The_generated_schema_compiles(string corpusName)
    {
        var set = Generator.Generate(BuildIr(corpusName), Options(protovalidate: false));

        var toolchain = RequireToolchain();
        if (toolchain is null) return;   // explicitly opted out inside RequireToolchain

        var (exitCode, output) = toolchain.Compile(set);
        Assert.True(exitCode == 0, $"protoc rejected the generated schema:\n{output}");
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void The_protovalidate_constraints_compile_against_the_real_extensions(string corpusName)
    {
        // The one that matters. Every option path the generator writes is checked against Buf's actual
        // validate.proto, so a misspelled rule or a wrongly nested one fails here rather than at whatever
        // consumer eventually tries to build the schema.
        var set = Generator.Generate(BuildIr(corpusName), Options(protovalidate: true));

        var toolchain = RequireToolchain();
        if (toolchain is null) return;

        Assert.True(toolchain.HasValidateProto,
            "validate.proto was not found, so the buf.validate constraints were never checked — the "
            + "reason this target is worth having went untested. Put Buf's validate.proto next to the "
            + $"compiler, or set {ProtocLocator.SkipVariable}=1 to accept coverage without it.");

        var (exitCode, output) = toolchain.Compile(set);
        Assert.True(exitCode == 0, $"protoc rejected the generated constraints:\n{output}");
    }

    private static ProtocolIr BuildIr(string corpusName)
    {
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();   // one call — project and bus must come from the same instance
        return new IrBuilder().Build(project, bus);
    }

    /// <summary>
    /// The toolchain, or null when the run has explicitly opted out. Fails when protoc is simply absent:
    /// a green test that compiled nothing is worse than a red one.
    /// </summary>
    private static ProtocToolchain? RequireToolchain()
    {
        var toolchain = ProtocLocator.Find();

        Assert.True(toolchain is not null || ProtocLocator.SkipRequested,
            "protoc was not found, so the generated schema was never compiled — the half of this test "
            + "that proves anything did not run. Put the compiler in protobuf/bin/, set "
            + $"{ProtocLocator.PathVariable}, or set {ProtocLocator.SkipVariable}=1 to accept "
            + "generation-only coverage.");

        return toolchain;
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void Constraints_are_purely_additive(string corpusName)
    {
        // Turning protovalidate off must remove constraints and change nothing else. Without this, the
        // flag could quietly alter a field's type or numbering and both compile runs would still pass,
        // leaving two schemas that disagree about the message rather than about its limits.
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();
        var ir = new IrBuilder().Build(project, bus);

        var plain = Generator.Generate(ir, Options(protovalidate: false));
        var constrained = Generator.Generate(ir, Options(protovalidate: true));

        Assert.Equal(plain.Files.Count, constrained.Files.Count);

        foreach (var file in plain.Files.Where(f => f.RelativePath.EndsWith(".proto", StringComparison.Ordinal)))
        {
            var other = constrained.Find(file.RelativePath);
            Assert.NotNull(other);
            Assert.Equal(Skeleton(file.Contents), Skeleton(other!.Contents), StringComparer.Ordinal);
        }
    }

    [Fact]
    public void A_schema_that_protoc_accepts_still_carries_our_field_numbers()
    {
        // protoc renumbering nothing is the point: the numbers in the descriptor must be the ones the
        // model persisted, or a regeneration could silently move a field an old peer depends on.
        var (project, bus) = Corpus.Scalars();
        var fields = bus.Messages[0].Fields;
        fields[0].ProtoFieldNumber = 11;
        fields[1].ProtoFieldNumber = 22;

        var ir = new IrBuilder().Build(project, bus);
        var set = Generator.Generate(ir, Options(protovalidate: false));

        var toolchain = ProtocLocator.Find();
        if (toolchain is null) return;   // covered by the assertion in the theories above

        var (exitCode, output) = toolchain.Compile(set);
        Assert.True(exitCode == 0, output);

        var source = set.Files.Single(f => f.RelativePath == "main.proto").Contents;
        Assert.Contains(" = 11;", source, StringComparison.Ordinal);
        Assert.Contains(" = 22;", source, StringComparison.Ordinal);
    }

    private static GeneratorOptions Options(bool protovalidate) =>
        new GeneratorOptions(Namespace: "proto")
            .With(ProtoGenerator.ProtovalidateOption, protovalidate ? "true" : "false");

    /// <summary>
    /// A schema with every option block and the protovalidate import removed, and blank lines collapsed —
    /// what is left is the structure protoc validated.
    /// </summary>
    private static string Skeleton(string source)
    {
        // `field = 1 [ ...options... ];` becomes `field = 1;`. The options never nest brackets, so a
        // non-greedy match to the closing one is sufficient and avoids a recursive parse.
        var stripped = Regex.Replace(source, @"\s*\[[^\]]*\]\s*;", ";", RegexOptions.Singleline);

        stripped = stripped.Replace("import \"buf/validate/validate.proto\";\n", "", StringComparison.Ordinal);
        stripped = stripped.Replace("import \"buf/validate/validate.proto\";\r\n", "", StringComparison.Ordinal);

        return string.Join('\n', stripped
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0));
    }
}


/// <summary>Finds a protobuf compiler and the schemas its generated output imports.</summary>
internal static class ProtocLocator
{
    /// <summary>Set to 1 to accept generation-only coverage on a machine with no protoc.</summary>
    /// <remarks>
    /// Opting out has to be deliberate, for the same reason as the C cross-check: a green test that never
    /// compiled anything proves only that the generator produced text.
    /// </remarks>
    public const string SkipVariable = "PROTODESIGNER_SKIP_PROTOC";

    /// <summary>Overrides the search with an explicit path to protoc.</summary>
    public const string PathVariable = "PROTODESIGNER_PROTOC";

    public static bool SkipRequested =>
        Environment.GetEnvironmentVariable(SkipVariable) is "1" or "true";

    /// <summary>
    /// The toolchain, or null when no compiler is available.
    /// </summary>
    /// <remarks>
    /// Looks for a <c>protobuf/</c> directory above the test binary holding <c>bin/protoc.exe</c>, the
    /// well-known types under <c>include/</c>, and Buf's <c>validate.proto</c>. It is not vendored — a
    /// 12 MB platform binary would live in git history forever — so the layout is a convention the
    /// failure message spells out rather than something the repo guarantees.
    /// </remarks>
    public static ProtocToolchain? Find()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? new ProtocToolchain(explicitPath, null, null) : null;

        var exe = OperatingSystem.IsWindows() ? "protoc.exe" : "protoc";
        var dir = AppContext.BaseDirectory;

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var root = Path.Combine(dir, "protobuf");
            if (Directory.Exists(root))
            {
                // bin/protoc.exe is the current layout; a bare protoc beside the includes also works.
                var compiler = new[] { Path.Combine(root, "bin", exe), Path.Combine(root, exe) }
                    .FirstOrDefault(File.Exists);

                if (compiler is not null)
                {
                    var includes = Path.Combine(root, "include");
                    var validate = new[]
                        {
                            Path.Combine(root, "validate.proto"),
                            Path.Combine(root, "buf", "validate", "validate.proto"),
                            Path.Combine(includes, "buf", "validate", "validate.proto"),
                        }
                        .FirstOrDefault(File.Exists);

                    return new ProtocToolchain(compiler, Directory.Exists(includes) ? includes : null, validate);
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}

/// <summary>A located protobuf compiler, plus whatever imports it can resolve.</summary>
/// <param name="Compiler">Path to protoc.</param>
/// <param name="IncludePath">Directory holding the well-known types, or null.</param>
/// <param name="ValidateProto">Buf's validate.proto, or null when the constraints cannot be checked.</param>
internal sealed record ProtocToolchain(string Compiler, string? IncludePath, string? ValidateProto)
{
    public bool HasValidateProto => ValidateProto is not null;

    /// <summary>
    /// Writes the set to a temp directory and compiles every .proto in it, returning protoc's exit code
    /// and output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>validate.proto</c> is staged at <c>buf/validate/validate.proto</c> beneath the working
    /// directory, because that is the path the generated schema imports and protoc resolves imports by
    /// path rather than by package. Copying it per run keeps the check independent of how the toolchain
    /// happens to be laid out on disk.
    /// </para>
    /// <para>
    /// <c>--descriptor_set_out</c> rather than a language plugin: it exercises the full parse, import
    /// resolution and option type-checking without needing a code generator backend installed, and it is
    /// what fails loudly on a duplicate field number or a misspelled constraint.
    /// </para>
    /// </remarks>
    public (int ExitCode, string Output) Compile(GeneratedFileSet set)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pd-protoc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try { return Run(set, dir, Path.Combine(dir, "out.desc"), includeImports: false); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Compiles into a directory the caller owns and leaves the descriptor set behind for something
    /// else to read.
    /// </summary>
    /// <remarks>
    /// <c>--include_imports</c> is the difference that matters: a descriptor set is only self-describing
    /// when it carries its dependencies, and a runtime loading it has no proto_path to go looking with.
    /// Without it the link step fails on the first import rather than at the field being checked, which
    /// looks like a schema bug and is not one.
    /// </remarks>
    public (int ExitCode, string Output) CompileDescriptorSet(GeneratedFileSet set, string dir, string descriptorSetOut) =>
        Run(set, dir, descriptorSetOut, includeImports: true);

    private (int ExitCode, string Output) Run(
        GeneratedFileSet set, string dir, string descriptorSetOut, bool includeImports)
    {
        Directory.CreateDirectory(dir);

        {
            var sources = new List<string>();
            foreach (var file in set.Files.Where(f => f.RelativePath.EndsWith(".proto", StringComparison.Ordinal)))
            {
                var full = Path.Combine(dir, file.RelativePath);
                File.WriteAllText(full, file.Contents);
                sources.Add(full);
            }

            if (sources.Count == 0) return (0, "no .proto files to compile");

            if (ValidateProto is not null)
            {
                var staged = Path.Combine(dir, "buf", "validate");
                Directory.CreateDirectory(staged);
                File.Copy(ValidateProto, Path.Combine(staged, "validate.proto"), overwrite: true);
            }

            var psi = new ProcessStartInfo(Compiler)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };
            psi.ArgumentList.Add($"--proto_path={dir}");
            if (IncludePath is not null) psi.ArgumentList.Add($"--proto_path={IncludePath}");
            if (includeImports) psi.ArgumentList.Add("--include_imports");
            psi.ArgumentList.Add($"--descriptor_set_out={descriptorSetOut}");
            foreach (var source in sources) psi.ArgumentList.Add(source);

            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(milliseconds: 120_000);

            return (proc.ExitCode, stdout.Result + stderr.Result);
        }
    }
}
