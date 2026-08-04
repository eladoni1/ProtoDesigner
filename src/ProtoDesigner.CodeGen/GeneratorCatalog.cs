using ProtoDesigner.CodeGen.CSharp;
using ProtoDesigner.CodeGen.Cpp;

namespace ProtoDesigner.CodeGen;

/// <summary>
/// The set of generators the product ships. One list, so the CLI's <c>targets</c> output and the editor's
/// target picker cannot disagree about what exists — adding a language is a single entry here.
/// </summary>
public static class GeneratorCatalog
{
    /// <summary>Every available target, in the order they should be offered.</summary>
    public static IReadOnlyList<IProtocolGenerator> All { get; } = new IProtocolGenerator[]
    {
        new CppGenerator(),
        new CSharpGenerator(),
    };

    /// <summary>The generator with this id, or null. Ids are matched case-insensitively.</summary>
    public static IProtocolGenerator? Find(string id) =>
        All.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
}
