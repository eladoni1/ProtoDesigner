using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ProtoDesigner.Application;
using ProtoDesigner.Application.Commands;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Behaviors;
using ProtoDesigner.Wpf.Mvvm;
using ProtoDesigner.Wpf.ViewModels;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>One member row while the struct is being edited. Committed to the model only on Save.</summary>
public sealed class StructFieldRow : ObservableObject
{
    public StructFieldRow(FieldBinding binding, string typeName, string sizeLabel)
    {
        Binding = binding;
        TypeName = typeName;
        SizeLabel = sizeLabel;
        _name = binding.Name;
    }

    public FieldBinding Binding { get; }
    public string TypeName { get; }
    public string SizeLabel { get; }

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
}

/// <summary>
/// Creates or edits a struct: name plus its ordered fields. Edits are staged in a local list and applied
/// on Save, so Cancel genuinely abandons them.
/// </summary>
public partial class StructEditorDialog : Window
{
    private readonly ProjectViewModel _project;
    private readonly StructType? _existing;
    private readonly ObservableCollection<StructFieldRow> _rows = new();

    private sealed record TypeOption(string Label, TypeDefinition Type)
    {
        public override string ToString() => Label;
    }

    private StructEditorDialog(ProjectViewModel project, StructType? existing)
    {
        InitializeComponent();
        _project = project;
        _existing = existing;

        FieldsList.ItemsSource = _rows;
        RefreshCandidates();

        if (existing is null)
        {
            HeadingText.Text = "Create struct";
            NameBox.Text = "NewStruct";
        }
        else
        {
            HeadingText.Text = $"Edit '{existing.Name}'";
            NameBox.Text = existing.Name;
            foreach (var f in existing.Fields) _rows.Add(MakeRow(f));
        }

        UpdateTotal();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static StructType? CreateNew(Window? owner, ProjectViewModel project)
    {
        var d = new StructEditorDialog(project, null) { Owner = owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    public static bool Edit(Window? owner, ProjectViewModel project, StructType type)
    {
        var d = new StructEditorDialog(project, type) { Owner = owner };
        return d.ShowDialog() == true;
    }

    public StructType? Result { get; private set; }

    /// <summary>
    /// Offers every type that cannot lead back to this struct. A struct containing itself, directly or
    /// through a chain, has no finite layout, so those options are withheld rather than rejected later.
    /// </summary>
    private void RefreshCandidates()
    {
        var options = TypeGraph.CandidatesFor(_project.Project, _existing)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TypeOption(t.Name, t))
            .ToArray();

        AddTypeBox.ItemsSource = options;
        AddTypeBox.SelectedItem ??= options.FirstOrDefault();
    }

    private StructFieldRow MakeRow(FieldBinding binding)
    {
        var name = _project.TypeName(binding.TypeId);
        var size = "-";
        if (_project.Project.Types.TryGet(binding.TypeId, out var type) && type is not null)
        {
            var bits = type switch
            {
                ParameterType p => p.WireBits ?? p.Kind.NaturalBits(),
                EnumType e => e.WireBits ?? e.UnderlyingKind.NaturalBits(),
                _ => 0,
            };
            if (bits > 0) size = bits % 8 == 0 ? $"{bits / 8} B" : $"{bits} bit";
            else if (type is StructType s) size = $"{s.Fields.Count} fld";
            else if (type is ArrayType a) size = $"x{a.Length.Capacity}";
        }
        return new StructFieldRow(binding, name, size);
    }

    private void OnAddField(object sender, RoutedEventArgs e)
    {
        if (AddTypeBox.SelectedItem is not TypeOption option)
        {
            ShowError("Pick a type to add.");
            return;
        }

        HideError();
        var baseName = char.ToLowerInvariant(option.Type.Name[0]) + option.Type.Name[1..];
        var candidate = baseName;
        var i = 2;
        while (_rows.Any(r => string.Equals(r.Name, candidate, StringComparison.Ordinal)))
            candidate = $"{baseName}{i++}";

        var binding = new FieldBinding(candidate, option.Type.Id)
        {
            Encoding = new FieldEncoding { AllowBitPacking = true },
        };
        WireEncodingPropagator.Apply(option.Type, binding);

        _rows.Add(MakeRow(binding));
        UpdateTotal();
    }

    private void OnRemoveField(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: StructFieldRow row }) _rows.Remove(row);
        UpdateTotal();
    }

    private void OnReorderRequested(object? sender, ReorderRequestedEventArgs e)
    {
        if (e.Item is not StructFieldRow row) return;
        if (e.IsProbe) return;                     // any order is valid inside a struct

        var from = _rows.IndexOf(row);
        if (from < 0) return;
        var to = Math.Clamp(e.NewIndex, 0, _rows.Count - 1);
        if (from != to) _rows.Move(from, to);
    }

    private void UpdateTotal()
    {
        TotalText.Text = _rows.Count == 0
            ? "No fields yet."
            : $"{_rows.Count} field(s), in wire order.";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowError("Give the struct a name."); return; }

        if (_project.Project.Types.All.Any(t => t.Id != _existing?.Id &&
                                                string.Equals(t.Name, name, StringComparison.Ordinal)))
        {
            ShowError($"Another type is already called '{name}'."); return;
        }

        foreach (var row in _rows)
        {
            if (string.IsNullOrWhiteSpace(row.Name)) { ShowError("Every field needs a name."); return; }
        }

        var duplicate = _rows.GroupBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) { ShowError($"Two fields are both called '{duplicate.Key}'."); return; }

        var target = _existing ?? _project.Types.AddStruct(name!);
        var edit = new StructEdit(name!, _rows.Select(r => new StructField(r.Binding, r.Name.Trim())).ToList());

        // Creating already pushed one command, and undoing it removes the whole type — so the initial
        // contents ride along with it rather than becoming a second step the user has to undo twice.
        if (_existing is null) edit.ApplyTo(target);
        else _project.Journal.Do(new EditStructCommand(target, edit));

        _project.Types.Rebuild();
        _project.RefreshAll();

        Result = target;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;
}
