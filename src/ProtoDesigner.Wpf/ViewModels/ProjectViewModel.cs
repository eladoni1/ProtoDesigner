using System.Collections.ObjectModel;
using ProtoDesigner.Application;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

/// <summary>
/// The top-level VM for one open project. Owns the command journal, buses, the type library, and the
/// diagnostics view. Every mutation of the model must go through the journal — that's how undo/redo,
/// dirty tracking, and downstream refreshes stay honest.
/// </summary>
public sealed class ProjectViewModel : ObservableObject
{
    private readonly LayoutEngine _engine = new();

    public ProjectViewModel(Project project)
    {
        Project = project;
        Journal = new CommandJournal(project);
        Journal.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            RefreshAll();
        };

        // Reconcile bindings with their types up front, so a project written before a type's wire
        // settings existed opens showing what those settings imply.
        WireEncodingPropagator.ApplyAll(project);

        Types = new TypeLibraryViewModel(this);
        Buses = new ObservableCollection<BusViewModel>(project.Buses.Select(b => new BusViewModel(this, b)));
        Diagnostics = new DiagnosticsViewModel();
        LayoutList = new LayoutListViewModel();
        Diagnostics.Refresh(project);
        RefreshLayouts();
    }

    public Project Project { get; }
    public CommandJournal Journal { get; }

    public ObservableCollection<BusViewModel> Buses { get; }
    public TypeLibraryViewModel Types { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public LayoutListViewModel LayoutList { get; }

    public bool IsDirty => Journal.IsDirty;
    public bool CanUndo => Journal.CanUndo;
    public bool CanRedo => Journal.CanRedo;

    public string Name
    {
        get => Project.Name;
        set
        {
            if (Project.Name == value || string.IsNullOrWhiteSpace(value)) return;
            Journal.Do(new RenameProjectCommand(value));
            OnPropertyChanged();
        }
    }

    /// <summary>Human-readable type name for a field's referenced type.</summary>
    public string TypeName(TypeId id) =>
        Project.Types.TryGet(id, out var type) && type is not null ? type.Name : "<missing>";

    public int TypeCount => Project.Types.Count;

    /// <summary>
    /// Pushes a type's wire representation onto every field that uses it, across every message on every
    /// bus. Editing a type is meant to change all of its occurrences at once, so this runs whenever a
    /// type's wire settings are saved.
    /// </summary>
    public int PropagateWireEncoding(TypeDefinition type)
    {
        var changed = WireEncodingPropagator.Apply(Project, type);
        if (changed > 0) Journal.MarkDirty();
        return changed;
    }

    public BusViewModel AddBus(string preferredName = "NewBus", Transport transport = Transport.Ethernet)
    {
        var name = UniqueBusName(preferredName);
        var bus = new Bus(name, transport);
        Journal.Do(new AddBusCommand(bus));

        var vm = new BusViewModel(this, bus);
        Buses.Add(vm);

        // See the note in BusViewModel.AddMessage: the journal's refresh runs before the view model is
        // in the collection, so anything derived from it must be recomputed afterwards.
        RefreshAll();
        return vm;
    }

    public void RemoveBus(BusViewModel bus)
    {
        Journal.Do(new RemoveBusCommand(bus.Bus));
        Buses.Remove(bus);
        RefreshAll();
    }

    private string UniqueBusName(string preferred)
    {
        var candidate = preferred;
        var i = 2;
        while (Buses.Any(b => string.Equals(b.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{preferred}{i++}";
        return candidate;
    }

    /// <summary>
    /// The type highlighted in the Types panel. "Add field" appends an occurrence of this, so the user
    /// chooses what to add rather than always getting a u8.
    /// </summary>
    private TypeItemViewModel? _selectedType;
    public TypeItemViewModel? SelectedType
    {
        get => _selectedType;
        set
        {
            if (!SetProperty(ref _selectedType, value)) return;
            OnPropertyChanged(nameof(HasSelectedType));
            OnPropertyChanged(nameof(AddFieldHint));
        }
    }

    public bool HasSelectedType => _selectedType is not null;

    public string AddFieldHint => _selectedType is null
        ? "Pick a type in the Types tab first"
        : $"Append a {_selectedType.Name} to this message";

    /// <summary>True when the type is referenced by any message field or struct member.</summary>
    public bool IsTypeInUse(TypeId id)
    {
        foreach (var bus in Project.Buses)
            foreach (var message in bus.Messages)
                if (message.Fields.Any(f => f.TypeId == id)) return true;

        foreach (var type in Project.Types.All)
        {
            switch (type)
            {
                case StructType s when s.Fields.Any(f => f.TypeId == id): return true;
                case ArrayType a when a.ElementTypeId == id: return true;
            }
        }
        return false;
    }

    /// <summary>Recomputes layouts and validation after any mutation.</summary>
    public void RefreshAll()
    {
        RefreshLayouts();
        Diagnostics.Refresh(Project);
        foreach (var bus in Buses)
            foreach (var m in bus.Messages) m.RefreshFields();
        Types.Rebuild();
        LayoutList.Show(SelectedMessage);
        OnPropertyChanged(nameof(TypeCount));
    }

    private void RefreshLayouts()
    {
        foreach (var bus in Buses)
        {
            foreach (var m in bus.Messages)
            {
                try
                {
                    m.Layout = _engine.Compute(Project, bus.Bus, m.Message);
                    m.LayoutError = null;
                }
                catch (LayoutException ex)
                {
                    m.Layout = null;
                    m.LayoutError = ex;
                }
                catch (Exception ex)
                {
                    // Anything that is not a LayoutException is a defect rather than a modelling
                    // mistake. Surface it instead of blanking the panel, which previously made bugs
                    // look identical to an empty message.
                    m.Layout = null;
                    m.LayoutError = new LayoutException(
                        $"Unexpected {ex.GetType().Name} while computing the layout: {ex.Message}");
                }
            }
        }
    }

    // ---- selection ---------------------------------------------------------------------------

    private MessageViewModel? _selectedMessage;
    public MessageViewModel? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (!SetProperty(ref _selectedMessage, value)) return;
            OnPropertyChanged(nameof(HasSelectedMessage));

            if (value is not null) SelectedBus = value.Bus;
            LayoutList.Show(value);
        }
    }

    public bool HasSelectedMessage => _selectedMessage is not null;

    private BusViewModel? _selectedBus;
    public BusViewModel? SelectedBus
    {
        get => _selectedBus ?? Buses.FirstOrDefault();
        set => SetProperty(ref _selectedBus, value);
    }

    // ---- save state -------------------------------------------------------------------------

    private string? _currentFilePath;
    public string? CurrentFilePath
    {
        get => _currentFilePath;
        set { if (SetProperty(ref _currentFilePath, value)) OnPropertyChanged(nameof(WindowTitle)); }
    }

    public string WindowTitle
    {
        get
        {
            var name = _currentFilePath is null ? Name : System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
            var dirty = IsDirty ? " ●" : "";
            return $"{name}{dirty} — ProtoDesigner";
        }
    }

    public void MarkSaved()
    {
        Journal.MarkSaved();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(WindowTitle));
    }
}
