using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application;

/// <summary>
/// Read/write port for a project. WPF, CLI, and future backends (SQL in Phase 6) depend on this,
/// not on any concrete storage. The JSON implementation lives in ProtoDesigner.Persistence.Json.
/// </summary>
/// <remarks>
/// <c>ProjectRepositoryContract</c> is the executable version of what follows. Run a new implementation
/// against it rather than trusting this prose.
/// </remarks>
public interface IProjectRepository
{
    /// <summary>Reads the project stored at <paramref name="path"/>.</summary>
    /// <remarks>
    /// <para>
    /// <b>Every call must return a fresh object graph.</b> Caching and handing back the same instance
    /// twice is the one mistake an implementation can make here that nothing downstream would report:
    /// <see cref="WorkingCopy"/> loads a project twice on purpose, once as the copy being edited and once
    /// as the untouched baseline its next save merges against. Share those and the baseline mutates along
    /// with the edits, so every merge compares a project against itself, finds nothing, takes nothing and
    /// reports success.
    /// </para>
    /// <para>
    /// <b>Nothing stored means throw.</b> An empty project is a valid one, so returning a blank
    /// <see cref="Project"/> loses the difference between "not there" and "there and empty" — and a
    /// merging save reads the second as an author who deleted every message.
    /// </para>
    /// </remarks>
    Project Load(string path);

    /// <summary>Writes <paramref name="project"/> to <paramref name="path"/>, replacing what is there.</summary>
    void Save(Project project, string path);
}

/// <summary>
/// A store that can refuse a write built on a read someone else has since overtaken.
/// </summary>
/// <remarks>
/// <para>
/// The merging save closes the ordinary case — every save re-reads storage first, so two people editing
/// different messages both survive whichever order they save in. What it cannot close from where it sits
/// is the window <em>inside</em> one save, between the read it merges against and the write that
/// follows. A second writer landing in that gap is lost entirely, and both saves report success.
/// </para>
/// <para>
/// A stamp closes it by making the write conditional: the store compares and writes as one indivisible
/// step, so there is no gap left to land in. Checking the stamp separately just before writing would
/// re-create the same window one level down.
/// </para>
/// <para>
/// This is separate from <see cref="IProjectRepository"/> because not every store can honour it. A
/// backend that cannot implement it atomically should not implement it at all: <see cref="WorkingCopy"/>
/// reads its absence as "this store has one writer" and carries on, which is honest, where a stamp that
/// only narrows the window would look like a guarantee.
/// </para>
/// </remarks>
public interface IStampedProjectRepository : IProjectRepository
{
    /// <summary>Loads, and reports a token for the version it loaded. Compare it; never interpret it.</summary>
    Project Load(string path, out string stamp);

    /// <summary>
    /// Writes only if what is stored is still <paramref name="expectedStamp"/>, as one atomic step.
    /// </summary>
    /// <returns>False when someone else wrote first — in which case nothing was written.</returns>
    bool SaveIfUnchanged(Project project, string path, string expectedStamp);
}
