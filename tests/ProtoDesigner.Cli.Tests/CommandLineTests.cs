using ProtoDesigner.Core.Validation;
using ProtoDesigner.Application;
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
        var u32 = p.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Ping") { WireId = 1 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "id", u32.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return p;
    }

    /// <summary>
    /// A project with a declared range, so the protobuf target has an actual constraint to emit.
    /// </summary>
    /// <remarks>
    /// <see cref="CleanProject"/> declares no range anywhere, so its schema carries no <c>buf.validate</c>
    /// options whether the flag is on or off — asserting on their absence there would pass for the wrong
    /// reason and prove nothing about the flag.
    /// </remarks>
    private static Project ConstrainedProject()
    {
        var p = new Project("Constrained");
        var ratio = p.Types.Add(new ParameterType(TypeId.New(), "Ratio", PrimitiveKind.U32,
            new NumericRange(0, 100)));
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Reading") { WireId = 1 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "ratio", ratio.Id));
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




    // ---- build policy -------------------------------------------------------------------------

    /// <summary>An oversized message on a UART bus: 400 bytes against a 256-byte budget.</summary>
    private static Project OversizedProject()
    {
        var p = new Project("Big");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var block = p.Types.Add(new ArrayType(TypeId.New(), "Block", u8.Id, new ArrayLength.Fixed(400)));
        var bus = new Bus(BusId.New(), "Link", Transport.Uart);
        var m = new Message(MessageId.New(), "Bulk") { WireId = 1 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "block", block.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return p;
    }

    [Fact]
    public void An_oversized_message_generates_by_default_and_only_warns()
    {
        var path = WriteProject(OversizedProject());
        var outDir = Path.Combine(_dir, "out");

        var (code, _, _) = Run("generate", path, "--out", outDir);

        Assert.Equal(CommandLine.ExitOk, code);

        // The per-bus header is named after the bus, so this one is link.h rather than main.h.
        Assert.True(File.Exists(Path.Combine(outDir, "link.h")));
    }

    [Fact]
    public void The_frame_budget_becomes_a_refusal_when_asked()
    {
        var path = WriteProject(OversizedProject());
        var outDir = Path.Combine(_dir, "out");

        var (code, _, err) = Run("generate", path, "--out", outDir, "--frame-budget-is-an-error");

        Assert.Equal(CommandLine.ExitValidationErrors, code);
        Assert.Contains(DiagnosticCodes.MtuExceeded, err, StringComparison.Ordinal);

        // Nothing written: a refusal that left half a header behind would be worse than no refusal.
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void Assign_ids_fills_the_gaps_and_names_only_what_it_changed()
    {
        var project = CleanProject();
        var bus = project.Buses[0];
        bus.Messages[0].WireId = 5;
        var second = new Message(MessageId.New(), "Second");   // no id at all
        second.Fields.Add(new FieldBinding(FieldId.New(), "x", project.Types.All.First().Id));
        bus.Messages.Add(second);

        var path = WriteProject(project);

        var (code, output, _) = Run("generate", path, "--out", Path.Combine(_dir, "out"), "--assign-ids");

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains("assigned Second id 1", output, StringComparison.Ordinal);

        // The message that already had an id is not listed — it was not reassigned, and saying so would
        // read as though it had been.
        Assert.DoesNotContain("assigned Ping", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Assign_ids_says_nothing_when_every_message_already_has_one()
    {
        var path = WriteProject(CleanProject());

        var (code, output, _) = Run("generate", path, "--out", Path.Combine(_dir, "out"), "--assign-ids");

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.DoesNotContain("assigned", output, StringComparison.Ordinal);
    }

    // ---- argument handling --------------------------------------------------------------------


    /// <summary>
    /// Putting the flags first is a common habit, and it used to report "File not found: --out" because
    /// the path was whatever landed in position zero.
    /// </summary>
    [Fact]
    public void The_file_may_be_named_after_the_flags()
    {
        var path = WriteProject(CleanProject());
        var outDir = Path.Combine(_dir, "out");

        var (code, _, err) = Run("generate", "--out", outDir, path);

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.DoesNotContain("File not found", err, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outDir, "main.h")));
    }

    /// <summary>
    /// A forgotten value used to swallow the next flag: `--namespace --out dir` generated successfully
    /// into `dir` under the namespace "out", which is a wrong answer reported as a right one.
    /// </summary>
    [Fact]
    public void An_option_will_not_swallow_the_next_flag_as_its_value()
    {
        var path = WriteProject(CleanProject());
        var outDir = Path.Combine(_dir, "out");

        var (code, _, err) = Run("generate", path, "--namespace", "--out", outDir);

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("--namespace", err, StringComparison.Ordinal);
        Assert.False(Directory.Exists(outDir));
    }

    /// <summary>A misspelled flag that changes the output must not look like the flag working.</summary>
    [Fact]
    public void An_unknown_option_is_reported_rather_than_ignored()
    {
        var path = WriteProject(CleanProject());

        var (code, _, err) = Run("generate", path, "--outt", Path.Combine(_dir, "out"));

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("--outt", err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_missing_its_value_at_the_end_of_the_line_is_a_usage_error()
    {
        var path = WriteProject(CleanProject());

        var (code, _, err) = Run("generate", path, "--out");

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("--out", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeating_an_option_uses_the_last_value()
    {
        var path = WriteProject(CleanProject());
        var wrong = Path.Combine(_dir, "wrong");
        var right = Path.Combine(_dir, "right");

        var (code, _, _) = Run("generate", path, "--out", wrong, "--out", right);

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.True(File.Exists(Path.Combine(right, "main.h")));
        Assert.False(Directory.Exists(wrong));
    }

    // ---- compare ------------------------------------------------------------------------------


    /// <summary>
    /// The two projects here are the same object saved twice, so every id matches — the situation two
    /// checkouts of one file are in, and the only one where the comparison means anything.
    /// </summary>
    [Fact]
    public void Comparing_a_project_against_itself_reports_no_differences()
    {
        var path = WriteProject(CleanProject());

        var (code, output, _) = Run("compare", path, path);

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains("No wire-visible differences", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparing_reports_a_narrowed_field_as_breaking()
    {
        var baseline = CleanProject();
        var basePath = WriteProject(baseline, "base.pdproj");

        baseline.Buses[0].Messages[0].Fields[0].Encoding.BitWidth = 16;
        var nextPath = WriteProject(baseline, "next.pdproj");

        var (code, output, _) = Run("compare", basePath, nextPath);

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains(DiagnosticCodes.FieldResized, output, StringComparison.Ordinal);
        Assert.Contains("BREAKING", output, StringComparison.Ordinal);
    }

    /// <summary>Renaming is the one change this tool can promise is free, so the CLI must say so.</summary>
    [Fact]
    public void Comparing_reports_a_rename_as_safe()
    {
        var baseline = CleanProject();
        var basePath = WriteProject(baseline, "base.pdproj");

        baseline.Buses[0].Messages[0].Fields[0].Name = "identifier";
        var nextPath = WriteProject(baseline, "next.pdproj");

        var (code, output, _) = Run("compare", basePath, nextPath);

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains(DiagnosticCodes.RenamedSafely, output, StringComparison.Ordinal);
        Assert.DoesNotContain("BREAKING", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Breaking the wire on purpose is what a version bump is, so the default exit code stays 0 and a CI
    /// job that wants otherwise has to say so.
    /// </summary>
    [Fact]
    public void Breaking_changes_only_fail_the_run_when_asked()
    {
        var baseline = CleanProject();
        var basePath = WriteProject(baseline, "base.pdproj");
        baseline.Buses[0].Messages[0].WireId = 99;
        var nextPath = WriteProject(baseline, "next.pdproj");

        Assert.Equal(CommandLine.ExitOk, Run("compare", basePath, nextPath).Code);
        Assert.Equal(CommandLine.ExitValidationErrors,
            Run("compare", basePath, nextPath, "--breaking-is-an-error").Code);
    }

    [Fact]
    public void Comparing_without_two_files_is_a_usage_error()
    {
        var path = WriteProject(CleanProject());

        Assert.Equal(CommandLine.ExitUsage, Run("compare").Code);
        Assert.Equal(CommandLine.ExitUsage, Run("compare", path).Code);
    }

    [Fact]
    public void Comparing_against_a_missing_baseline_is_an_io_error()
    {
        var path = WriteProject(CleanProject());

        Assert.Equal(CommandLine.ExitIoError, Run("compare", "nope.pdproj", path).Code);
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
    public void Targets_lists_the_c_generator()
    {
        var (code, output, _) = Run("targets");
        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains("c ", output, StringComparison.Ordinal);
        Assert.Contains("csharp", output, StringComparison.Ordinal);
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
        // A clean project still emits Info diagnostics. An unreferenced STRUCT is the trigger here:
        // unused primitives are deliberately not reported, since every project seeds the integer widths
        // and reporting each unused one would drown the real findings.
        var project = CleanProject();
        var payload = project.Types.All.OfType<ParameterType>().First();
        project.Types.Add(new StructType(TypeId.New(), "Unused")
            .With(new FieldBinding(FieldId.New(), "x", payload.Id)));
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

        var (code, output, _) = Run("generate", path, "--target", "c", "--out", outDir);

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

        var (code, _, err) = Run("generate", path, "--target", "c", "--out", outDir);

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
        var (code, _, err) = Run("generate", path, "--target", "c");
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

        Run("generate", path, "--target", "c", "--out", outDir, "--namespace", "acme");

        var header = File.ReadAllText(Path.Combine(outDir, "main.h"));
        // C has no namespaces, so the option becomes a symbol prefix instead.
        Assert.Contains("acme_", header, StringComparison.Ordinal);
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

    // ---- scoped generation ---------------------------------------------------------------------

    /// <summary>
    /// Two buses. Sensor sits on both, so a module-scoped build must cover both. Beta involves neither
    /// Sensor nor Logger, which is what proves the filter is doing something.
    /// </summary>
    private static Project RoutedProject()
    {
        var p = new Project("Routed");
        var u32 = p.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var main = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var sensor = main.AddModule("Sensor");
        var controller = main.AddModule("Controller");

        var alpha = new Message(MessageId.New(), "Alpha") { WireId = 1 };
        alpha.Fields.Add(new FieldBinding(FieldId.New(), "x", u32.Id));
        alpha.Routes.Add(new MessageRoute(sensor.Id, controller.Id));

        var beta = new Message(MessageId.New(), "Beta") { WireId = 2 };
        beta.Fields.Add(new FieldBinding(FieldId.New(), "y", u32.Id));
        beta.Routes.Add(new MessageRoute(controller.Id, controller.Id));

        main.Messages.Add(alpha);
        main.Messages.Add(beta);

        var aux = new Bus(BusId.New(), "Aux", Transport.Uart);
        var sensorOnAux = aux.AddModule("Sensor");
        var logger = aux.AddModule("Logger");

        var gamma = new Message(MessageId.New(), "Gamma") { WireId = 1 };
        gamma.Fields.Add(new FieldBinding(FieldId.New(), "z", u32.Id));
        gamma.Routes.Add(new MessageRoute(sensorOnAux.Id, logger.Id));
        aux.Messages.Add(gamma);

        p.Buses.Add(main);
        p.Buses.Add(aux);
        return p;
    }

    [Fact]
    public void A_subset_of_a_buses_messages_can_be_generated()
    {
        var path = WriteProject(RoutedProject());
        var outDir = Path.Combine(_dir, "subset");

        var (code, output, _) = Run("generate", path, "--out", outDir, "--bus", "Main", "--messages", "Alpha");

        Assert.Equal(CommandLine.ExitOk, code);
        var header = File.ReadAllText(Path.Combine(outDir, "main.h"));
        Assert.Contains("typedef struct proto_Alpha {", header, StringComparison.Ordinal);
        Assert.DoesNotContain("typedef struct proto_Beta {", header, StringComparison.Ordinal);
        Assert.Contains("1 message(s)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_generates_every_bus_it_sits_on()
    {
        var path = WriteProject(RoutedProject());
        var outDir = Path.Combine(_dir, "bymodule");

        var (code, output, _) = Run("generate", path, "--out", outDir, "--module", "Sensor");

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.Contains("2 bus(es)", output, StringComparison.Ordinal);

        // Alpha (Main) and Gamma (Aux) involve Sensor; Beta does not.
        Assert.Contains("typedef struct proto_Alpha {", File.ReadAllText(Path.Combine(outDir, "main.h")), StringComparison.Ordinal);
        Assert.DoesNotContain("typedef struct proto_Beta {", File.ReadAllText(Path.Combine(outDir, "main.h")), StringComparison.Ordinal);
        Assert.Contains("typedef struct proto_Gamma {", File.ReadAllText(Path.Combine(outDir, "aux.h")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_on_one_bus_only_generates_that_bus()
    {
        var path = WriteProject(RoutedProject());
        var outDir = Path.Combine(_dir, "logger");

        var (code, _, _) = Run("generate", path, "--out", outDir, "--module", "Logger");

        Assert.Equal(CommandLine.ExitOk, code);
        Assert.True(File.Exists(Path.Combine(outDir, "aux.h")));
        Assert.False(File.Exists(Path.Combine(outDir, "main.h")));
    }

    [Fact]
    public void An_unknown_module_lists_the_ones_that_do_exist()
    {
        var path = WriteProject(RoutedProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "x"), "--module", "Ghost");

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("Ghost", err, StringComparison.Ordinal);
        Assert.Contains("Sensor", err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_message_name_is_reported_rather_than_silently_skipped()
    {
        var path = WriteProject(RoutedProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "x"),
            "--bus", "Main", "--messages", "Alpha,Nope");

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("Nope", err, StringComparison.Ordinal);
    }

    // --module already answers both questions; combining leaves two answers and no winner.
    [Fact]
    public void Module_cannot_be_combined_with_bus_or_messages()
    {
        var path = WriteProject(RoutedProject());
        var outDir = Path.Combine(_dir, "x");

        var (busCode, _, _) = Run("generate", path, "--out", outDir, "--module", "Sensor", "--bus", "Main");
        Assert.Equal(CommandLine.ExitUsage, busCode);

        var (msgCode, _, _) = Run("generate", path, "--out", outDir, "--module", "Sensor", "--messages", "Alpha");
        Assert.Equal(CommandLine.ExitUsage, msgCode);
    }

    [Fact]
    public void Messages_without_a_bus_is_a_usage_error()
    {
        var path = WriteProject(RoutedProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "x"), "--messages", "Alpha");

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("--bus", err, StringComparison.Ordinal);
    }

    // ---- target options ----------------------------------------------------------------------------

    [Fact]
    public void A_target_option_reaches_the_generator()
    {
        // The whole point of the option bag: a per-target setting the CLI passes through without knowing
        // what it means. Turning protovalidate off must actually remove the constraints.
        var path = WriteProject(ConstrainedProject());
        var withDir = Path.Combine(_dir, "with-validate");
        var withoutDir = Path.Combine(_dir, "no-validate");

        Assert.Equal(0, Run("generate", path, "--out", withDir, "--target", "proto").Code);
        var (code, _, err) = Run("generate", path, "--out", withoutDir, "--target", "proto",
            "--option", "protovalidate=false");

        Assert.Equal(0, code);
        Assert.Equal("", err);

        // Both directions, so the assertion cannot pass because the constraint was never there.
        Assert.Contains("buf.validate", File.ReadAllText(Path.Combine(withDir, "main.proto")),
            StringComparison.Ordinal);
        Assert.DoesNotContain("buf.validate", File.ReadAllText(Path.Combine(withoutDir, "main.proto")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Target_options_are_repeatable_and_the_last_one_wins()
    {
        // Repeatable because a target may declare several; last-wins because a single flag given twice
        // has to resolve to something, and silently keeping the first would contradict every other CLI.
        var path = WriteProject(ConstrainedProject());
        var outDir = Path.Combine(_dir, "repeated");

        var (code, _, _) = Run("generate", path, "--out", outDir, "--target", "proto",
            "--option", "protovalidate=false", "--option", "unrelated=1", "--option", "protovalidate=true");

        Assert.Equal(0, code);
        Assert.Contains("buf.validate", File.ReadAllText(Path.Combine(outDir, "main.proto")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("protovalidate")]      // no '='
    [InlineData("=false")]             // no key
    [InlineData("protovalidate=")]     // no value
    public void A_malformed_target_option_is_a_usage_error(string pair)
    {
        // Rejected rather than ignored: a typo that silently does nothing would leave the user believing
        // a setting applied when the output says otherwise.
        var path = WriteProject(CleanProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "bad"),
            "--target", "proto", "--option", pair);

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("key=value", err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_option_flag_with_no_pair_is_a_usage_error()
    {
        var path = WriteProject(CleanProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "bad"),
            "--target", "proto", "--option");

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("--option", err, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_a_target_does_not_declare_is_ignored()
    {
        // A generator reads its own keys and ignores the rest, which is what lets one flag list serve
        // every target. Erroring here would make a shared script impossible to write.
        var path = WriteProject(CleanProject());
        var outDir = Path.Combine(_dir, "foreign");

        var (code, _, _) = Run("generate", path, "--out", outDir, "--target", "c",
            "--option", "protovalidate=false");

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(outDir, "main.h")));
    }

    [Fact]
    public void Targets_lists_the_options_each_generator_accepts()
    {
        // How a user discovers what --option takes without reading the source.
        var (code, output, _) = Run("targets");

        Assert.Equal(0, code);
        Assert.Contains("protovalidate", output, StringComparison.Ordinal);
    }

    // ---- the shipped samples -----------------------------------------------------------------------

    /// <summary>A file under <c>samples/</c>, found by walking up from the test binary.</summary>
    /// <remarks>
    /// The samples are documentation that runs. A sample which stopped loading, stopped validating, or
    /// quietly changed which messages it exports would otherwise be discovered by whoever opened it next.
    /// </remarks>
    private static string Sample(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "samples", name);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Could not find samples/{name} above the test binary.");
    }

    [Fact]
    public void The_protobuf_sample_validates_cleanly()
    {
        var (code, _, _) = Run("validate", Sample("protobuf-demo.pdproj"));

        Assert.Equal(0, code);
    }

    [Fact]
    public void The_protobuf_sample_exports_what_it_claims_and_withholds_the_rest()
    {
        // The sample exists to demonstrate the gate, so its two exportable and two refused messages are
        // the thing under test. Hand-checking this once proved it worked that day.
        var outDir = Path.Combine(_dir, "demo");
        var (code, output, _) = Run("generate", Sample("protobuf-demo.pdproj"),
            "--out", outDir, "--target", "proto", "--namespace", "demo");

        Assert.Equal(0, code);

        var schema = File.ReadAllText(Path.Combine(outDir, "main.proto"));
        Assert.Contains("message Status", schema, StringComparison.Ordinal);
        Assert.Contains("message Batch", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("message CompactStatus", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("message Power", schema, StringComparison.Ordinal);

        // Refusals are reported by name and by cause; a message dropped in silence is the failure the
        // whole gate exists to avoid.
        Assert.Contains("skipped CompactStatus", output, StringComparison.Ordinal);
        Assert.Contains("compactMode", output, StringComparison.Ordinal);
        Assert.Contains("skipped Power", output, StringComparison.Ordinal);
        Assert.Contains("level", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_protobuf_sample_keeps_an_offset_field_s_real_range()
    {
        // The design decision the target rests on: `pressure` is 1000..1015 with an offset of 1000, so it
        // travels as 0..15 on our wire. Protobuf carries the value, so the constraint must be the real
        // range — leaking the wire code here would ship a schema that rejects every valid reading.
        var outDir = Path.Combine(_dir, "demo-offset");
        Run("generate", Sample("protobuf-demo.pdproj"), "--out", outDir, "--target", "proto",
            "--namespace", "demo");

        var schema = File.ReadAllText(Path.Combine(outDir, "main.proto"));

        Assert.Contains("uint32.gte = 1000", schema, StringComparison.Ordinal);
        Assert.Contains("uint32.lte = 1015", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void The_protobuf_sample_carries_both_ends_of_its_variable_array()
    {
        // `payload` is declared 1..64. Without a declared minimum a repeated field accepts an empty list,
        // so the floor is the half that only exists because the model carries it.
        var outDir = Path.Combine(_dir, "demo-bounds");
        Run("generate", Sample("protobuf-demo.pdproj"), "--out", outDir, "--target", "proto",
            "--namespace", "demo");

        var schema = File.ReadAllText(Path.Combine(outDir, "main.proto"));

        Assert.Contains("min_items: 1, max_items: 64", schema, StringComparison.Ordinal);
    }

    // ---- compiling the schema ----------------------------------------------------------------------

    [Fact]
    public void An_unknown_protoc_language_is_a_usage_error()
    {
        // Checked before anything runs, and the message names the C confusion explicitly, because
        // "--protoc-out c" is the obvious thing to type and protobuf has no C backend.
        var path = WriteProject(ConstrainedProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "x"),
            "--target", "proto", "--protoc-out", "c");

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("no C output", err, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trailing_protoc_out_with_no_language_is_a_usage_error()
    {
        var path = WriteProject(ConstrainedProject());
        var (code, _, err) = Run("generate", path, "--out", Path.Combine(_dir, "x"),
            "--target", "proto", "--protoc-out");

        Assert.Equal(CommandLine.ExitUsage, code);
        Assert.Contains("--protoc-out", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiling_the_schema_produces_real_cpp_beside_it()
    {
        if (ProtocCompiler.Locate() is null) return;   // ProtocCompilerTests fails loudly for this

        var path = WriteProject(ConstrainedProject());
        var outDir = Path.Combine(_dir, "cpp");

        var (code, output, err) = Run("generate", path, "--out", outDir, "--target", "proto",
            "--namespace", "demo", "--protoc-out", "cpp");

        Assert.Equal(0, code);
        Assert.Equal("", err);
        Assert.True(File.Exists(Path.Combine(outDir, "main.pb.h")));
        Assert.True(File.Exists(Path.Combine(outDir, "main.pb.cc")));
        Assert.Contains("compiled main.pb.h", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_C_target_ignores_protoc_out()
    {
        // The flag only means something for a target that emits a schema. Asking for it alongside the C
        // target must not fail — it should simply have nothing to compile.
        if (ProtocCompiler.Locate() is null) return;

        var path = WriteProject(CleanProject());
        var outDir = Path.Combine(_dir, "c-with-flag");

        var (code, _, _) = Run("generate", path, "--out", outDir, "--target", "c");

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(outDir, "main.h")));
    }

    [Fact]
    public void The_telemetry_sample_still_generates_C()
    {
        // The other sample, and the one that must never be caught by the protobuf gate: bit-packed
        // messages are what the C target is for.
        var outDir = Path.Combine(_dir, "telemetry");
        var (code, _, _) = Run("generate", Sample("telemetry.pdproj"), "--out", outDir, "--target", "c");

        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(outDir, "protodesigner_runtime.h")));
    }
}
