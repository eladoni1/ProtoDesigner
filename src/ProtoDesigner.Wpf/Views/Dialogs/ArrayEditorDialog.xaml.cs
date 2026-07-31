using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.ViewModels;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>
/// Creates or edits a fixed-length array: name, element type and count. There is no wire-size control —
/// an array's size is its element's size times the count, and the element's size is set on that type.
/// </summary>
public partial class ArrayEditorDialog : Window
{
    private readonly ProjectViewModel _project;
    private readonly ArrayType? _existing;
    private bool _loaded;

    private sealed record TypeOption(string Label, TypeDefinition Type)
    {
        public override string ToString() => Label;
    }

    private ArrayEditorDialog(ProjectViewModel project, ArrayType? existing)
    {
        InitializeComponent();
        _project = project;
        _existing = existing;

        var options = project.Project.Types.All
            .Where(t => CanBeElementOf(project, t, existing))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TypeOption($"{t.Name}  ({Describe(t)})", t))
            .ToArray();
        ElementBox.ItemsSource = options;

        if (existing is null)
        {
            HeadingText.Text = "Create array";
            NameBox.Text = "NewArray";
            CountBox.Text = "4";
            ElementBox.SelectedItem = options.FirstOrDefault();
        }
        else
        {
            HeadingText.Text = $"Edit '{existing.Name}'";
            NameBox.Text = existing.Name;
            CountBox.Text = existing.Length.Capacity.ToString(CultureInfo.InvariantCulture);
            ElementBox.SelectedItem = options.FirstOrDefault(o => o.Type.Id == existing.ElementTypeId);
        }

        _loaded = true;
        UpdateHints();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static ArrayType? CreateNew(Window? owner, ProjectViewModel project)
    {
        var d = new ArrayEditorDialog(project, null) { Owner = owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    public static bool Edit(Window? owner, ProjectViewModel project, ArrayType type)
    {
        var d = new ArrayEditorDialog(project, type) { Owner = owner };
        return d.ShowDialog() == true;
    }

    public ArrayType? Result { get; private set; }

    /// <summary>
    /// An array cannot contain itself, directly or through a chain of types — a fixed layout would be
    /// infinitely large. Candidates that would close a cycle are simply not offered.
    /// </summary>
    private static bool CanBeElementOf(ProjectViewModel project, TypeDefinition candidate, ArrayType? array)
    {
        if (array is null) return true;
        if (candidate.Id == array.Id) return false;
        return !TypeGraph.Reaches(project.Project, candidate, array.Id);
    }

    private static string Describe(TypeDefinition type) => type switch
    {
        ParameterType p => $"{p.Kind}, {p.Kind.NaturalBits() / 8} B host",
        EnumType e => $"enum, {e.Members.Count} member(s)",
        StructType s => $"struct, {s.Fields.Count} field(s)",
        ArrayType a => $"array of {a.Length.Capacity}",
        _ => "type",
    };

    private void OnElementChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) UpdateHints(); }
    private void OnCountChanged(object sender, TextChangedEventArgs e) { if (_loaded) UpdateHints(); }

    private void UpdateHints()
    {
        var element = (ElementBox.SelectedItem as TypeOption)?.Type;
        ElementHint.Text = element is null
            ? "Pick the type each slot holds."
            : $"Each slot holds one {element.Name}. Change its wire size by editing that type.";

        if (element is null || !int.TryParse(CountBox.Text?.Trim(), out var count) || count <= 0)
        {
            SizeHint.Text = "Enter how many elements the array holds.";
            return;
        }

        var elementBits = ElementWireBits(element);
        if (elementBits <= 0)
        {
            SizeHint.Text = $"{count} x {element.Name}.";
            return;
        }

        var total = elementBits * count;
        SizeHint.Text = total % 8 == 0
            ? $"{count} x {FormatBits(elementBits)} = {total / 8} bytes on the wire."
            : $"{count} x {FormatBits(elementBits)} = {total} bits on the wire.";
    }

    private static int ElementWireBits(TypeDefinition type) => type switch
    {
        ParameterType p => p.WireBits ?? p.Kind.NaturalBits(),
        EnumType e => e.WireBits ?? e.UnderlyingKind.NaturalBits(),
        _ => 0,
    };

    private static string FormatBits(int bits) => bits % 8 == 0 ? $"{bits / 8} B" : $"{bits} bit";

    private void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowError("Give the array a name."); return; }

        if (_project.Project.Types.All.Any(t => t.Id != _existing?.Id &&
                                                string.Equals(t.Name, name, StringComparison.Ordinal)))
        {
            ShowError($"Another type is already called '{name}'."); return;
        }

        if (ElementBox.SelectedItem is not TypeOption option)
        {
            ShowError("Pick an element type."); return;
        }

        if (!int.TryParse(CountBox.Text?.Trim(), out var count) || count <= 0)
        {
            ShowError("The number of elements must be a positive whole number."); return;
        }

        if (_existing is null)
        {
            Result = _project.Types.AddArray(name!, option.Type.Id, count);
        }
        else
        {
            _existing.Name = name!;
            _existing.ElementTypeId = option.Type.Id;
            _existing.Length = new ArrayLength.Fixed(count);
            Result = _existing;
        }

        _project.Types.Rebuild();
        _project.RefreshAll();
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
