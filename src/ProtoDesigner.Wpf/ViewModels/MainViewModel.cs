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

    public MainViewModel()
    {
        _repository = new JsonProjectRepository();
        Project = BuildSampleProject();

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
        var project = new Core.Model.Project("Untitled");
        Project = new ProjectViewModel(project);
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
            var loaded = _repository.Load(dialog.FileName);
            Project = new ProjectViewModel(loaded) { CurrentFilePath = dialog.FileName };
            Project.MarkSaved();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n\n{ex.Message}", "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool Save()
    {
        if (Project.CurrentFilePath is null) return SaveAs(null);
        try
        {
            _repository.Save(Project.Project, Project.CurrentFilePath);
            Project.MarkSaved();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n\n{ex.Message}", "ProtoDesigner", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
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
            _repository.Save(Project.Project, dialog.FileName);
            Project.CurrentFilePath = dialog.FileName;
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

    private static ProjectViewModel BuildSampleProject()
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

        var vm = new ProjectViewModel(project);

        // The sample declares only the handful of types its own messages need, so bool, char and the
        // wider integers were absent from the Types tab on launch and looked as though the tool did not
        // support them. Seeding fills in the rest; entries the sample already declared are skipped.
        vm.Types.SeedBuiltIns();

        vm.MarkSaved();
        vm.SelectedMessage = vm.Buses.FirstOrDefault()?.Messages.FirstOrDefault();
        return vm;
    }
}
