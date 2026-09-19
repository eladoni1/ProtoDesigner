using Microsoft.Win32;
using ProtoDesigner.Application;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Persistence.Json;
using ProtoDesigner.Wpf.Mvvm;
using System.Windows;

namespace ProtoDesigner.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IProjectRepository _repository;

    /// <summary>The open project, its file, and the baseline its next save merges against.</summary>
    private WorkingCopy _workingCopy = null!;   // set by Show() in the constructor

    public MainViewModel()
    {
        _repository = new JsonProjectRepository();
        Show(WorkingCopy.Started(_repository, BuildSampleProject()));

        // The sample declares only the handful of types its own messages need, so bool, char and the
        // wider integers were absent from the Types tab on launch and looked as though the tool did not
        // support them. Seeding fills in the rest; entries the sample already declared are skipped.
        Project.Types.SeedBuiltIns();
        Project.MarkSaved();
        Project.SelectedMessage = Project.Buses.FirstOrDefault()?.Messages.FirstOrDefault();

        NewCommand = new RelayCommand(p => NewProject(p));
        OpenCommand = new RelayCommand(p => OpenProject(p));
        SaveCommand = new RelayCommand(_ => Save(), _ => true);
        SaveAsCommand = new RelayCommand(p => SaveAs(p));
        UndoCommand = new RelayCommand(() => Project.Journal.Undo(), () => Project.CanUndo);
        RedoCommand = new RelayCommand(() => Project.Journal.Redo(), () => Project.CanRedo);
        ExitCommand = new RelayCommand(() => System.Windows.Application.Current.Shutdown());
    }

    private ProjectViewModel _project = null!;
    public ProjectViewModel Project
    {
        get => _project;
        private set
        {
            if (SetProperty(ref _project, value))
            {
                _project.PropertyChanged += (_, _) => OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string WindowTitle => Project.WindowTitle;

    /// <summary>
    /// The one place the open project is swapped, and the only place these two are assigned.
    /// </summary>
    /// <remarks>
    /// The working copy already owns the project and its path, so anything that sets the view model
    /// separately is how the two come to disagree — and a save that writes the wrong project reports
    /// success. Keeping it to one line makes that impossible rather than merely unlikely.
    /// </remarks>
    private void Show(WorkingCopy copy)
    {
        _workingCopy = copy;
        Project = new ProjectViewModel(copy.Project) { CurrentFilePath = copy.Path };
        Project.MarkSaved();
    }

    public RelayCommand NewCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand ExitCommand { get; }

    private void NewProject(object? _)
    {
        if (!ConfirmDiscardIfDirty()) return;

        Show(WorkingCopy.Started(_repository, new Core.Model.Project("Untitled")));
        Project.Types.SeedBuiltIns();

        // Seeding routes through the journal, which marks the project dirty. A brand-new project holding
        // nothing but the built-ins has no work in it worth warning about on close.
        Project.MarkSaved();
    }

    private void OpenProject(object? _)
    {
        if (!ConfirmDiscardIfDirty()) return;

        var dialog = new OpenFileDialog
        {
            Filter = "ProtoDesigner project (*.pdproj)|*.pdproj|All files (*.*)|*.*",
            Title = "Open project",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            Show(WorkingCopy.Open(_repository, dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n\n{ex.Message}", "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Saves, merging in anything another author stored since this copy was opened.
    /// </summary>
    /// <remarks>
    /// The policy lives in <see cref="WorkingCopy"/> and is tested there; this is the part that has to be
    /// here — telling the user, and re-rendering when the model changed underneath them.
    /// </remarks>
    private bool Save()
    {
        if (_workingCopy.Path is null) return SaveAs(null);
        try
        {
            var outcome = _workingCopy.Save();

            if (outcome.Status == SaveStatus.Conflicted)
            {
                ReportConflicts(outcome);
                return false;
            }

            if (outcome.Status == SaveStatus.Merged) AdoptMergedProject(outcome);
            else Project.MarkSaved();   // Show() already marks saved for the merged case

            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n\n{ex.Message}", "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private static void ReportConflicts(SaveOutcome outcome)
    {
        var what = string.Join("\n", outcome.Conflicts.Select(c => $"  • {c.Description} [{c.Target}]"));
        MessageBox.Show(
            "Someone else has saved changes that cannot be combined with yours automatically.\n\n"
            + what
            + "\n\nNothing was written and nothing on screen has changed. Adjust what is listed above, "
            + "then save again.",
            "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Re-renders after someone else's changes were merged into the open project.
    /// </summary>
    /// <remarks>
    /// The merge edits the model in place and the view models were built around what it used to be, so the
    /// tree is rebuilt rather than patched. That discards the undo history, which is the honest outcome
    /// rather than a shortcut: the journal describes operations against a project that has since changed
    /// underneath them, so replaying one backwards is not defined. The user is told, because losing undo
    /// silently is worse than losing it.
    /// </remarks>
    private void AdoptMergedProject(SaveOutcome outcome)
    {
        Show(_workingCopy);

        var what = string.Join("\n", outcome.Merged.Select(m => $"  • {m}"));
        var invalid = outcome.Validation.Count == 0
            ? ""
            : "\n\nThe combined project has problems that need fixing:\n"
              + string.Join("\n", outcome.Validation.Select(d => $"  • {d.Code}: {d.Message}"));

        MessageBox.Show(
            "Changes from another author were merged into yours and everything was saved.\n\n"
            + what
            + "\n\nUndo history has been reset, because the project changed underneath it."
            + invalid,
            "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private bool SaveAs(object? _)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "ProtoDesigner project (*.pdproj)|*.pdproj",
            Title = "Save project as",
            FileName = Project.Project.Name + ".pdproj",
        };
        if (dialog.ShowDialog() != true) return false;

        try
        {
            _workingCopy.SaveAs(dialog.FileName);
            Project.CurrentFilePath = _workingCopy.Path;
            Project.MarkSaved();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n\n{ex.Message}", "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private bool ConfirmDiscardIfDirty()
    {
        if (!Project.IsDirty) return true;
        var result = MessageBox.Show(
            "You have unsaved changes. Save them first?",
            "ProtoDesigner",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ---- sample project seed ------------------------------------------------------------------

    private static Core.Model.Project BuildSampleProject()
    {
        var project = new Core.Model.Project("SampleProject");

        // Plain primitives carry their kind's full span, so a field using one reports real limits rather
        // than "unbounded" — the limits are what make a narrower wire size possible.
        var u8  = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8, PrimitiveKind.U8.NaturalRange()));
        var u16 = project.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16, PrimitiveKind.U16.NaturalRange()));
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32, PrimitiveKind.U32.NaturalRange()));

        // Wire size lives on the type: 1000..1015 is 16 distinct values, so 4 bits carries it exactly.
        var temperature = project.Types.Add(
            new ParameterType(TypeId.New(), "Temperature", PrimitiveKind.U16, new NumericRange(1000, 1015))
            {
                WireBits = 4,
            });

        // Four members, highest value 10, so 4 bits is enough on the wire despite the 32-bit host type.
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
        {
            WireBits = 4,
        }
            .With("Idle", 0)
            .With("Arming", 1)
            .With("Running", 5)
            .With("Fault", 10));

        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(
                new FieldBinding("messageId", u8.Id),
                new FieldBinding("flags", u8.Id),
                new FieldBinding("timestamp", u32.Id)));

        var samples = project.Types.Add(new ArrayType(TypeId.New(), "Samples", u16.Id, new ArrayLength.Fixed(4)));

        var bus = new Bus("Main", Transport.Ethernet);
        var sensor = bus.AddModule("Sensor");
        var controller = bus.AddModule("Controller");
        var logger = bus.AddModule("Logger");

        var telemetry = new Message("Telemetry") { WireId = 42 };
        telemetry.Routes.Add(new MessageRoute(sensor.Id, controller.Id));
        telemetry.Routes.Add(new MessageRoute(sensor.Id, logger.Id));
        telemetry.Fields.Add(new FieldBinding("header", header.Id));
        telemetry.Fields.Add(new FieldBinding("mode", mode.Id, FieldEncoding.Packed(4)));
        telemetry.Fields.Add(new FieldBinding("temperature", temperature.Id,
            new FieldEncoding { BitWidth = 4, AllowBitPacking = true, Transform = new ScalarTransform(1000, 1) }));
        telemetry.Fields.Add(new FieldBinding("samples", samples.Id));

        // An ordinary u16 the sender fills in. The tool does not compute checksums — the generated
        // per-type wire sizes are what locate one in a frame.
        telemetry.Fields.Add(new FieldBinding("checksum", u16.Id));

        var ping = new Message("Ping") { WireId = 1 };
        ping.Routes.Add(new MessageRoute(controller.Id, sensor.Id));
        ping.Fields.Add(new FieldBinding("counter", u32.Id));

        bus.Messages.Add(telemetry);
        bus.Messages.Add(ping);

        project.Buses.Add(bus);
        return project;
    }
}
