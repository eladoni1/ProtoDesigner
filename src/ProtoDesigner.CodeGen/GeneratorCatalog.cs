using ProtoDesigner.CodeGen.C;
using ProtoDesigner.CodeGen.CSharp;

namespace ProtoDesigner.CodeGen;

/// <summary>
/// The set of generators the product ships. One list, so the CLI's <c>targets</c> output and the editor's
/// target picker cannot disagree about what exists — adding a language is a single entry here.
/// </summary>
public static class GeneratorCatalog
{
    /// <summary>Every available target, in the order they should be offered.</summary>
    /// <remarks>
    /// C replaced the C++14 target rather than joining it. The C output compiles under a C++ compiler
    /// too, so a separate C++ generator would be a second near-identical thing to keep correct for no
    /// capability the first one lacks.
    /// </remarks>
    public static IReadOnlyList<IProtocolGenerator> All { get; } = new IProtocolGenerator[]
    {
        new CGenerator(),
        new CSharpGenerator(),
    };

    /// <summary>The generator with this id, or null. Ids are matched case-insensitively.</summary>
    public static IProtocolGenerator? Find(string id) =>
        All.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
}
