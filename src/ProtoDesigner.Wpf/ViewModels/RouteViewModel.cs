using System.Collections.ObjectModel;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

/// <summary>
/// One <c>sender → receiver</c> row on a message.
/// </summary>
/// <remarks>
/// A <see cref="MessageRoute"/> is a record — changing an endpoint produces a new value rather than
/// mutating the old one. So this view model holds the current value and swaps it wholesale, which is also
/// exactly what the undo command records.
/// </remarks>
public sealed class RouteViewModel : ObservableObject
{
    private readonly MessageViewModel _message;
    private MessageRoute _route;

    public RouteViewModel(MessageViewModel message, MessageRoute route)
    {
        _message = message;
        _route = route;
    }

    public MessageRoute Route => _route;

    /// <summary>The modules available in both dropdowns — every module on the owning bus.</summary>
    public ObservableCollection<Module> Modules => _message.Bus.Modules;

    public Module? From
    {
        get => _message.Bus.Bus.FindModule(_route.From);
        set
        {
            if (value is null || value.Id == _route.From) return;
            _message.ReplaceRoute(this, _route with { From = value.Id });
        }
    }

    public Module? To
    {
        get => _message.Bus.Bus.FindModule(_route.To);
        set
        {
            if (value is null || value.Id == _route.To) return;
            _message.ReplaceRoute(this, _route with { To = value.Id });
        }
    }

    /// <summary>Takes on a new value after the message swapped it in the model.</summary>
    public void Adopt(MessageRoute route)
    {
        _route = route;
        OnPropertyChanged(nameof(From));
        OnPropertyChanged(nameof(To));
        OnPropertyChanged(nameof(Label));
    }

    public string Label => $"{From?.Name ?? "?"} → {To?.Name ?? "?"}";
}
