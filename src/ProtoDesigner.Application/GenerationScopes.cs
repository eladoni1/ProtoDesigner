using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application;

/// <summary>One bus and the messages on it that a generation run should cover.</summary>
public sealed record GenerationScope(Bus Bus, IReadOnlyList<Message> Messages)
{
    public IReadOnlySet<MessageId> MessageIds => Messages.Select(m => m.Id).ToHashSet();

    public override string ToString() => $"{Bus.Name} ({Messages.Count} message(s))";
}

/// <summary>
/// Works out which messages a generation run covers.
/// </summary>
/// <remarks>
/// This is a use case, not a model rule, which is why it lives here rather than in Core: nothing about a
/// project is wrong if a module has no routes, it just means there is nothing to generate for it. Core
/// stays a neutral description of the protocol and this layer decides what to do with it.
/// </remarks>
public static class GenerationScopes
{
    /// <summary>Every message on every bus — the "generate the lot" case.</summary>
    public static IReadOnlyList<GenerationScope> ForProject(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Buses
            .Where(b => b.Messages.Count > 0)
            .Select(b => new GenerationScope(b, b.Messages.ToList()))
            .ToList();
    }

    /// <summary>Every message on one bus.</summary>
    public static IReadOnlyList<GenerationScope> ForBus(Bus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        return bus.Messages.Count == 0
            ? Array.Empty<GenerationScope>()
            : new[] { new GenerationScope(bus, bus.Messages.ToList()) };
    }

    /// <summary>
    /// A chosen subset of one bus's messages, in the bus's own order.
    /// </summary>
    /// <remarks>
    /// Order comes from the bus rather than from the caller's list: message order is not wire-significant,
    /// but keeping it stable means generating the same selection twice produces the same file, which is
    /// what makes the output diffable.
    /// </remarks>
    public static IReadOnlyList<GenerationScope> ForMessages(Bus bus, IEnumerable<MessageId> messageIds)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(messageIds);

        var wanted = messageIds.ToHashSet();
        var selected = bus.Messages.Where(m => wanted.Contains(m.Id)).ToList();

        return selected.Count == 0
            ? Array.Empty<GenerationScope>()
            : new[] { new GenerationScope(bus, selected) };
    }

    /// <summary>
    /// The messages one module instance sends or receives, on the single bus that owns it.
    /// </summary>
    /// <remarks>
    /// Both directions are included deliberately. A module that only receives a message still gets its
    /// encoder — loopback and unit tests need it, and discovering a function is missing at the point you
    /// reach for it is worse than the handful of bytes it costs.
    /// </remarks>
    public static IReadOnlyList<GenerationScope> ForModule(Project project, ModuleId moduleId)
    {
        ArgumentNullException.ThrowIfNull(project);

        var scopes = new List<GenerationScope>();

        foreach (var bus in project.Buses)
        {
            if (bus.Modules.All(m => m.Id != moduleId)) continue;

            var messages = bus.Messages
                .Where(m => m.Routes.Any(r => r.From == moduleId || r.To == moduleId))
                .ToList();

            if (messages.Count > 0) scopes.Add(new GenerationScope(bus, messages));
        }

        return scopes;
    }

    /// <summary>
    /// Everything a module touches across the whole project: on every bus carrying a module of that name,
    /// the messages it sends or receives. This is the "give me everything for module X" case.
    /// </summary>
    /// <remarks>
    /// Matching is by NAME rather than by <see cref="ModuleId"/> because modules currently belong to a
    /// bus: a real ECU sitting on two buses is modelled as two <see cref="Module"/> objects with different
    /// ids that happen to share a name. Name is therefore the only thing that identifies "the same box" —
    /// which does mean renaming a module on one bus quietly detaches it from its twin on another. The
    /// clean fix is a project-level module registry that buses attach to; until then this is the honest
    /// approximation, and it is what makes one build cover all of a module's buses at once.
    /// </remarks>
    public static IReadOnlyList<GenerationScope> ForModuleNamed(Project project, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(moduleName);

        var scopes = new List<GenerationScope>();

        foreach (var bus in project.Buses)
        {
            var ids = bus.Modules
                .Where(m => string.Equals(m.Name, moduleName, StringComparison.Ordinal))
                .Select(m => m.Id)
                .ToHashSet();
            if (ids.Count == 0) continue;

            var messages = bus.Messages
                .Where(m => m.Routes.Any(r => ids.Contains(r.From) || ids.Contains(r.To)))
                .ToList();

            if (messages.Count > 0) scopes.Add(new GenerationScope(bus, messages));
        }

        return scopes;
    }

    /// <summary>Every distinct module name in the project, for populating a picker.</summary>
    public static IReadOnlyList<string> ModuleNames(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Buses
            .SelectMany(b => b.Modules.Select(m => m.Name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
