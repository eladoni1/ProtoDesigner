using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.ViewModels;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>
/// Creates or edits an array: name, element type, and how many elements it holds. There is no wire-size
/// control — an array's size is its element's size times the count, and the element's size is set on that
/// type.
/// </summary>
/// <remarks>
/// The length offers two shapes, not five. <see cref="ArrayLength.CountFromField"/> needs a reference to a
/// field in a specific message, but a type is project-wide and may be used by several — there is no one
/// field to point at from here, so that rule stays authored through the project file. It is nonetheless
/// <em>preserved</em>: editing such an array here changes its bounds and leaves the rule alone, because
/// silently rewriting it to something self-describing would add a length to the wire that no peer expects.
/// </remarks>
public partial class ArrayEditorDialog : Window
{
    private readonly ProjectViewModel _project;
    private readonly ArrayType? _existing;
    private bool _loaded;

    private sealed record TypeOption(string Label, TypeDefinition Type)
    {
        public override string ToString() => Label;
    }

    private sealed record LengthOption(string Label, bool IsDynamic)
    {
        public override string ToString() => Label;
    }

    private static readonly LengthOption[] LengthOptions =
    [
        new("Exactly — always this many", IsDynamic: false),
        new("Variable — between a minimum and a maximum", IsDynamic: true),
    ];

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
        LengthBox.ItemsSource = LengthOptions;

        if (existing is null)
        {
            HeadingText.Text = "Create array";
            NameBox.Text = "NewArray";
            CountBox.Text = "4";
            MinBox.Text = "0";
            LengthBox.SelectedItem = LengthOptions[0];
            ElementBox.SelectedItem = options.FirstOrDefault();
        }
        else
        {
            HeadingText.Text = $"Edit '{existing.Name}'";
            NameBox.Text = existing.Name;
            CountBox.Text = existing.Length.Capacity.ToString(CultureInfo.InvariantCulture);
            MinBox.Text = existing.Length.MinimumCount.ToString(CultureInfo.InvariantCulture);
            LengthBox.SelectedItem = LengthOptions[existing.Length.IsDynamic ? 1 : 0];
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
    private void OnLengthKindChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) UpdateHints(); }

    private bool IsDynamic => (LengthBox.SelectedItem as LengthOption)?.IsDynamic == true;

    private void UpdateHints()
    {
        var element = (ElementBox.SelectedItem as TypeOption)?.Type;
        ElementHint.Text = element is null
            ? "Pick the type each slot holds."
            : $"Each slot holds one {element.Name}. Change its wire size by editing that type.";

        // A fixed array's minimum is its count, so there is nothing to ask for.
        MinPanel.Visibility = IsDynamic ? Visibility.Visible : Visibility.Collapsed;
        CountLabel.Text = IsDynamic ? "Maximum elements" : "Number of elements";

        if (element is null || !int.TryParse(CountBox.Text?.Trim(), out var count) || count <= 0)
        {
            SizeHint.Text = "Enter how many elements the array holds.";
            return;
        }

        var min = IsDynamic && int.TryParse(MinBox.Text?.Trim(), out var m) && m >= 0 ? m : 0;

        var elementBits = ElementWireBits(element);
        if (elementBits <= 0)
        {
            SizeHint.Text = IsDynamic ? $"{min} to {count} x {element.Name}." : $"{count} x {element.Name}.";
            return;
        }

        if (!IsDynamic)
        {
            var total = elementBits * count;
            SizeHint.Text = total % 8 == 0
                ? $"{count} x {FormatBits(elementBits)} = {total / 8} bytes on the wire."
                : $"{count} x {FormatBits(elementBits)} = {total} bits on the wire.";
            return;
        }

        // Spelling out both ends is the point of a variable array: what it costs at worst is the number a
        // frame budget is spent on, and what it costs at best is what the minimum just bought.
        SizeHint.Text =
            $"{min} to {count} elements — {FormatSize(elementBits * min)} to {FormatSize(elementBits * count)} "
            + $"of elements{(_existing?.Length is ArrayLength.CountFromField ? ", counted by an earlier field" : ", plus the length it carries")}.";
    }

    private static string FormatSize(int bits) => bits % 8 == 0 ? $"{bits / 8} bytes" : $"{bits} bits";

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

        var min = 0;
        if (IsDynamic)
        {
            if (!int.TryParse(MinBox.Text?.Trim(), out min) || min < 0)
            {
                ShowError("The minimum must be zero or a positive whole number."); return;
            }

            if (min > count)
            {
                ShowError($"The minimum ({min}) cannot exceed the maximum ({count})."); return;
            }
        }

        var length = BuildLength(_existing?.Length, count, min);

        if (_existing is null)
        {
            Result = _project.Types.AddArray(name!, option.Type.Id, count);
            Result.Length = length;
        }
        else
        {
            _existing.Name = name!;
            _existing.ElementTypeId = option.Type.Id;
            _existing.Length = length;
            Result = _existing;
        }

        _project.Types.Rebuild();
        _project.RefreshAll();
        DialogResult = true;
    }

    /// <summary>
    /// The length rule to save, given whatever the array had before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An existing dynamic rule is <b>kept</b> and only re-bounded. How a variable array carries its
    /// length — a count field, an inline prefix, a sentinel, or the end of the frame — is a wire-format
    /// decision that peers already agree on. Rewriting it because the user opened this dialog to change a
    /// maximum would change the bytes and break them silently, and this dialog never asked the question.
    /// </para>
    /// <para>
    /// Only a genuine switch between fixed and variable picks a new rule, and a new variable array gets a
    /// length prefix: it is the one self-describing option that needs nothing outside the type itself.
    /// The prefix is rounded to a whole byte so the elements after it stay byte-addressable.
    /// </para>
    /// </remarks>
    private ArrayLength BuildLength(ArrayLength? previous, int count, int min)
    {
        if (!IsDynamic) return new ArrayLength.Fixed(count);

        return previous switch
        {
            ArrayLength.CountFromField c => c with { MaxCount = count, MinCount = min },
            ArrayLength.LengthPrefixed l => l with { MaxCount = count, MinCount = min, PrefixBits = PrefixBitsFor(count) },
            ArrayLength.Terminated t => t with { MaxCount = count, MinCount = min },
            ArrayLength.FillRemaining r => r with { MaxCount = count, MinCount = min },
            _ => new ArrayLength.LengthPrefixed(PrefixBitsFor(count), count, min),
        };
    }

    /// <summary>Whole bytes of length prefix, wide enough to express <paramref name="capacity"/>.</summary>
    private static int PrefixBitsFor(int capacity) =>
        Math.Max(8, BitMath.AlignUp(BitMath.BitsForUnsignedMax((ulong)capacity), 8));

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorBanner.Visibility = Visibility.Collapsed;
}
