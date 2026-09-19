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
