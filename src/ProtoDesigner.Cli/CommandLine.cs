using ProtoDesigner.Application;
using ProtoDesigner.CodeGen;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Core.Validation;
using ProtoDesigner.Persistence.Json;

namespace ProtoDesigner.Cli;

/// <summary>
/// The headless entry point: <c>validate</c> and <c>generate</c>. Exit codes are the contract that
/// makes this usable in CI, so they are documented and tested rather than incidental.
/// </summary>
public static class CommandLine
{
    public const int ExitOk = 0;
    public const int ExitValidationErrors = 1;
    public const int ExitUsage = 2;
    public const int ExitIoError = 3;

    private static IReadOnlyList<IProtocolGenerator> Generators => GeneratorCatalog.All;

    public static int Run(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage(stdout);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        return args[0] switch
        {
            "validate" => Validate(args.Skip(1).ToArray(), stdout, stderr),
            "generate" => Generate(args.Skip(1).ToArray(), stdout, stderr),
            "targets" => ListTargets(stdout),
            _ => Unknown(args[0], stderr),
        };
    }

    private static bool IsHelp(string arg) =>
        arg is "-h" or "--help" or "help" or "/?";

    private static int Unknown(string verb, TextWriter stderr)
    {
        stderr.WriteLine($"Unknown command '{verb}'. Run 'protodesigner --help' for usage.");
        return ExitUsage;
    }

    private static int ListTargets(TextWriter stdout)
    {
        stdout.WriteLine("Available code generation targets:");
        foreach (var g in Generators)
        {
            stdout.WriteLine($"  {g.Id,-8} {g.DisplayName}");
            if (!g.CoversEveryMessage)
                stdout.WriteLine($"  {"",-8} (cannot express every message; incompatible ones are reported and skipped)");
            foreach (var option in g.Options)
                stdout.WriteLine($"  {"",-8}   --option {option.Key}=<{option.Kind.ToString().ToLowerInvariant()}>"
                    + $"   {option.Label} (default {option.Default ?? "unset"})");
        }
        return ExitOk;
    }

    // ---- validate ------------------------------------------------------------------------------

    private static int Validate(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("usage: protodesigner validate <file.pdproj> [--quiet]");
            return ExitUsage;
        }

        var path = args[0];
        var quiet = args.Contains("--quiet");

        if (!TryLoad(path, stderr, out var project)) return ExitIoError;

        var diagnostics = new Validator().Validate(project!);
        var errors = diagnostics.Count(d => d.Severity == Severity.Error);
        var warnings = diagnostics.Count(d => d.Severity == Severity.Warning);
        var infos = diagnostics.Count(d => d.Severity == Severity.Info);

        foreach (var d in diagnostics)
        {
            if (quiet && d.Severity != Severity.Error) continue;
            var writer = d.Severity == Severity.Error ? stderr : stdout;
            writer.WriteLine($"{d.Code} {d.Severity.ToString().ToLowerInvariant()}: {d.Message} [{d.Target}]");
        }

        stdout.WriteLine(diagnostics.Count == 0
            ? $"{project!.Name}: no issues."
            : $"{project!.Name}: {errors} error(s), {warnings} warning(s), {infos} info.");

