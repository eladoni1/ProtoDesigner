using ProtoDesigner.Persistence.Json;

namespace ProtoDesigner.Cli.Tests;

/// <summary>
/// Smoke tests over the CLI surface. Exit codes are the CI contract, so they are asserted explicitly;
/// so is the presence of the diagnostic code in the output, because that is what users grep for.
/// </summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly string _dir;

    public CommandLineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"pd-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CommandLine.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string WriteProject(Project project, string name = "test.pdproj")
    {
        var path = Path.Combine(_dir, name);
        new JsonProjectRepository().Save(project, path);
        return path;
    }

    private static Project CleanProject()
    {
        var p = new Project("Clean");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Ping") { WireId = 1 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "id", u8.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return p;
    }

    /// <summary>A project whose field asks for fewer bits than its declared range needs (PD0020).</summary>
    private static Project BrokenProject()
    {
        var p = new Project("Broken");
        var wide = p.Types.Add(new ParameterType(TypeId.New(), "Wide", PrimitiveKind.U16, new NumericRange(0, 1000)));
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Bad");
        m.Fields.Add(new FieldBinding(FieldId.New(), "tooSmall", wide.Id, FieldEncoding.Packed(4)));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return p;
    }

    // ---- usage --------------------------------------------------------------------------------

    [Fact]
    public void No_arguments_prints_usage_and_returns_the_usage_code()
    {
        var (code, output, _) = Run();
        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_returns_success()
    {
        var (code, output, _) = Run("--help");
        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains("protodesigner", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_command_is_a_usage_error()
    {
        var (code, _, err) = Run("frobnicate");
        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("Unknown command", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Targets_lists_the_cpp_generator()
    {
        var (code, output, _) = Run("targets");
        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains("cpp", output, StringComparison.Ordinal);
    }

    // ---- validate -----------------------------------------------------------------------------

    [Fact]
    public void Validating_a_clean_project_returns_zero()
    {
        var path = WriteProject(CleanProject());
        var (code, output, _) = Run("validate", path);
        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains("no issues", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Validating_a_broken_project_returns_one_and_prints_the_diagnostic_code()
    {
        var path = WriteProject(BrokenProject());
        var (code, _, err) = Run("validate", path);
        Assert.Equal(CommandLine.ExitValidationErrors, code);
        Assert.Contains("PD0020", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Validating_a_missing_file_is_an_io_error()
    {
        var (code, _, err) = Run("validate", Path.Combine(_dir, "nope.pdproj"));
        Assert.Equal(CommandLine.ExitIoError, code);
        Assert.Contains("not found", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quiet_suppresses_non_error_diagnostics()
    {
        // A clean project still emits Info diagnostics (unreferenced types etc.).
        var project = CleanProject();
        project.Types.Add(new ParameterType(TypeId.New(), "Unused", PrimitiveKind.U32));
        var path = WriteProject(project);

        var (_, verbose, _) = Run("validate", path);
        Assert.Contains("PD0060", verbose, StringComparison.Ordinal);

        var (_, quiet, _) = Run("validate", path, "--quiet");
        Assert.DoesNotContain("PD0060", quiet, StringComparison.Ordinal);
    }

    // ---- generate -----------------------------------------------------------------------------

    [Fact]
    public void Generating_from_a_clean_project_writes_the_expected_files()
    {
        var path = WriteProject(CleanProject());
        var outDir = Path.Combine(_dir, "out");

        var (code, output, _) = Run("generate", path, "--target", "cpp", "--out", outDir);

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.True(File.Exists(Path.Combine(outDir, "protodesigner_runtime.h")));
        Assert.True(File.Exists(Path.Combine(outDir, "main.h")));
        Assert.Contains("Generated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Generation_is_refused_when_the_project_has_validation_errors()
    {
        var path = WriteProject(BrokenProject());
        var outDir = Path.Combine(_dir, "refused");

        var (code, _, err) = Run("generate", path, "--target", "cpp", "--out", outDir);

        Assert.Equal(CommandLine.ExitValidationErrors, code);
        Assert.Contains("Refusing to generate", err, StringComparison.Ordinal);
        Assert.Contains("PD0020", err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outDir) && Directory.GetFiles(outDir).Length > 0,
            "No files should be written when generation is refused.");
    }

    [Fact]
    public void Generate_requires_an_output_directory()
    {
        var path = WriteProject(CleanProject());
        var (code, _, err) = Run("generate", path, "--target", "cpp");
        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("--out", err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_target_is_a_usage_error()
    {
        var path = WriteProject(CleanProject());
        var (code, _, err) = Run("generate", path, "--target", "fortran", "--out", Path.Combine(_dir, "x"));
        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("Unknown target", err, StringComparison.Ordinal);
    }

    [Fact]
    public void The_namespace_option_reaches_the_generated_code()
    {
        var path = WriteProject(CleanProject());
        var outDir = Path.Combine(_dir, "ns");

        Run("generate", path, "--target", "cpp", "--out", outDir, "--namespace", "acme");

        var header = File.ReadAllText(Path.Combine(outDir, "main.h"));
        Assert.Contains("namespace acme {", header, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_bus_can_be_generated_on_its_own()
    {
        var project = CleanProject();
        var second = new Bus(BusId.New(), "Secondary", Transport.Uart);
        var m = new Message(MessageId.New(), "Beep");
        m.Fields.Add(new FieldBinding(FieldId.New(), "x", project.Types.All.First().Id));
        second.Messages.Add(m);
        project.Buses.Add(second);

        var path = WriteProject(project);
        var outDir = Path.Combine(_dir, "onebus");

        var (code, _, _) = Run("generate", path, "--out", outDir, "--bus", "Secondary");

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.True(File.Exists(Path.Combine(outDir, "secondary.h")));
        Assert.False(File.Exists(Path.Combine(outDir, "main.h")));
    }

    [Fact]
    public void Generating_a_bus_that_does_not_exist_is_a_usage_error()
    {
        var path = WriteProject(CleanProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "x"), "--bus", "Ghost");
        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("Ghost", err, StringComparison.Ordinal);
    }
}
