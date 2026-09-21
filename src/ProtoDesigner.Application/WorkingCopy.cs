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

    /// <summary>Where this copy is stored, and what it looked like when it got there.</summary>
    /// <remarks>
    /// One nullable field rather than two, because the path and the baseline are the same fact: a project
    /// with nowhere to save has no common ancestor to merge against, and a project that has been saved
    /// always has both. Held separately they could disagree, and the merge would be measured against the
    /// wrong ancestor with nothing to show for it.
    /// </remarks>
    private (string Path, Project Baseline)? _storedAs;

    private WorkingCopy(IProjectRepository repository, Project project, (string, Project)? storedAs)
    {
        _repository = repository;
        Project = project;
        _storedAs = storedAs;
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
        return new WorkingCopy(repository, repository.Load(path), (path, repository.Load(path)));
    }

    /// <summary>Takes a project that has never been stored. It has nowhere to save until <see cref="SaveAs"/>.</summary>
    public static WorkingCopy Started(IProjectRepository repository, Project project)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(project);
        return new WorkingCopy(repository, project, storedAs: null);
    }

    /// <summary>The copy being edited. Every mutation goes through the command journal, as ever.</summary>
    public Project Project { get; }

    /// <summary>Where <see cref="Save"/> writes. Null until this project has been stored somewhere.</summary>
    public string? Path => _storedAs?.Path;

    /// <summary>
    /// Merges in whatever was stored since this copy was opened, then writes the result.
    /// </summary>
    /// <remarks>
    /// Requires a <see cref="Path"/>. A project that has never been stored has nowhere to save and no
    /// ancestor to merge against, so the caller has to ask the user for a destination and call
    /// <see cref="SaveAs"/> — a choice only the caller can make, which is why this refuses rather than guesses.
    /// </remarks>
    public SaveOutcome Save()
    {
        if (_storedAs is not { } stored)
            throw new InvalidOperationException(
                "This project has never been stored, so there is nowhere to save it. Call SaveAs first.");

        // Attempts, not retries-on-error: being overtaken means the merge had stale input, so the only
        // useful response is to read again and redo it. The bound stops a pathologically busy store
        // spinning here; in practice the second attempt wins, because it starts from what overtook us.
        for (var attempt = 0; ; attempt++)
        {
            var incoming = Read(stored.Path, out var stamp);
            var merge = ProjectMerge.Merge(stored.Baseline, Project, incoming);

            if (merge.Conflicts.Count > 0)
                return new SaveOutcome(SaveStatus.Conflicted, Array.Empty<string>(), merge.Conflicts,
                                       Array.Empty<Diagnostic>());

            var status = merge.Applied.Count == 0 ? SaveStatus.Written : SaveStatus.Merged;

            if (Write(stored.Path, status, merge.Applied, merge.Validation, stamp) is { } outcome)
                return outcome;

            if (attempt >= MaxAttempts)
                throw new InvalidOperationException(
                    $"Could not save '{stored.Path}': something wrote to it during each of "
                    + $"{MaxAttempts + 1} attempts. Nothing was written.");
        }
    }

    /// <summary>Enough to get past a burst; few enough that a livelock is reported rather than hidden.</summary>
    private const int MaxAttempts = 4;

    /// <summary>Loads, with the stamp when the store can supply one.</summary>
    private Project Read(string path, out string? stamp)
    {
        if (_repository is IStampedProjectRepository stamped) return stamped.Load(path, out stamp);
        stamp = null;
        return _repository.Load(path);
    }

    /// <summary>
    /// Writes to a given path, replacing whatever is there.
    /// </summary>
    /// <remarks>
    /// Deliberately does not merge. "Save as" is a fork: the user named a destination, and merging into
    /// whatever happens to be there would be a different operation than the one they asked for. The path
    /// becomes this copy's own from here on, so ordinary saves resume merging against it.
    /// </remarks>
    public SaveOutcome SaveAs(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Unconditional: "save as" names a destination and replaces it, so there is no read for a
        // stamp to guard.
        return Write(path, SaveStatus.Written, Array.Empty<string>(), Array.Empty<Diagnostic>(),
                     stamp: null)!;
    }

    /// <summary>
    /// Writes, and re-baselines. Null means the store refused because <paramref name="stamp"/> had
    /// moved — somebody wrote while this save was deciding what to write.
    /// </summary>
    private SaveOutcome? Write(string path, SaveStatus status, IReadOnlyList<string> merged,
                               IReadOnlyList<Diagnostic> validation, string? stamp)
    {
        if (stamp is not null && _repository is IStampedProjectRepository stamped)
        {
            if (!stamped.SaveIfUnchanged(Project, path, stamp)) return null;
        }
        else
        {
            _repository.Save(Project, path);
        }

        // Re-read rather than keeping the copy just written: the baseline has to be what storage now
        // holds, and only a read proves that is what we think it is.
        _storedAs = (path, _repository.Load(path));

        return new SaveOutcome(status, merged, Array.Empty<MergeConflict>(), validation);
    }
}
