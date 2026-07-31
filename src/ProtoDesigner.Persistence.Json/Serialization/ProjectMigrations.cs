using System.Text.Json.Nodes;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Persistence.Json.Serialization;

/// <summary>
/// One-file-at-a-time migration chain. Adding a version means adding a single migration that goes from
/// N to N+1; the runner applies them in sequence until it reaches <see cref="Project.CurrentSchemaVersion"/>.
/// Persisting derived data is prohibited by design, so migrations only shuffle shape and never invent semantics.
/// </summary>
public static class ProjectMigrations
{
    public interface IProjectMigration
    {
        int From { get; }
        int To { get; }
        JsonObject Migrate(JsonObject document);
    }

    private static readonly IReadOnlyList<IProjectMigration> _migrations = new IProjectMigration[]
    {
        // No migrations yet — v1 is the initial version. Add v0->v1 style entries here as the schema evolves.
    };

    public static JsonObject MigrateToCurrent(JsonObject document)
    {
        var version = document["schemaVersion"]?.GetValue<int>() ?? 0;

        while (version < Project.CurrentSchemaVersion)
        {
            var next = _migrations.FirstOrDefault(m => m.From == version);
            if (next is null)
                throw new NotSupportedException(
                    $"No migration registered to move a project from schema v{version} to v{Project.CurrentSchemaVersion}.");

            document = next.Migrate(document);
            document["schemaVersion"] = next.To;
            version = next.To;
        }

        if (version > Project.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Project was written at schema v{version}; this build only understands up to v{Project.CurrentSchemaVersion}. " +
                "Update the tool.");

        return document;
    }
}
