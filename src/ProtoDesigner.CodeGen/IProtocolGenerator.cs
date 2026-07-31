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

    GeneratedFileSet Generate(ProtocolIr ir, GeneratorOptions options);
}

/// <summary>Options every generator understands. Language-specific options belong on the concrete generator.</summary>
public sealed record GeneratorOptions(
    string Namespace = "proto",
    bool IncludeReadme = true);

/// <summary>A single output file, in memory. The CLI decides whether to write it to disk.</summary>
public sealed record GeneratedFile(string RelativePath, string Contents);

/// <summary>Every file a generator produced, in a stable order suitable for golden-file diffing.</summary>
public sealed record GeneratedFileSet(IReadOnlyList<GeneratedFile> Files)
{
    public static GeneratedFileSet Empty { get; } = new(Array.Empty<GeneratedFile>());

    public GeneratedFile? Find(string relativePath) =>
        Files.FirstOrDefault(f => string.Equals(f.RelativePath, relativePath, StringComparison.Ordinal));
}
