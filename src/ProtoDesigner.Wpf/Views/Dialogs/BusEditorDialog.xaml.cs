using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;
using ProtoDesigner.Wpf.ViewModels;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>
/// One module row while the bus is being edited. Carries the existing <see cref="Module"/> when the row
/// came from the model, so a rename keeps the identity every route already points at.
/// </summary>
public sealed class ModuleRow : ObservableObject
{
    public ModuleRow(Module? existing, string name)
    {
        Existing = existing;
        _name = name;
    }

    public Module? Existing { get; }

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
}

/// <summary>
/// Creates or edits a bus: name, transport, and the modules on it. Modules are staged locally and applied
/// on Save so Cancel abandons them; removing one takes its routes with it.
/// </summary>
public partial class BusEditorDialog : Window
{
    private readonly ProjectViewModel _project;
    private readonly BusViewModel? _existing;
    private readonly ObservableCollection<ModuleRow> _rows = new();

    private sealed record TransportOption(string Label, Transport Value, string Hint)
    {
        public override string ToString() => Label;
    }

    private static readonly TransportOption[] Transports =
    {
        new("Ethernet", Transport.Ethernet, "Frames up to 1500 bytes. Messages larger than that are flagged."),
        new("UART", Transport.Uart, "A byte stream. Keep messages small; there is no framing budget to spend."),
    };

    /// <summary>A byte-order choice, including "inherit" so a bus need not state one.</summary>
    /// <remarks>
    /// Null means the project default rather than a value, which is what keeps the whole chain
    /// field → message → bus → project meaningful. Offering only Little and Big would force every bus to
    /// answer a question most protocols answer once.
    /// </remarks>
    private sealed record EndiannessOption(string Label, Endianness? Value)
    {
        public override string ToString() => Label;
    }

    private static readonly EndiannessOption[] Endiannesses =
    [
        new("Inherit from project", null),
        new("Little-endian (least significant byte first)", Endianness.Little),
        new("Big-endian (network order, most significant byte first)", Endianness.Big),
    ];

    /// <summary>A bit-order choice, with the same "inherit" entry as byte order.</summary>
    private sealed record BitOrderOption(string Label, BitOrder? Value)
    {
        public override string ToString() => Label;
    }

    private static readonly BitOrderOption[] BitOrders =
    [
        new("Inherit from project", null),
        new("MSB-first (most significant bit first)", BitOrder.MsbFirst),
        new("LSB-first (least significant bit first)", BitOrder.LsbFirst),
    ];

