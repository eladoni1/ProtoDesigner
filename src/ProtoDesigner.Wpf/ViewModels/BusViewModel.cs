using System.Collections.ObjectModel;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

public sealed class BusViewModel : ObservableObject
{
    public BusViewModel(ProjectViewModel project, Bus bus)
    {
        Project = project;
        Bus = bus;
        Messages = new ObservableCollection<MessageViewModel>(bus.Messages.Select(m => new MessageViewModel(this, m)));
        Modules = new ObservableCollection<Module>(bus.Modules);
    }

    public ProjectViewModel Project { get; }
    public Bus Bus { get; }

    public ObservableCollection<MessageViewModel> Messages { get; }

    /// <summary>The modules on this bus. Route dropdowns bind to this, so it must track the model.</summary>
    public ObservableCollection<Module> Modules { get; }

    public string Name
    {
        get => Bus.Name;
        set
        {
            if (Bus.Name == value || string.IsNullOrWhiteSpace(value)) return;
            Project.Journal.Do(new RenameBusCommand(Bus, value));
            OnPropertyChanged();
        }
    }

    public Transport Transport
    {
        get => Bus.Transport;
        set
        {
            if (Bus.Transport == value) return;
            Bus.Transport = value;   // simple property; not journalled (rare edit, low risk).
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Adds a message with the next free wire ID. Auto-assigning beats leaving it blank: the ID is how a
    /// receiver tells messages apart, and an arbitrary number the user has to invent reads as noise.
    /// </summary>
    public MessageViewModel AddMessage(string? preferredName = null)
    {
        var message = new Message(NextMessageName(preferredName)) { WireId = NextWireId() };
        Project.Journal.Do(new AddMessageCommand(Bus, message));

        var vm = new MessageViewModel(this, message);
        Messages.Add(vm);
        OnPropertyChanged(nameof(SubtitleLabel));

        // The journal fires its refresh while applying the command, which is before this view model
        // joins the collection — so it would be skipped and left without a computed layout. Refresh
        // again now that it is visible.
        Project.RefreshAll();
        return vm;
    }

    public void RemoveMessage(MessageViewModel message)
    {
        Project.Journal.Do(new RemoveMessageCommand(Bus, message.Message));
        Messages.Remove(message);
        OnPropertyChanged(nameof(SubtitleLabel));
        Project.RefreshAll();
    }

    // ---- modules ---------------------------------------------------------------------------------

    public Module AddModule(string name)
    {
        var module = new Module(name);
        Project.Journal.Do(new AddModuleCommand(Bus, module));
        Modules.Add(module);
        OnPropertyChanged(nameof(ModuleSummary));
        return module;
    }

    /// <summary>Removes a module and, with it, every route that referenced it.</summary>
    public void RemoveModule(Module module)
    {
        Project.Journal.Do(new RemoveModuleCommand(Bus, module));
        Modules.Remove(module);
        OnPropertyChanged(nameof(ModuleSummary));
        foreach (var m in Messages) m.RefreshRoutes();
    }

    /// <summary>
    /// Renames in place. Routes reference the module by ID, so every one of them keeps pointing at it and
    /// simply reads differently — the whole reason modules carry identity.
    /// </summary>
    public void RenameModule(Module module, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || module.Name == name) return;
        Project.Journal.Do(new RenameModuleCommand(module, name));
        OnPropertyChanged(nameof(ModuleSummary));
        foreach (var m in Messages) m.RefreshRoutes();
    }

    public string ModuleSummary => Modules.Count == 0
        ? "no modules"
        : string.Join(", ", Modules.Select(m => m.Name));

    /// <summary>Lowest non-negative integer not already used on this bus.</summary>
    private int NextWireId()
    {
        var used = Messages.Select(m => m.WireId).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        var candidate = 0;
        while (used.Contains(candidate)) candidate++;
        return candidate;
    }

    private string NextMessageName(string? preferred)
    {
        var baseName = string.IsNullOrWhiteSpace(preferred) ? "NewMessage" : preferred!.Trim();
        var candidate = baseName;
        var i = 2;
        while (Messages.Any(m => string.Equals(m.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{baseName}{i++}";
        return candidate;
    }

    /// <summary>Shown next to the bus in the tree so the hierarchy reads at a glance.</summary>
    public string SubtitleLabel =>
        $"· {Transport} · {Modules.Count} module(s) · {Messages.Count} message(s)";
}
