using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation;

/// <summary>
/// Shared, cached lookups a rule may need. Passed to every rule so rules stay pure and cheap; the
/// context does the expensive work (layout computation, cross-reference indices) once.
/// </summary>
public sealed class ValidationContext
{
    private readonly Dictionary<MessageId, MessageLayout?> _layouts = new();
    private readonly Dictionary<MessageId, LayoutException?> _layoutErrors = new();
    private readonly LayoutEngine _engine = new();

    public ValidationContext(Project project)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
    }

    public Project Project { get; }

    /// <summary>
    /// Computes the layout of a message once and caches it. Returns null (and stores the exception) if the
    /// engine refuses the model — the rule that needs the layout can skip its check without crashing.
    /// </summary>
    public MessageLayout? TryLayout(Bus bus, Message message, out LayoutException? error)
    {
        if (_layouts.TryGetValue(message.Id, out var cached))
        {
            error = _layoutErrors.GetValueOrDefault(message.Id);
            return cached;
        }

        try
        {
            var layout = _engine.Compute(Project, bus, message);
            _layouts[message.Id] = layout;
            _layoutErrors[message.Id] = null;
            error = null;
            return layout;
        }
        catch (LayoutException ex)
        {
            _layouts[message.Id] = null;
            _layoutErrors[message.Id] = ex;
            error = ex;
            return null;
        }
    }

    /// <summary>The set of every type transitively referenced by any message's fields.</summary>
    private HashSet<TypeId>? _reachable;
    public HashSet<TypeId> ReachableTypes
    {
        get
        {
            if (_reachable is not null) return _reachable;

            var reachable = new HashSet<TypeId>();
            foreach (var bus in Project.Buses)
                foreach (var message in bus.Messages)
                    foreach (var field in message.Fields)
                        Walk(field.TypeId, reachable);

            return _reachable = reachable;
        }
    }

    private void Walk(TypeId id, HashSet<TypeId> sink)
    {
        if (!sink.Add(id)) return;
        if (!Project.Types.TryGet(id, out var type) || type is null) return;

        switch (type)
        {
            case StructType s:
                foreach (var f in s.Fields) Walk(f.TypeId, sink);
                break;
            case ArrayType a:
                Walk(a.ElementTypeId, sink);
                break;
        }
    }
}
