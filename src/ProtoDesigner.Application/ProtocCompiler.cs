using System.Diagnostics;

namespace ProtoDesigner.Application;

/// <summary>A language protoc can emit from a generated schema.</summary>
/// <param name="Id">What the user types: <c>cpp</c>, <c>csharp</c>, …</param>
/// <param name="Flag">The protoc flag, without the output directory.</param>
/// <param name="Label">Short description for a picker.</param>
/// <param name="Note">What the caller has to know before choosing it, or null.</param>
public sealed record ProtocLanguage(string Id, string Flag, string Label, string? Note = null);

/// <summary>What a protoc run produced.</summary>
/// <param name="Ran">False when protoc could not be found, or nothing was asked for.</param>
/// <param name="Produced">Paths relative to the output directory, in sorted order.</param>
/// <param name="Error">Why it failed, or null on success.</param>
/// <param name="CompiledValidate">
/// Whether Buf's <c>validate.proto</c> was compiled alongside the schema. It is not decoration: the
/// emitted <c>main.pb.h</c> carries <c>#include "buf/validate/validate.pb.h"</c>, so without it the C++
/// does not compile. Worth reporting, because it is otherwise a surprising pile of files the user never
/// asked for — the C# form of it alone is close to a megabyte.
/// </param>
public sealed record ProtocCompilation(
    bool Ran, IReadOnlyList<string> Produced, string? Error, bool CompiledValidate = false)
{
    public bool Succeeded => Ran && Error is null;

    public static ProtocCompilation Failed(string error) => new(Ran: false, [], error);

    public static readonly ProtocCompilation NotRequested = new(Ran: false, [], null);
}

/// <summary>
/// Runs the real protobuf compiler over an emitted schema, so a caller gets usable source rather than a
/// <c>.proto</c> they still have to compile themselves.
/// </summary>
/// <remarks>
/// <para>
/// This is a step <b>after</b> generation, not part of it. An <c>IProtocolGenerator</c> is a pure
/// function from IR to text — that is what makes golden files meaningful and what lets the whole suite
/// run without a toolchain. Shelling out to another compiler inside one would give up both. So the
/// generator emits the schema, the caller writes it, and this compiles what was written.
/// </para>
/// <para>
/// protoc is not vendored, for the same reason the tests do not vendor it: a 12 MB platform binary would
/// live in git history forever. It is located, and its absence is reported rather than ignored — a run
/// that was asked for C++ and silently produced none is the failure this project keeps refusing to ship.
/// </para>
/// </remarks>
public static class ProtocCompiler
{
    /// <summary>Overrides the search with an explicit path to protoc.</summary>
    public const string PathVariable = "PROTODESIGNER_PROTOC";

    /// <summary>
    /// The languages offered.
    /// </summary>
    /// <remarks>
    /// <b>There is no C.</b> Google's protobuf has never had a C backend — <c>--cpp_out</c> emits C++
    /// that needs libprotobuf, a C++ runtime, and the heap. C consumers use a third-party implementation
    /// (nanopb, protobuf-c) with its own generator, which is a different tool entirely. If you want a C
    /// codec for these messages, that is what this project's own <c>c</c> target is for; it is
    /// freestanding and allocation-free, and it speaks our wire format rather than protobuf's.
    /// </remarks>
    public static IReadOnlyList<ProtocLanguage> Languages { get; } =
    [
        new("cpp", "--cpp_out", "C++ (.pb.h / .pb.cc)",
            "Needs libprotobuf and a C++ runtime. Not freestanding — it allocates."),
        new("csharp", "--csharp_out", "C# (.cs)",
            "Needs the Google.Protobuf package at build time."),
        new("java", "--java_out", "Java (.java)"),
        new("python", "--python_out", "Python (_pb2.py)"),
    ];

    public static ProtocLanguage? FindLanguage(string id) =>
        Languages.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The protobuf compiler, or null when none can be found.</summary>
    /// <remarks>
    /// Looks at <see cref="PathVariable"/>, then for a <c>protobuf/</c> directory above the running
    /// binary holding <c>bin/protoc</c>, then <c>PATH</c>. Same convention the conformance tests use, so
    /// one layout serves both.
    /// </remarks>
    public static string? Locate()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? explicitPath : null;

        var exe = OperatingSystem.IsWindows() ? "protoc.exe" : "protoc";

