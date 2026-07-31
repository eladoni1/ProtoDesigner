using ProtoDesigner.CodeGen;
using ProtoDesigner.CodeGen.Cpp;
using ProtoDesigner.Core.Ir;
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

    private static readonly IProtocolGenerator[] Generators = { new CppGenerator() };

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
            stdout.WriteLine($"  {g.Id,-8} {g.DisplayName}");
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
            stderr.WriteLine("usage: protodesigner generate <file.pdproj> --target <id> --out <dir> [--bus <name>] [--namespace <ns>]");
            return ExitUsage;
        }

        var path = args[0];
        var target = GetOption(args, "--target") ?? "cpp";
        var outDir = GetOption(args, "--out");
        var busName = GetOption(args, "--bus");
        var ns = GetOption(args, "--namespace") ?? "proto";

        if (outDir is null)
        {
            stderr.WriteLine("Missing required --out <dir>.");
            return ExitUsage;
        }

        var generator = Generators.FirstOrDefault(g => string.Equals(g.Id, target, StringComparison.OrdinalIgnoreCase));
        if (generator is null)
        {
            stderr.WriteLine($"Unknown target '{target}'. Run 'protodesigner targets' to list them.");
            return ExitUsage;
        }

        if (!TryLoad(path, stderr, out var project)) return ExitIoError;

        // Never generate from an unvalidated model — that rule exists so a broken protocol fails at
        // the CLI with a diagnostic rather than at the compiler with a mystery.
        var diagnostics = new Validator().Validate(project!);
        var errors = diagnostics.Where(d => d.Severity == Severity.Error).ToArray();
        if (errors.Length > 0)
        {
            stderr.WriteLine($"Refusing to generate: {errors.Length} validation error(s).");
            foreach (var d in errors)
                stderr.WriteLine($"  {d.Code} {d.Message} [{d.Target}]");
            return ExitValidationErrors;
        }

        var buses = busName is null
            ? project!.Buses.ToArray()
            : project!.Buses.Where(b => string.Equals(b.Name, busName, StringComparison.Ordinal)).ToArray();

        if (buses.Length == 0)
        {
            stderr.WriteLine(busName is null
                ? "Project contains no buses."
                : $"No bus named '{busName}'.");
            return ExitUsage;
        }

        var builder = new IrBuilder();
        var written = 0;

        try
        {
            Directory.CreateDirectory(outDir);
            foreach (var bus in buses)
            {
                var ir = builder.Build(project!, bus);
                var set = generator.Generate(ir, new GeneratorOptions(Namespace: ns));
                foreach (var file in set.Files)
                {
                    var full = Path.Combine(outDir, file.RelativePath);
                    var dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(full, file.Contents);
                    stdout.WriteLine($"  wrote {file.RelativePath}");
                    written++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Could not write output: {ex.Message}");
            return ExitIoError;
        }

        stdout.WriteLine($"Generated {written} file(s) for {buses.Length} bus(es) into {outDir}.");
        return ExitOk;
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
        stdout.WriteLine("  protodesigner generate <file.pdproj> --out <dir> [--target cpp] [--bus <name>] [--namespace <ns>]");
        stdout.WriteLine("  protodesigner targets");
        stdout.WriteLine();
        stdout.WriteLine("Exit codes:");
        stdout.WriteLine($"  {ExitOk}  success");
        stdout.WriteLine($"  {ExitValidationErrors}  validation errors (generation refused)");
        stdout.WriteLine($"  {ExitUsage}  usage error");
        stdout.WriteLine($"  {ExitIoError}  could not read or write a file");
    }
}
