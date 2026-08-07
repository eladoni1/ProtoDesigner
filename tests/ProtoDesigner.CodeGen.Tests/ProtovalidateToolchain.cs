using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>Finds a Go toolchain and builds the protovalidate harness that runs beside these tests.</summary>
/// <remarks>
/// <para>
/// Go rather than C#: protovalidate has no .NET implementation, and the point of this check is to run
/// the constraints through a real, independent implementation of the spec. A C# reimplementation would
/// only prove the generator agrees with our reading of protovalidate, which is exactly the assumption
/// under test.
/// </para>
/// <para>
/// The harness source and its <c>go.mod</c> live in the repo under <c>Protovalidate/</c>, so this check
/// survives deleting the gitignored <c>protobuf/</c> staging directory. What it needs from outside is
/// the Go toolchain and its module cache.
/// </para>
/// </remarks>
internal static class GoLocator
{
    /// <summary>Set to 1 to accept schema-only coverage on a machine with no Go toolchain.</summary>
    /// <remarks>
    /// Opting out is deliberate, as with protoc and MSVC. Without this check the suite proves the
    /// constraints <em>parse</em>; it does not prove any of them ever rejects anything.
    /// </remarks>
    public const string SkipVariable = "PROTODESIGNER_SKIP_PROTOVALIDATE";

    /// <summary>Overrides the search with an explicit path to the Go executable.</summary>
    public const string PathVariable = "PROTODESIGNER_GO";

    public static bool SkipRequested =>
        Environment.GetEnvironmentVariable(SkipVariable) is "1" or "true";

    private static readonly Lazy<ProtovalidateHarness?> Harness = new(Build, isThreadSafe: true);

    /// <summary>The built harness, or null when Go is unavailable or the build failed.</summary>
    /// <remarks>Built once per test run; Go's own build cache makes repeated runs cheap.</remarks>
    public static ProtovalidateHarness? Find() => Harness.Value;

    /// <summary>Why <see cref="Find"/> returned null, for a failure message worth reading.</summary>
    public static string? Failure { get; private set; }

    private static string? FindGo()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? explicitPath : null;

        var exe = OperatingSystem.IsWindows() ? "go.exe" : "go";

        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe))
            .FirstOrDefault(File.Exists);
        if (onPath is not null) return onPath;

        // The Windows installer does not add Go to PATH for already-open sessions, and a test host
        // inherits whatever environment it was launched with. Check the default location too.
        return new[]
            {
                @"C:\Program Files\Go\bin\go.exe",
                "/usr/local/go/bin/go",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Go", "bin", exe),
            }
            .FirstOrDefault(File.Exists);
    }

    private static ProtovalidateHarness? Build()
    {
        var go = FindGo();
        if (go is null)
        {
            Failure = "no Go toolchain found on PATH, in the default install location, or via "
                      + PathVariable;
            return null;
        }

        var source = Path.Combine(AppContext.BaseDirectory, "Protovalidate");
        if (!File.Exists(Path.Combine(source, "harness.go")))
        {
            Failure = $"the harness source was not copied to the output directory ({source})";
            return null;
        }

        // A stable build directory rather than a fresh temp one: Go caches compiled packages by content,
        // so the second run of the day costs a link instead of building cel-go from scratch.
        var buildDir = Path.Combine(Path.GetTempPath(), "pd-protovalidate-harness");
        Directory.CreateDirectory(buildDir);
        foreach (var name in new[] { "harness.go", "go.mod", "go.sum" })
            File.Copy(Path.Combine(source, name), Path.Combine(buildDir, name), overwrite: true);

        var exe = Path.Combine(buildDir, OperatingSystem.IsWindows() ? "harness.exe" : "harness");

        var psi = new ProcessStartInfo(go)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = buildDir,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add(".");

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(milliseconds: 600_000))
        {
            Failure = "go build did not finish within ten minutes";
            return null;
        }

        if (proc.ExitCode != 0)
        {
            Failure = $"go build failed:\n{stdout.Result}{stderr.Result}";
            return null;
        }

        return new ProtovalidateHarness(exe);
    }
}

/// <summary>One violation reported by protovalidate.</summary>
/// <param name="Field">
/// protovalidate's dotted path to what failed: <c>pressure</c>, <c>samples[2]</c>, <c>header.id</c>.
/// </param>
/// <param name="Rule">protovalidate's rule id, e.g. <c>uint32.gte_lte</c> or <c>repeated.max_items</c>.</param>
/// <param name="Message">protovalidate's human-readable explanation.</param>
internal sealed record ProtoViolation(string Field, string Rule, string Message);

/// <summary>The outcome of validating one payload.</summary>
internal sealed record ProtovalidateReport(bool Valid, IReadOnlyList<ProtoViolation> Violations)
{
    /// <summary>Whether any violation is against the named field or something inside it.</summary>
    /// <remarks>
    /// Matches the path's first segment, so <c>samples</c> covers the <c>samples[2]</c> an element
    /// violation reports. Asserting on the field is what separates "a rule fired" from "the rule fired
    /// on the thing it was written for" — a constraint copied onto the wrong field would still reject
    /// the payload and would still look correct without this.
    /// </remarks>
    public bool Mentions(string field) =>
        Violations.Any(v => v.Field == field
                            || v.Field.StartsWith(field + "[", StringComparison.Ordinal)
                            || v.Field.StartsWith(field + ".", StringComparison.Ordinal));

    public override string ToString() => Valid
        ? "valid"
        : string.Join("; ", Violations.Select(v => $"{v.Field}: {v.Rule} ({v.Message})"));
}

/// <summary>The built harness: feeds a JSON payload through protovalidate against a compiled schema.</summary>
internal sealed class ProtovalidateHarness(string executable)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Validates <paramref name="payloadJson"/> as an instance of <paramref name="messageName"/>.
    /// </summary>
    /// <remarks>
    /// The payload is written as UTF-8 without a byte order mark. protojson rejects a leading BOM as a
    /// syntax error, and the resulting failure looks like a bad test case rather than a bad encoder.
    /// </remarks>
    public ProtovalidateReport Validate(string descriptorSet, string messageName, string payloadJson)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.ArgumentList.Add(descriptorSet);
        psi.ArgumentList.Add(messageName);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        proc.StandardInput.Write(payloadJson);
        proc.StandardInput.Close();

        proc.WaitForExit(milliseconds: 120_000);

        var output = stdout.Result;

        // A rule violation exits 0 with a report; a non-zero exit means the harness itself could not run
        // the case, which is never a valid answer to "is this message valid?".
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"the protovalidate harness failed on {messageName}:\n{output}{stderr.Result}");

        var report = JsonSerializer.Deserialize<Payload>(output, Json)
                     ?? throw new InvalidOperationException($"unreadable harness output: {output}");

        return new ProtovalidateReport(report.Valid, report.Violations ?? []);
    }

    private sealed record Payload(bool Valid, List<ProtoViolation>? Violations);
}
