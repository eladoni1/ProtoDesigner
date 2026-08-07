using ProtoDesigner.Core.Ir;

namespace ProtoDesigner.CodeGen;

/// <summary>
/// Produces one language's encode/decode source from a resolved <see cref="ProtocolIr"/>. The IR is
/// the ONLY input — a generator never touches Project/Bus/Message. That one-way boundary is what
/// makes adding a second language a matter of adding a generator, not touching the editor.
/// </summary>
public interface IProtocolGenerator
{
    /// <summary>Short id used by the CLI: "cpp", "csharp", "rust", …</summary>
    string Id { get; }

    /// <summary>Human-readable name for menus.</summary>
    string DisplayName { get; }

    /// <summary>
    /// The target-specific settings this generator understands, for the UI and CLI to offer. Empty by
    /// default, so a generator with nothing to configure declares nothing.
    /// </summary>
    IReadOnlyList<GeneratorOption> Options => Array.Empty<GeneratorOption>();

    /// <summary>
    /// Whether this target can express every message, or only some.
    /// </summary>
    /// <remarks>
    /// True for a target whose wire format is ours — it can always represent what the layout engine
    /// produced. False for one borrowing a foreign format, where some messages have no representation
    /// at all; the caller must then narrow the scope and say which were left out rather than emitting
    /// something that quietly means something else.
    /// </remarks>
    bool CoversEveryMessage => true;

    /// <summary>
    /// Generates for several buses at once, sharing one set of type declarations between them.
    /// </summary>
    /// <remarks>
    /// This is the primary entry point, not a convenience wrapper. Types live in a project-wide library,
    /// so a struct used by two buses is one type — and once generated code declares real structs rather
    /// than flattening them, emitting it into both bus headers makes the two impossible to include in the
    /// same translation unit. Generating the set together is what lets the shared declarations go in one
    /// place. It also matches how the buses are actually selected: "everything module X touches" spans
    /// however many buses that module sits on.
    /// </remarks>
    GeneratedFileSet Generate(IReadOnlyList<ProtocolIr> buses, GeneratorOptions options);

    /// <summary>Convenience for the single-bus case.</summary>
    GeneratedFileSet Generate(ProtocolIr ir, GeneratorOptions options) =>
        Generate(new[] { ir }, options);
}

/// <summary>Options every generator understands, plus a bag for the ones only some do.</summary>
/// <param name="Namespace">
/// Wraps the output. A target without namespaces uses it as a symbol prefix instead — see the C target.
/// </param>
/// <param name="IncludeReadme">Whether to emit the explanatory README alongside the code.</param>
/// <param name="TargetOptions">
/// Target-specific settings, keyed by the option's <see cref="GeneratorOption.Key"/>. A generator reads
/// its own keys and ignores every other, so one dictionary can carry settings for whichever target the
/// caller happens to pick.
/// </param>
/// <remarks>
/// A string bag rather than a property per target. Widening this record every time a language wants a
/// switch would make a shared type grow language-specific fields, which is exactly what the "no language
/// bias" boundary exists to prevent — and the UI and CLI can render whatever a generator
/// <see cref="IProtocolGenerator.Options">declares</see> without knowing anything about it.
/// </remarks>
public sealed record GeneratorOptions(
    string Namespace = "proto",
    bool IncludeReadme = true,
    IReadOnlyDictionary<string, string>? TargetOptions = null)
{
    /// <summary>A boolean option, or <paramref name="fallback"/> when unset or unparseable.</summary>
    public bool Flag(string key, bool fallback = false)
    {
        var text = Value(key);
        if (text is null) return fallback;

        // Accepting the obvious spellings keeps `--option protovalidate=1` from silently meaning false.
        return text.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    /// <summary>A raw option value, or null when unset.</summary>
    public string? Value(string key) =>
        TargetOptions is not null && TargetOptions.TryGetValue(key, out var value) ? value : null;

    /// <summary>The same options with one target setting added or replaced.</summary>
    public GeneratorOptions With(string key, string value)
    {
        var merged = TargetOptions is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(TargetOptions, StringComparer.OrdinalIgnoreCase);

        merged[key] = value;
        return this with { TargetOptions = merged };
    }
}

/// <summary>How an option is presented and entered.</summary>
public enum GeneratorOptionKind
{
    /// <summary>A checkbox. Values are parsed by <see cref="GeneratorOptions.Flag"/>.</summary>
    Flag,

    /// <summary>Free text.</summary>
    Text,
}

/// <summary>
/// One setting a generator understands, declared so the editor and the CLI can offer it without either
/// of them knowing which target is selected.
/// </summary>
public sealed record GeneratorOption(
    string Key,
    string Label,
    string Description,
    GeneratorOptionKind Kind = GeneratorOptionKind.Flag,
    string? Default = null);

/// <summary>A single output file, in memory. The CLI decides whether to write it to disk.</summary>
public sealed record GeneratedFile(string RelativePath, string Contents);

/// <summary>Every file a generator produced, in a stable order suitable for golden-file diffing.</summary>
public sealed record GeneratedFileSet(IReadOnlyList<GeneratedFile> Files)
{
    public static GeneratedFileSet Empty { get; } = new(Array.Empty<GeneratedFile>());

    public GeneratedFile? Find(string relativePath) =>
        Files.FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.Ordinal));
}
