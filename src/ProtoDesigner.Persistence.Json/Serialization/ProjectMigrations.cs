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
        new DropCrcSpecs(),
    };

    /// <summary>
    /// v1 → v2: removes the per-field <c>crc</c> object.
    /// </summary>
    /// <remarks>
    /// The tool no longer models CRCs. A field that carried one stays exactly where it was and keeps its
    /// type, name and encoding — only the annotation goes, because nothing consumed it. Dropping the key
    /// explicitly rather than letting the reader ignore it means an old file is rewritten in the new shape
    /// the first time it is saved, instead of quietly carrying a dead key forever.
    /// </remarks>
    private sealed class DropCrcSpecs : IProjectMigration
    {
        public int From => 1;

        public int To => 2;

        public JsonObject Migrate(JsonObject document)
        {
            foreach (var bus in Array(document["buses"]))
                foreach (var message in Array(bus?["messages"]))
                    foreach (var field in Array(message?["fields"]))
                        (field as JsonObject)?.Remove("crc");

            // Struct types own field bindings too, and those could carry a crc just as a message's could.
            foreach (var type in Array(document["types"]))
                foreach (var field in Array(type?["fields"]))
                    (field as JsonObject)?.Remove("crc");

            return document;
        }

        /// <summary>
        /// The children of a node that may be absent, a different kind, or an ID-keyed object rather than
        /// an array. Being permissive here is deliberate: a migration that throws on an unexpected shape
        /// turns a recoverable old file into an unopenable one.
        /// </summary>
        private static IEnumerable<JsonNode?> Array(JsonNode? node) => node switch
        {
            JsonArray array => array,
            JsonObject obj => obj.Select(kv => kv.Value),
            _ => Enumerable.Empty<JsonNode?>(),
        };
    }

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
