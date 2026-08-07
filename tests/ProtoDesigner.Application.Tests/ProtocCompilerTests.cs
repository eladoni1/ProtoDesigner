namespace ProtoDesigner.Application.Tests;

/// <summary>
/// Compiling an emitted schema into real source with the real protobuf compiler.
/// </summary>
/// <remarks>
/// This is the step that turns a <c>.proto</c> into something a consumer can actually link against.
/// It runs protoc for real; there is no way to check "did we produce usable C++" without doing so, and a
/// mocked compiler would only assert that we can build a command line.
/// </remarks>
public class ProtocCompilerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pd-protoc-out-{Guid.NewGuid():N}");

    public ProtocCompilerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A minimal schema on disk, as the proto target would have written it.</summary>
    private string WriteSchema(bool withConstraints = false)
    {
        var constraint = withConstraints
            ? " [(buf.validate.field).uint32.lte = 100]"
            : "";
        var import = withConstraints ? "import \"buf/validate/validate.proto\";\n" : "";

        var path = Path.Combine(_dir, "main.proto");
        File.WriteAllText(path,
            "syntax = \"proto3\";\n"
            + "package demo;\n"
            + import
            + "message Reading {\n"
            + $"  uint32 ratio = 1{constraint};\n"
            + "}\n");
        return path;
    }

    // ---- what the caller asked for ----------------------------------------------------------------

    [Fact]
    public void Asking_for_nothing_runs_nothing()
    {
        var result = ProtocCompiler.Run(_dir, []);

        Assert.False(result.Ran);
        Assert.Null(result.Error);
    }

    [Fact]
    public void An_unknown_language_is_refused_by_name()
    {
        // Before locating protoc, so the message is about the typo rather than about a missing toolchain.
        var result = ProtocCompiler.Run(_dir, ["rust"]);

        Assert.False(result.Succeeded);
        Assert.Contains("rust", result.Error!, StringComparison.Ordinal);
        Assert.Contains("cpp", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_no_C_language_because_protobuf_has_no_C_backend()
    {
        // Worth pinning as a fact rather than a comment: "C/C++" is a habit of speech, and protobuf's
        // --cpp_out is C++ only. A C consumer wants this project's own `c` target, or nanopb.
        Assert.Null(ProtocCompiler.FindLanguage("c"));
        Assert.NotNull(ProtocCompiler.FindLanguage("cpp"));
    }

    [Fact]
    public void Compiling_a_directory_with_no_schema_says_so()
    {
        if (ProtocCompiler.Locate() is null) return;   // covered by RequiresProtoc below

        var result = ProtocCompiler.Run(_dir, ["cpp"]);

        Assert.False(result.Succeeded);
        Assert.Contains("nothing for protoc to compile", result.Error!, StringComparison.Ordinal);
    }

    // ---- the real thing ---------------------------------------------------------------------------

    /// <summary>
    /// Fails when protoc is absent rather than passing quietly, matching every other toolchain check
    /// here — a green test that compiled nothing is worse than a red one.
    /// </summary>
    private static string RequireProtoc()
    {
        var protoc = ProtocCompiler.Locate();
        Assert.True(protoc is not null,
            "protoc was not found, so nothing was compiled from the schema and this test proved only "
            + $"that a command line can be built. Put it in protobuf/bin/, set "
            + $"{ProtocCompiler.PathVariable}, or put it on PATH.");
        return protoc!;
    }

    [Fact]
    public void A_schema_compiles_to_real_cpp()
    {
        RequireProtoc();
        WriteSchema();

        var result = ProtocCompiler.Run(_dir, ["cpp"]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("main.pb.h", result.Produced);
        Assert.Contains("main.pb.cc", result.Produced);

        // Not just "a file appeared" — the accessor for the field we declared has to be in it, or protoc
        // compiled something other than our schema.
        var header = File.ReadAllText(Path.Combine(_dir, "main.pb.h"));
        Assert.Contains("class Reading", header, StringComparison.Ordinal);
        Assert.Contains("ratio", header, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_compiles_to_real_csharp()
    {
        RequireProtoc();
        WriteSchema();

        var result = ProtocCompiler.Run(_dir, ["csharp"]);

        Assert.True(result.Succeeded, result.Error);
        var generated = Assert.Single(result.Produced, f => f.EndsWith(".cs", StringComparison.Ordinal));
        Assert.Contains("class Reading", File.ReadAllText(Path.Combine(_dir, generated)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Several_languages_come_out_of_one_run()
    {
        RequireProtoc();
        WriteSchema();

        var result = ProtocCompiler.Run(_dir, ["cpp", "csharp"]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Produced, f => f.EndsWith(".pb.h", StringComparison.Ordinal));
        Assert.Contains(result.Produced, f => f.EndsWith(".cs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_constrained_schema_brings_its_validate_dependency_with_it()
    {
        // The generated main.pb.h carries `#include "buf/validate/validate.pb.h"`, so without compiling
        // the import too the output does not build. Staging it is not a convenience — it is the
        // difference between usable source and a dangling include.
        RequireProtoc();
        WriteSchema(withConstraints: true);

        var result = ProtocCompiler.Run(_dir, ["cpp"]);

        if (!result.Succeeded && result.Error!.Contains("buf/validate", StringComparison.Ordinal))
        {
            Assert.Fail("Buf's validate.proto was not found beside the compiler, so a constrained schema "
                        + "could not be compiled: " + result.Error);
        }

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.CompiledValidate, "the import was not reported as compiled");
        Assert.Contains("buf/validate/validate.pb.h", result.Produced);

        var header = File.ReadAllText(Path.Combine(_dir, "main.pb.h"));
        Assert.Contains("buf/validate/validate.pb.h", header, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unconstrained_schema_pulls_in_nothing_extra()
    {
        // The other half: no constraints, no import, no megabyte of validate code the user never asked
        // for. This is what `--option protovalidate=false` buys.
        RequireProtoc();
        WriteSchema(withConstraints: false);

        var result = ProtocCompiler.Run(_dir, ["cpp"]);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.CompiledValidate);
        Assert.DoesNotContain(result.Produced, f => f.Contains("validate", StringComparison.Ordinal));
    }

    [Fact]
    public void A_broken_schema_reports_protocs_own_complaint()
    {
        RequireProtoc();
        File.WriteAllText(Path.Combine(_dir, "main.proto"), "syntax = \"proto3\";\nmessage {{{\n");

        var result = ProtocCompiler.Run(_dir, ["cpp"]);

        Assert.True(result.Ran);
        Assert.False(result.Succeeded);
        Assert.Contains("protoc failed", result.Error!, StringComparison.Ordinal);
    }
}