        if (FindToolchainRoot() is { } root)
        {
            var candidate = new[] { Path.Combine(root, "bin", exe), Path.Combine(root, exe) }
                .FirstOrDefault(File.Exists);
            if (candidate is not null) return candidate;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>A <c>protobuf/</c> directory above the running binary, or null.</summary>
    private static string? FindToolchainRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var root = Path.Combine(dir, "protobuf");
            if (Directory.Exists(root)) return root;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Compiles every <c>.proto</c> in <paramref name="directory"/> into the requested languages, in
    /// place.
    /// </summary>
    /// <param name="directory">Where the schema was written; also where output lands.</param>
    /// <param name="languageIds">Language ids from <see cref="Languages"/>. Empty means do nothing.</param>
    public static ProtocCompilation Run(string directory, IReadOnlyList<string> languageIds)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(languageIds);

        if (languageIds.Count == 0) return ProtocCompilation.NotRequested;

        var languages = new List<ProtocLanguage>();
        foreach (var id in languageIds)
        {
            var language = FindLanguage(id);
            if (language is null)
                return ProtocCompilation.Failed(
                    $"Unknown protoc language '{id}'. Available: {string.Join(", ", Languages.Select(l => l.Id))}.");
            languages.Add(language);
        }

        var compiler = Locate();
        if (compiler is null)
            return ProtocCompilation.Failed(
                "protoc was not found, so no code was compiled from the schema. Put it in protobuf/bin/, "
                + $"set {PathVariable}, or put it on PATH.");

        var sources = Directory.GetFiles(directory, "*.proto", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (sources.Count == 0)
            return ProtocCompilation.Failed(
                "No .proto files were written, so there was nothing for protoc to compile. This target "
                + "emits a schema only for messages protobuf can express — check whether any survived the gate.");

        var before = Snapshot(directory);

        // The schema imports buf/validate/validate.proto when constraints are on, and protoc resolves
        // imports by path rather than by package. Staging a copy makes the compile work regardless of how
        // the toolchain is laid out on disk — and compiling it too, so the emitted C++ has a
        // validate.pb.h to include rather than a dangling reference.
        var staged = StageValidateProto(directory, sources);
        if (staged is not null) sources.Add(staged);

        var psi = new ProcessStartInfo(compiler)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = directory,
        };
        psi.ArgumentList.Add($"--proto_path={directory}");

        if (FindToolchainRoot() is { } root && Directory.Exists(Path.Combine(root, "include")))
            psi.ArgumentList.Add($"--proto_path={Path.Combine(root, "include")}");

        foreach (var language in languages) psi.ArgumentList.Add($"{language.Flag}={directory}");
        foreach (var source in sources) psi.ArgumentList.Add(source);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(milliseconds: 120_000))
            return ProtocCompilation.Failed("protoc did not finish within two minutes.");

        var output = (stdout.Result + stderr.Result).Trim();

        if (proc.ExitCode != 0)
            return new ProtocCompilation(Ran: true, [],
                $"protoc failed (exit {proc.ExitCode}):{Environment.NewLine}{output}");

        var produced = Snapshot(directory)
            .Except(before, StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return new ProtocCompilation(Ran: true, produced, Error: null, CompiledValidate: staged is not null);
    }

    /// <summary>
    /// Puts Buf's <c>validate.proto</c> where the generated schema's import expects it, if the schema
    /// needs it and it can be found. Returns the staged path, or null.
    /// </summary>
    private static string? StageValidateProto(string directory, IReadOnlyList<string> sources)
    {
        const string importPath = "buf/validate/validate.proto";

        var needed = sources.Any(s =>
            File.ReadAllText(s).Contains(importPath, StringComparison.Ordinal));
        if (!needed) return null;

        var target = Path.Combine(directory, "buf", "validate", "validate.proto");
        if (File.Exists(target)) return target;

        if (FindToolchainRoot() is not { } root) return null;

        var source = new[]
            {
                Path.Combine(root, "validate.proto"),
                Path.Combine(root, "buf", "validate", "validate.proto"),
                Path.Combine(root, "include", "buf", "validate", "validate.proto"),
            }
            .FirstOrDefault(File.Exists);

        if (source is null) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        return target;
    }

    /// <summary>Every file under a directory, as paths relative to it.</summary>
    private static HashSet<string> Snapshot(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/'))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
}
