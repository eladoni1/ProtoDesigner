using System.Collections.ObjectModel;
using ProtoDesigner.Application;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

public sealed class MessageViewModel : ObservableObject
{
    private readonly BusViewModel _bus;

    /// <summary>
    /// The byte order this message serialises with once the chain has been walked.
    /// </summary>
    /// <remarks>
    /// Set on the bus and nowhere else: one bus is one agreement about wire format, and two modules on it
    /// disagreeing is a broken link rather than a configuration. The resolve call still walks the whole
    /// chain, because the model allows a message or field override that the editor no longer offers —
    /// reusing it means the grid cannot drift from what actually gets generated.
    /// </remarks>
    public Endianness ResolvedEndianness =>
        Project.Project.OptionsFor(_bus.Bus, Message).Endianness;

    public MessageViewModel(BusViewModel bus, Message message)
    {
        _bus = bus;
        Message = message;
        Fields = new ObservableCollection<FieldViewModel>(message.Fields.Select(f => new FieldViewModel(this, f)));
        Routes = new ObservableCollection<RouteViewModel>(
            message.Routes.Select(r => new RouteViewModel(this, r)));
    }

    public Message Message { get; }
    public BusViewModel Bus => _bus;
    public ProjectViewModel Project => _bus.Project;

    public ObservableCollection<FieldViewModel> Fields { get; }

    /// <summary>Who sends this message to whom. Empty is legal — routing simply is not modelled yet.</summary>
    public ObservableCollection<RouteViewModel> Routes { get; }

    public string Name
    {
        get => Message.Name;
        set
        {
            if (Message.Name == value || string.IsNullOrWhiteSpace(value)) return;
            Project.Journal.Do(new RenameMessageCommand(Message, value));
            OnPropertyChanged();

            // The bus's MessageId enum is derived from these, so the type library has to be rebuilt or it
            // keeps showing the old answer. This is the cost of deriving rather than storing, and it is
            // cheaper than a stored list that can disagree with the model.
            Project.RefreshAll();
        }
    }

    /// <summary>How a receiver tells this message apart from the others on the bus.</summary>
    public int? WireId
    {
        get => Message.WireId;
        set
        {
            if (Message.WireId == value) return;
            Project.Journal.Do(new SetWireIdCommand(Message, value));
            OnPropertyChanged();
            OnPropertyChanged(nameof(WireIdClash));
            OnPropertyChanged(nameof(HasWireIdClash));
            Project.RefreshAll();
        }
    }

    /// <summary>
    /// The other message already using this id on this bus, or null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported, not blocked, and that is deliberate. Every other invalid state in this tool is allowed to
    /// exist while being named — a width too small for its range, an out-of-range default — because
    /// half-finished work is normal and an editor that refuses keystrokes fights the user mid-thought.
    /// Reverting a typed id would also be the first place the editor silently discards input.
    /// </para>
    /// <para>
    /// What was missing was not a block but proximity: the clash was only visible in the diagnostics list,
    /// away from the box being typed into. Saying it next to the field is what makes reporting enough.
    /// </para>
    /// </remarks>
    public string? WireIdClash
    {
        get
        {
            if (Message.WireId is not { } id) return null;

            var other = _bus.Bus.Messages.FirstOrDefault(m => m.Id != Message.Id && m.WireId == id);
            return other is null ? null : $"'{other.Name}' already uses id {id}";
        }
    }

    public bool HasWireIdClash => WireIdClash is not null;

    // ---- routes ----------------------------------------------------------------------------------

    /// <summary>True once the bus has at least two modules — below that there is no leg to declare.</summary>
    public bool CanAddRoute => Bus.Modules.Count > 0;

    public string RouteHint => Bus.Modules.Count switch
    {
        0 => "Add modules to the bus first — a route runs between two of them.",
        1 => "Add a second module to the bus to route between them.",
        _ => "Who sends this message to whom. One message can have several legs.",
    };

    /// <summary>Appends a leg, defaulting to the first two modules so the row arrives already meaningful.</summary>
    public RouteViewModel? AddRoute()
    {
        if (Bus.Modules.Count == 0) return null;

        var from = Bus.Modules[0];
        var to = Bus.Modules.Count > 1 ? Bus.Modules[1] : Bus.Modules[0];
        var route = new MessageRoute(from.Id, to.Id);

        Project.Journal.Do(new AddRouteCommand(Message, route));
        var vm = new RouteViewModel(this, route);
        Routes.Add(vm);
        OnPropertyChanged(nameof(RoutesLabel));
        OnPropertyChanged(nameof(HasRoutes));
        return vm;
    }

    public void RemoveRoute(RouteViewModel route)
    {
        Project.Journal.Do(new RemoveRouteCommand(Message, route.Route));
        Routes.Remove(route);
        OnPropertyChanged(nameof(RoutesLabel));
        OnPropertyChanged(nameof(HasRoutes));
    }

    /// <summary>Replaces one leg in place — what changing a dropdown on an existing row means.</summary>
    public void ReplaceRoute(RouteViewModel route, MessageRoute updated)
    {
        var index = Routes.IndexOf(route);
        if (index < 0 || route.Route == updated) return;

        Project.Journal.Do(new ChangeRouteCommand(Message, index, updated));
        route.Adopt(updated);
        OnPropertyChanged(nameof(RoutesLabel));
    }

    public bool HasRoutes => Routes.Count > 0;