    private BusEditorDialog(ProjectViewModel project, BusViewModel? existing)
    {
        InitializeComponent();
        _project = project;
        _existing = existing;

        ModulesList.ItemsSource = _rows;
        TransportBox.ItemsSource = Transports;
        EndiannessBox.ItemsSource = Endiannesses;
        BitOrderBox.ItemsSource = BitOrders;

        if (existing is null)
        {
            HeadingText.Text = "Create bus";
            NameBox.Text = "NewBus";
            TransportBox.SelectedItem = Transports[0];
            EndiannessBox.SelectedItem = Endiannesses[0];
            BitOrderBox.SelectedItem = BitOrders[0];

            // Two modules is the smallest set that can carry a route, so seeding them means the routes
            // editor is usable the moment the first message is created.
            _rows.Add(new ModuleRow(null, "Sensor"));
            _rows.Add(new ModuleRow(null, "Controller"));
        }
        else
        {
            HeadingText.Text = $"Edit '{existing.Name}'";
            NameBox.Text = existing.Name;
            TransportBox.SelectedItem = Transports.FirstOrDefault(t => t.Value == existing.Transport) ?? Transports[0];
            EndiannessBox.SelectedItem =
                Endiannesses.FirstOrDefault(e => e.Value == existing.Bus.Options.Endianness) ?? Endiannesses[0];
            BitOrderBox.SelectedItem =
                BitOrders.FirstOrDefault(b => b.Value == existing.Bus.Options.BitOrder) ?? BitOrders[0];
            foreach (var m in existing.Bus.Modules) _rows.Add(new ModuleRow(m, m.Name));
        }

        UpdateTransportHint();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static BusViewModel? CreateNew(Window? owner, ProjectViewModel project)
    {
        var d = new BusEditorDialog(project, null) { Owner = owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    public static bool Edit(Window? owner, ProjectViewModel project, BusViewModel bus)
    {
        var d = new BusEditorDialog(project, bus) { Owner = owner };
        return d.ShowDialog() == true;
    }

    public BusViewModel? Result { get; private set; }

    // ---- module list ----------------------------------------------------------------------------

    private void OnAddModule(object sender, RoutedEventArgs e)
    {
        _rows.Add(new ModuleRow(null, UniqueModuleName("Module")));
    }

    private void OnRemoveModule(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModuleRow row }) return;

        // Only an already-saved module can have routes pointing at it; a staged row cannot.
        var routes = row.Existing is null || _existing is null
            ? 0
            : _existing.Bus.Messages.Sum(m => m.Routes.Count(r => r.From == row.Existing.Id || r.To == row.Existing.Id));

        if (routes > 0 &&
            MessageBox.Show(
                $"'{row.Name}' is used by {routes} route(s). Removing it removes those routes too.",
                "ProtoDesigner", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _rows.Remove(row);
    }

    private string UniqueModuleName(string preferred)
    {
        var candidate = preferred;
        var i = 2;
        while (_rows.Any(r => string.Equals(r.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{preferred}{i++}";
        return candidate;
    }

    private void OnTransportChanged(object sender, SelectionChangedEventArgs e) => UpdateTransportHint();

    private void UpdateTransportHint()
    {
        if (TransportHint is null) return;
        TransportHint.Text = (TransportBox.SelectedItem as TransportOption)?.Hint ?? string.Empty;
    }

    // ---- save -----------------------------------------------------------------------------------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowError("Give the bus a name."); return; }

        if (_project.Buses.Any(b => !ReferenceEquals(b, _existing) &&
                                    string.Equals(b.Name, name, StringComparison.Ordinal)))
        {
            ShowError($"Another bus is already called '{name}'."); return;
        }

        var moduleNames = _rows.Select(r => r.Name?.Trim() ?? string.Empty).ToList();
        if (moduleNames.Any(string.IsNullOrWhiteSpace))
        {
            ShowError("Every module needs a name."); return;
        }

        var clash = moduleNames.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
        {
            ShowError($"Two modules are both called '{clash.Key}'."); return;
        }

        var transport = (TransportBox.SelectedItem as TransportOption)?.Value ?? Transport.Ethernet;
        var endianness = (EndiannessBox.SelectedItem as EndiannessOption)?.Value;
        var bitOrder = (BitOrderBox.SelectedItem as BitOrderOption)?.Value;

        if (_existing is null)
        {
            var bus = _project.AddBus(name!, transport);
            bus.Bus.Options.Endianness = endianness;
            bus.Bus.Options.BitOrder = bitOrder;
            foreach (var row in _rows)
                bus.AddModule(row.Name.Trim());
            Result = bus;
        }
        else
        {
            _existing.Name = name!;
            _existing.Transport = transport;
            _existing.Bus.Options.Endianness = endianness;
            _existing.Bus.Options.BitOrder = bitOrder;
            ApplyModuleEdits(_existing);
            Result = _existing;
        }

        _project.RefreshAll();
        DialogResult = true;
    }

    /// <summary>
    /// Reconciles the staged rows with the bus. Renames reuse the existing module, so identity — and every
    /// route built on it — survives. Only rows that were dropped are removed.
    /// </summary>
    private void ApplyModuleEdits(BusViewModel bus)
    {
        var kept = _rows.Where(r => r.Existing is not null).Select(r => r.Existing!.Id).ToHashSet();

        foreach (var module in bus.Bus.Modules.Where(m => !kept.Contains(m.Id)).ToList())
            bus.RemoveModule(module);

        foreach (var row in _rows)
        {
            var target = row.Name.Trim();
            if (row.Existing is null)
                bus.AddModule(target);
            else if (!string.Equals(row.Existing.Name, target, StringComparison.Ordinal))
                bus.RenameModule(row.Existing, target);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;
}
