using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Wpf.ViewModels;

/// <summary>
/// Reachability over the type reference graph, used by the editors to refuse a choice that would make a
/// type contain itself.
/// </summary>
/// <remarks>
/// The layout engine already rejects recursion and the validator reports it, but by then the user has
/// already committed to a broken model. Asking this question up front lets the dialogs simply not offer
/// the option, which is a better experience than an error after the fact.
/// </remarks>
internal static class TypeGraph
{
    /// <summary>True if <paramref name="from"/> references <paramref name="target"/>, directly or transitively.</summary>
    public static bool Reaches(Project project, TypeDefinition from, TypeId target)
    {
        var seen = new HashSet<TypeId>();
        return Walk(project, from.Id, target, seen);
    }

    private static bool Walk(Project project, TypeId current, TypeId target, HashSet<TypeId> seen)
    {
        if (current == target) return true;
        if (!seen.Add(current)) return false;               // already explored, or a pre-existing cycle
        if (!project.Types.TryGet(current, out var type) || type is null) return false;

        return type switch
        {
            StructType s => s.Fields.Any(f => Walk(project, f.TypeId, target, seen)),
            ArrayType a => Walk(project, a.ElementTypeId, target, seen),
            _ => false,
        };
    }

    /// <summary>
    /// The types that may safely be placed inside <paramref name="container"/>. Excludes the container
    /// itself and anything that already leads back to it.
    /// </summary>
    public static IEnumerable<TypeDefinition> CandidatesFor(Project project, TypeDefinition? container) =>
        project.Types.All.Where(t =>
            container is null || (t.Id != container.Id && !Reaches(project, t, container.Id)));
}
