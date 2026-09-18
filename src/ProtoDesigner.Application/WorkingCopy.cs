using ProtoDesigner.Core.Merge;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Application;

/// <summary>What a save did, and what it needs a person for.</summary>
public enum SaveStatus
{
    /// <summary>Nobody else had touched the file. It was written as it stood.</summary>
    Written,

    /// <summary>Someone else's changes were taken in first, then the whole thing was written.</summary>
    Merged,

    /// <summary>Two edits could not both be kept. Nothing was written and nothing was changed.</summary>
    Conflicted,
}

/// <param name="Merged">One line per change taken from the stored copy. Empty unless <see cref="SaveStatus.Merged"/>.</param>
/// <param name="Validation">
/// <see cref="Severity.Error"/> findings on what was written. A merge can be clean and still produce an
/// invalid project, and this reports that rather than refusing the save: the editor's standing rule is
/// that a duplicate wire id is reported, not refused, and a save that throws work away to enforce a rule
/// is worse than a file that needs one fixing.
/// </param>
public sealed record SaveOutcome(
    SaveStatus Status,
    IReadOnlyList<string> Merged,
    IReadOnlyList<MergeConflict> Conflicts,
    IReadOnlyList<Diagnostic> Validation)
{
    public bool Wrote => Status != SaveStatus.Conflicted;
}

/// <summary>
/// A project open for editing, and the save that has to cope with someone else having saved meanwhile.
/// </summary>
/// <remarks>
/// <para>
/// A plain save is last-writer-wins: whoever saves second silently destroys the other's work, and neither
/// of them finds out. Two people on one protocol hit that constantly, because they are usually editing
/// different messages on the same bus — the case where nothing genuinely conflicts.
/// </para>
/// <para>
/// So a save reads the stored copy first and merges into it by entity. The baseline is the project as it
/// was when this copy was opened, which is what makes it a three-way merge rather than a guess: without
/// it, "this field is missing from your copy" cannot be told apart from "I deleted this field".
/// </para>
/// <para>
/// <b>Every save merges, even when nothing came in.</b> There is no "has it changed?" shortcut, because a
/// timestamp or hash checked before the read is stale by the time the read happens, and a merge against an
/// identical copy already applies nothing. One path that is always right beats two that are usually right.
/// </para>
/// <para>
/// <b>A conflicted save writes nothing and changes nothing.</b> The working copy is left exactly as the
/// user had it, so the conflict list is something to act on rather than a state to recover from. Resolving
/// it is a person's job today — a merge view is later Phase 6 work, and the report names the entity.
/// </para>
/// </remarks>
public sealed class WorkingCopy
{
    private readonly IProjectRepository _repository;

    /// <summary>The project as it was last read from or written to storage. Never edited.</summary>
    /// <remarks>
    /// Null until the first save of a project that was never stored, which is the one case with no
    /// common ancestor to merge against — nobody else can have edited a file that does not exist yet.
    /// </remarks>
    private Project? _baseline;

    private WorkingCopy(IProjectRepository repository, Project project, string path, Project? baseline)
    {
        _repository = repository;
        Project = project;
        Path = path;
        _baseline = baseline;
    }

    /// <summary>Opens a stored project for editing.</summary>
    /// <remarks>
    /// The project is read twice on purpose: once as the working copy and once as the untouched baseline.
    /// Two reads of one file are two independent object graphs, which is the cheapest correct deep copy
    /// available here and costs nothing measurable on a schema file.
    /// </remarks>
    public static WorkingCopy Open(IProjectRepository repository, string path)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new WorkingCopy(repository, repository.Load(path), path, repository.Load(path));
    }

    /// <summary>Takes a project that has never been stored, to be written at <paramref name="path"/>.</summary>
    public static WorkingCopy Started(IProjectRepository repository, Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new WorkingCopy(repository, project, path, baseline: null);
    }

    /// <summary>The copy being edited. Every mutation goes through the command journal, as ever.</summary>
    public Project Project { get; }

    public string Path { get; private set; }

    /// <summary>
    /// Merges in whatever was stored since this copy was opened, then writes the result.
    /// </summary>
    public SaveOutcome Save()
    {
        if (_baseline is null) return Write(SaveStatus.Written, Array.Empty<string>(), Array.Empty<Diagnostic>());

        var stored = _repository.Load(Path);
        var merge = ProjectMerge.Merge(_baseline, Project, stored);

        if (merge.Conflicts.Count > 0)
            return new SaveOutcome(SaveStatus.Conflicted, Array.Empty<string>(), merge.Conflicts,
                                   Array.Empty<Diagnostic>());

        return Write(merge.Applied.Count == 0 ? SaveStatus.Written : SaveStatus.Merged,
                     merge.Applied, merge.Validation);
    }

    /// <summary>
    /// Writes to a different path, replacing whatever is there.
    /// </summary>
    /// <remarks>
    /// Deliberately does not merge. "Save as" is a fork: the user named a destination, and merging into
    /// whatever happens to be there would be a different operation than the one they asked for. The new
    /// path becomes this copy's own from here on, so ordinary saves resume merging against it.
    /// </remarks>
    public SaveOutcome SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
        return Write(SaveStatus.Written, Array.Empty<string>(), Array.Empty<Diagnostic>());
    }

    private SaveOutcome Write(SaveStatus status, IReadOnlyList<string> merged, IReadOnlyList<Diagnostic> validation)
    {
        _repository.Save(Project, Path);

        // Re-read rather than keeping the copy just written: the baseline has to be what storage now
        // holds, and only a read proves that is what we think it is.
        _baseline = _repository.Load(Path);

        return new SaveOutcome(status, merged, Array.Empty<MergeConflict>(), validation);
    }
}
