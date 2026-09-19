using ProtoDesigner.Application;
using ProtoDesigner.Persistence.Contract;

namespace ProtoDesigner.Persistence.Json.Tests;

/// <summary>The JSON file implementation against the shared repository contract.</summary>
public sealed class JsonProjectRepositoryContractTests : ProjectRepositoryContract, IDisposable
{
    private readonly List<string> _paths = new();

    protected override (IProjectRepository Repository, string Path) NewStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"protodesigner-contract-{Guid.NewGuid():N}.pdproj");
        _paths.Add(path);
        return (new JsonProjectRepository(), path);
    }

    public void Dispose()
    {
        foreach (var path in _paths.Where(File.Exists)) File.Delete(path);
    }
}
