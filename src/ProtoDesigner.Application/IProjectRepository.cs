using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application;

/// <summary>
/// Read/write port for a project. WPF, CLI, and future backends (SQL in Phase 6) depend on this,
/// not on any concrete storage. The JSON implementation lives in ProtoDesigner.Persistence.Json.
/// </summary>
public interface IProjectRepository
{
    Project Load(string path);

    void Save(Project project, string path);
}
