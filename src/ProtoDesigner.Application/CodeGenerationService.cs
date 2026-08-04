using ProtoDesigner.CodeGen;
using ProtoDesigner.Core.Ir;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Application;

/// <summary>What a generation run produced, or why it produced nothing.</summary>
/// <param name="Files">The generated files, in memory. Empty when <paramref name="Refused"/> is true.</param>
/// <param name="Refused">
/// True when the model had validation errors and nothing was generated. This is not an exception: a
/// half-finished protocol is a normal state for an editor to be in, and the caller wants the diagnostics
/// to show, not a stack trace.
/// </param>
/// <param name="BlockingErrors">The <see cref="Severity.Error"/> diagnostics that caused a refusal.</param>
/// <param name="MessageCount">How many messages the run covered — for reporting.</param>
public sealed record CodeGenerationResult(
    GeneratedFileSet Files,
    bool Refused,
    IReadOnlyList<Diagnostic> BlockingErrors,
    int MessageCount)
{
    public static CodeGenerationResult RefusedWith(IReadOnlyList<Diagnostic> errors) =>
        new(GeneratedFileSet.Empty, true, errors, 0);
}

/// <summary>
/// The one place "generate code from this project" is implemented: validate, refuse on errors, build the
/// IR for each scope, hand the whole set to the generator.
/// </summary>
/// <remarks>
/// <para>
/// Producing the files and writing them to disk are separate calls on purpose. The editor shows the
/// output in a preview before anything touches the filesystem, and a caller that never writes — a test,
/// a preview pane — should not need a temp directory to see what it would have got.
/// </para>
/// <para>
/// The refusal rule is load-bearing and lives here rather than in each caller: a model with an
/// <see cref="Severity.Error"/> must fail with a diagnostic that names the field, not at the C++ compiler
/// with a mystery about a struct member that is the wrong width.
/// </para>
/// </remarks>
public static class CodeGenerationService
{
    /// <summary>
    /// Validates the project and, if it is clean, generates every scope in one pass.
    /// </summary>
    /// <remarks>
    /// All scopes go to the generator together rather than one at a time. Types are project-wide, so a
    /// struct used by two buses is one type; generating bus by bus would have each write its own copy of
    /// the shared declarations and the last one would win, leaving the others referring to types that are
    /// no longer declared.
    /// </remarks>
    public static CodeGenerationResult Generate(
        Project project,
        IProtocolGenerator generator,
        IReadOnlyList<GenerationScope> scopes,
        GeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(scopes);
        options ??= new GeneratorOptions();

        var errors = new Validator().Validate(project)
            .Where(d => d.Severity == Severity.Error)
            .ToList();
        if (errors.Count > 0) return CodeGenerationResult.RefusedWith(errors);

        if (scopes.Count == 0)
            return new CodeGenerationResult(GeneratedFileSet.Empty, false, Array.Empty<Diagnostic>(), 0);

        var builder = new IrBuilder();
        var irs = scopes.Select(s => builder.Build(project, s.Bus, s.MessageIds)).ToList();

        var files = generator.Generate(irs, options);
        return new CodeGenerationResult(files, false, Array.Empty<Diagnostic>(), scopes.Sum(s => s.Messages.Count));
    }

    /// <summary>
    /// Writes a generated set under <paramref name="outputDirectory"/>, creating directories as needed.
    /// Returns the full path of each file written, in the order the generator produced them.
    /// </summary>
    /// <remarks>
    /// I/O failures propagate. The caller knows whether a missing directory is a usage error worth an exit
    /// code or a message box, and this layer does not.
    /// </remarks>
    public static IReadOnlyList<string> Write(GeneratedFileSet files, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);

        var written = new List<string>(files.Files.Count);
        foreach (var file in files.Files)
        {
            var full = Path.Combine(outputDirectory, file.RelativePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, file.Contents);
            written.Add(full);
        }

        return written;
    }
}