        return errors > 0 ? ExitValidationErrors : ExitOk;
    }

    // ---- generate ------------------------------------------------------------------------------

    private static int Generate(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine("usage: protodesigner generate <file.pdproj> --out <dir> [--target <id>]");
            stderr.WriteLine("       [--bus <name> [--messages <a,b,c>]] | [--module <name>] [--namespace <ns>]");
            stderr.WriteLine("       [--option key=value ...]   run 'targets' to list each target's options");
            return ExitUsage;
        }

        var path = args[0];
        // C is the default: it is the only target that emits conversion code today, and its output
        // compiles as C or C++.
        var target = GetOption(args, "--target") ?? "c";
        var outDir = GetOption(args, "--out");
        var busName = GetOption(args, "--bus");
        var moduleName = GetOption(args, "--module");
        var messageList = GetOption(args, "--messages");
        var ns = GetOption(args, "--namespace") ?? "proto";

        if (!TryParseTargetOptions(args, stderr, out var targetOptions)) return ExitUsage;

        if (outDir is null)
        {
            stderr.WriteLine("Missing required --out <dir>.");
            return ExitUsage;
        }

        // --module already says which buses and which messages; combining it with either would leave two
        // answers to the same question and no obvious winner.
        if (moduleName is not null && (busName is not null || messageList is not null))
        {
            stderr.WriteLine("--module selects its own buses and messages; do not combine it with --bus or --messages.");
            return ExitUsage;
        }

        if (messageList is not null && busName is null)
        {
            stderr.WriteLine("--messages needs --bus, since message names are only unique within a bus.");
            return ExitUsage;
        }

        var generator = GeneratorCatalog.Find(target);
        if (generator is null)
        {
            stderr.WriteLine($"Unknown target '{target}'. Run 'protodesigner targets' to list them.");
            return ExitUsage;
        }

        if (!TryLoad(path, stderr, out var project)) return ExitIoError;

        if (!TryResolveScopes(project!, busName, moduleName, messageList, stderr, out var scopes))
            return ExitUsage;

        // The validate-then-build-IR-then-generate sequence lives in the application layer so this command
        // and the editor's Generate dialog cannot drift apart. Refusing on an Error is part of it: a broken
        // protocol fails here with a diagnostic rather than at the compiler with a mystery.
        var result = CodeGenerationService.Generate(
            project!, generator, scopes, new GeneratorOptions(ns, IncludeReadme: true, targetOptions));

        if (result.Refused)
        {
            stderr.WriteLine($"Refusing to generate: {result.BlockingErrors.Count} validation error(s).");
            foreach (var d in result.BlockingErrors)
                stderr.WriteLine($"  {d.Code} {d.Message} [{d.Target}]");
            return ExitValidationErrors;
        }

        // A target that cannot express every message must say which it left out. Silently exporting a
        // subset is how someone ships half a protocol and finds out from the other end.
        foreach (var skipped in result.Excluded)
            stdout.WriteLine($"  skipped {skipped.Message.Name}: {skipped.Reason}");

        if (result.MessageCount == 0)
        {
            stderr.WriteLine(result.Excluded.Count > 0
                ? $"Nothing generated: all {result.Excluded.Count} selected message(s) are outside what "
                  + $"the '{generator.Id}' target can express."
                : "Nothing generated: the selection covers no messages.");
            return ExitValidationErrors;
        }

        try
        {
            CodeGenerationService.Write(result.Files, outDir);
            foreach (var file in result.Files.Files)
                stdout.WriteLine($"  wrote {file.RelativePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Could not write output: {ex.Message}");
            return ExitIoError;
        }

        stdout.WriteLine(
            $"Generated {result.Files.Files.Count} file(s) covering {result.MessageCount} message(s) "
            + $"across {scopes.Count} bus(es) into {outDir}.");
        return ExitOk;
    }

    /// <summary>
    /// Turns the selection flags into the buses and messages to generate. Reports precisely what went
    /// wrong rather than silently producing an empty output directory.
    /// </summary>
    private static bool TryResolveScopes(Project project, string? busName, string? moduleName,
        string? messageList, TextWriter stderr, out IReadOnlyList<GenerationScope> scopes)
    {
        scopes = Array.Empty<GenerationScope>();

        if (moduleName is not null)
        {
            scopes = GenerationScopes.ForModuleNamed(project, moduleName);
            if (scopes.Count == 0)
            {
                var known = GenerationScopes.ModuleNames(project);
                stderr.WriteLine(known.Count == 0
                    ? $"No module named '{moduleName}'. This project declares no modules."
                    : $"No messages are routed to or from a module named '{moduleName}'. "
                      + $"Known modules: {string.Join(", ", known)}.");
                return false;
            }
            return true;
        }

        if (busName is null)
        {
            scopes = GenerationScopes.ForProject(project);
            if (scopes.Count == 0)
            {
                stderr.WriteLine("Project contains no buses with messages.");
                return false;
            }
            return true;
        }

        var bus = project.Buses.FirstOrDefault(b => string.Equals(b.Name, busName, StringComparison.Ordinal));
        if (bus is null)
        {
            stderr.WriteLine($"No bus named '{busName}'.");
            return false;
        }

        if (messageList is null)
        {
            scopes = GenerationScopes.ForBus(bus);
            if (scopes.Count == 0)
            {
                stderr.WriteLine($"Bus '{bus.Name}' has no messages.");
                return false;
            }
            return true;
        }

        var wantedNames = messageList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        var unknown = wantedNames
            .Where(n => bus.Messages.All(m => !string.Equals(m.Name, n, StringComparison.Ordinal)))
            .ToArray();
        if (unknown.Length > 0)
        {
            stderr.WriteLine($"Bus '{bus.Name}' has no message(s) named: {string.Join(", ", unknown)}.");
            return false;
        }

        var ids = bus.Messages
            .Where(m => wantedNames.Contains(m.Name, StringComparer.Ordinal))
            .Select(m => m.Id);

        scopes = GenerationScopes.ForMessages(bus, ids);
        return true;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static bool TryLoad(string path, TextWriter stderr, out Project? project)
    {
        project = null;
        if (!File.Exists(path))
        {
            stderr.WriteLine($"File not found: {path}");
            return false;
        }

        try
        {
            project = new JsonProjectRepository().Load(path);
            return true;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Could not read '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Collects repeated <c>--option key=value</c> pairs into the bag a generator reads its own settings
    /// from. An unparseable pair is a usage error rather than a silent no-op: a typo in a flag that
    /// changes the output should not look like the flag being ignored.
    /// </summary>
    private static bool TryParseTargetOptions(
        string[] args, TextWriter stderr, out Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] != "--option") continue;

            if (i + 1 >= args.Length)
            {
                stderr.WriteLine("--option needs a key=value pair.");
                return false;
            }

            var pair = args[++i];
            var split = pair.IndexOf('=');
            if (split <= 0 || split == pair.Length - 1)
            {
                stderr.WriteLine($"Could not read '--option {pair}'. Expected key=value, e.g. protovalidate=false.");
                return false;
            }

            options[pair[..split]] = pair[(split + 1)..];
        }

        return true;
    }

    private static string? GetOption(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        if (idx < 0 || idx + 1 >= args.Length) return null;
        return args[idx + 1];
    }

    private static void PrintUsage(TextWriter stdout)
    {
        stdout.WriteLine("protodesigner - binary protocol designer");
        stdout.WriteLine();
        stdout.WriteLine("Usage:");
        stdout.WriteLine("  protodesigner validate <file.pdproj> [--quiet]");
        stdout.WriteLine("  protodesigner generate <file.pdproj> --out <dir> [--target c] [--namespace <ns>]");
        stdout.WriteLine("  protodesigner targets");
        stdout.WriteLine();
        stdout.WriteLine("Choosing what to generate (default: every bus):");
        stdout.WriteLine("  --bus <name>              one bus, all of its messages");
        stdout.WriteLine("  --bus <name> --messages A,B   one bus, only those messages");
        stdout.WriteLine("  --module <name>           every message that module sends or receives, on every");
        stdout.WriteLine("                            bus it sits on — both directions, so loopback works");
        stdout.WriteLine();
        stdout.WriteLine("Exit codes:");
        stdout.WriteLine($"  {ExitOk}  success");
        stdout.WriteLine($"  {ExitValidationErrors}  validation errors (generation refused)");
        stdout.WriteLine($"  {ExitUsage}  usage error");
        stdout.WriteLine($"  {ExitIoError}  could not read or write a file");
    }
}
