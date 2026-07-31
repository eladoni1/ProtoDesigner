using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProtoDesigner.Application;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Persistence.Json.Serialization;

namespace ProtoDesigner.Persistence.Json;

/// <summary>
/// Reads and writes projects as canonical, ID-keyed JSON. The written form is deterministic — stable
/// key order, LF line endings, 2-space indent, invariant number formatting — so merges only conflict
/// where two authors touched the same entity.
/// </summary>
public sealed class JsonProjectRepository : IProjectRepository
{
    public Project Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var text = File.ReadAllText(path, Encoding.UTF8);
        return LoadFromString(text);
    }

    public void Save(Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, SaveToString(project), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>String round-trip. Exposed for tests and for in-memory copies.</summary>
    public static Project LoadFromString(string text)
    {
        var node = JsonNode.Parse(text) ?? throw new FormatException("Empty JSON document.");
        node = ProjectMigrations.MigrateToCurrent(node.AsObject());
        return ProjectSerializer.FromJson(node.AsObject());
    }

    public static string SaveToString(Project project)
    {
        var obj = ProjectSerializer.ToJson(project);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var raw = obj.ToJsonString(options);
        return raw.Replace("\r\n", "\n");
    }
}