    /// <summary>Compact "A→B, A→C" for the tree, so the hierarchy reads without opening the message.</summary>
    public string RoutesLabel
    {
        get
        {
            if (Message.Routes.Count == 0) return string.Empty;
            var legs = Message.Routes.Select(r =>
                $"{Bus.Bus.FindModule(r.From)?.Name ?? "?"}→{Bus.Bus.FindModule(r.To)?.Name ?? "?"}");
            return $"({string.Join(", ", legs)})";
        }
    }

    /// <summary>Rebuilds the route rows after the bus's module list changed underneath them.</summary>
    public void RefreshRoutes()
    {
        Routes.Clear();
        foreach (var r in Message.Routes) Routes.Add(new RouteViewModel(this, r));
        OnPropertyChanged(nameof(RoutesLabel));
        OnPropertyChanged(nameof(HasRoutes));
        OnPropertyChanged(nameof(CanAddRoute));
        OnPropertyChanged(nameof(RouteHint));
    }

    // ---- field editing ---------------------------------------------------------------------------

    /// <summary>
    /// Appends an occurrence of <paramref name="type"/>. Every field the editor creates is packed —
    /// packing on the wire is a product rule here, not a per-field choice.
    /// </summary>
    public FieldViewModel AddField(TypeDefinition type, int? index = null)
    {
        var binding = new FieldBinding(NextFieldName(type.Name), type.Id)
        {
            Encoding = new FieldEncoding { AllowBitPacking = true },
        };

        // Adopt the type's wire settings immediately. Without this a second field of a 4-bit enum would
        // come in at the enum's natural 4 bytes, so two occurrences of one type would disagree.
        WireEncodingPropagator.Apply(type, binding);

        var at = index ?? Fields.Count;
        Project.Journal.Do(new AddFieldCommand(Message, binding, at));

        var vm = new FieldViewModel(this, binding);
        Fields.Insert(Math.Clamp(at, 0, Fields.Count), vm);

        // The journal refreshes while applying, before this row joins the collection; recompute so the
        // layout list and the size chip account for the new field.
        Project.RefreshAll();
        return vm;
    }

    public void RemoveField(FieldViewModel field)
    {
        Project.Journal.Do(new RemoveFieldCommand(Message, field.Field));
        Fields.Remove(field);
        Project.RefreshAll();
    }

    /// <summary>
    /// Moves a field to a new position. Order in this list <em>is</em> wire order, so this is the primary
    /// editing gesture; it routes through the journal so Ctrl+Z puts it back.
    /// </summary>
    public void MoveField(FieldViewModel field, int newIndex)
    {
        var current = Fields.IndexOf(field);
        if (current < 0) return;

        newIndex = Math.Clamp(newIndex, 0, Fields.Count - 1);
        if (newIndex == current) return;

        Project.Journal.Do(new MoveFieldCommand(Message, field.Field, newIndex));
        Fields.Move(current, newIndex);
    }

    /// <summary>
    /// Whether <paramref name="field"/> may legally land at <paramref name="newIndex"/>. A dynamic array
    /// reads its length from a count field, and the engine requires that field to be laid out first — so
    /// the drop that would invert the pair is refused rather than producing an unlayoutable message.
    /// </summary>
    public bool CanMoveField(FieldViewModel field, int newIndex)
    {
        var current = Fields.IndexOf(field);
        if (current < 0) return false;
        newIndex = Math.Clamp(newIndex, 0, Fields.Count - 1);
        if (newIndex == current) return true;

        var reordered = Fields.ToList();
        reordered.RemoveAt(current);
        reordered.Insert(newIndex, field);

        var types = Project.Project.Types;
        for (var i = 0; i < reordered.Count; i++)
        {
            if (!types.TryGet(reordered[i].Field.TypeId, out var t) || t is not ArrayType array) continue;
            if (array.Length is not ArrayLength.CountFromField cff) continue;

            var countAt = reordered.FindIndex(f => f.Field.Id == cff.CountFieldId);
            if (countAt >= 0 && countAt > i) return false;   // count would follow its array
        }
        return true;
    }

    private string NextFieldName(string preferred)
    {
        var baseName = string.IsNullOrWhiteSpace(preferred)
            ? "field"
            : char.ToLowerInvariant(preferred[0]) + preferred[1..];
        var candidate = baseName;
        var i = 2;
        while (Fields.Any(f => string.Equals(f.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{baseName}{i++}";
        return candidate;
    }

    // ---- computed layout --------------------------------------------------------------------------

    private MessageLayout? _layout;
    private LayoutException? _layoutError;

    public MessageLayout? Layout
    {
        get => _layout;
        set
        {
            _layout = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeLabel));
            OnPropertyChanged(nameof(IsFixedSize));
            OnPropertyChanged(nameof(HasLayout));
        }
    }

    public LayoutException? LayoutError
    {
        get => _layoutError;
        set { _layoutError = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeLabel)); }
    }

    public bool HasLayout => _layout is not null;

    /// <summary>Message size, in bytes — bits are a field-level concern, not a message-level one.</summary>
    public string SizeLabel
    {
        get
        {
            if (_layoutError is not null) return "layout error";
            if (_layout is null) return "—";
            return _layout.IsFixedSize
                ? $"{_layout.MinBytes} B"
                : $"{_layout.MinBytes}–{_layout.MaxBytes} B";
        }
    }

    public bool IsFixedSize => _layout?.IsFixedSize ?? true;

    public void RefreshFields()
    {
        foreach (var f in Fields) f.RefreshComputed();
    }
}
