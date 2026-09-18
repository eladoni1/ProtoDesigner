using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ProtoDesigner.Application;
using ProtoDesigner.Application.Commands;
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
/// <para>
/// A variable array chooses between the two rules a user can actually author: an earlier field holds the
/// count, or the array writes its own count in front of its elements. <see cref="ArrayLength.Terminated"/>
/// and <see cref="ArrayLength.FillRemaining"/> stay out of the dialog — both are authored through the
/// project file — but an array already using one is <em>preserved</em>, because rewriting a wire format
/// because someone opened a dialog to change a maximum would break every deployed peer in silence.
/// </para>
/// <para>
/// The awkward corner is that <see cref="ArrayLength.CountFromField"/> holds one <see cref="FieldId"/> and
/// lives on the <em>type</em>, which is project-wide. An array used by two messages can only point into one
/// of them. That is reported rather than prevented — see the shared-array warning — in keeping with the
/// validator reporting instead of the editor refusing.
/// </para>
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

    /// <summary>Where a variable array's element count comes from.</summary>
    private sealed record SourceOption(string Label, bool FromField)
    {
        public override string ToString() => Label;
    }

    private static readonly SourceOption[] SourceOptions =
    [
        new("A count field earlier in the message", FromField: true),
        new("The array carries its own count on the wire", FromField: false),
    ];

    /// <summary>One eligible count field, or the "none available" placeholder.</summary>
    private sealed record CountFieldOption(string Label, CountFieldCandidate? Candidate)
    {
        public override string ToString() => Label;
    }

    private readonly List<CountFieldOption> _countFields = new();

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
        SourceBox.ItemsSource = SourceOptions;

        if (existing is null)
        {
            HeadingText.Text = "Create array";
            NameBox.Text = "NewArray";
            CountBox.Text = "4";
            MinBox.Text = "0";
            LengthBox.SelectedItem = LengthOptions[0];
            ElementBox.SelectedItem = options.FirstOrDefault();

            // A brand-new array is in no message yet, so there is no field to count it. Self-describing is
            // the only rule that works before it has been placed.
            SourceBox.SelectedItem = SourceOptions[1];
        }
        else
        {
            HeadingText.Text = $"Edit '{existing.Name}'";
            NameBox.Text = existing.Name;
            CountBox.Text = existing.Length.Capacity.ToString(CultureInfo.InvariantCulture);
            MinBox.Text = existing.Length.MinimumCount.ToString(CultureInfo.InvariantCulture);
            LengthBox.SelectedItem = LengthOptions[existing.Length.IsDynamic ? 1 : 0];
            ElementBox.SelectedItem = options.FirstOrDefault(o => o.Type.Id == existing.ElementTypeId);
            SourceBox.SelectedItem =
                SourceOptions[existing.Length is ArrayLength.CountFromField ? 0 : 1];
        }

        _loaded = true;
        RebuildCountFields();
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
    private void OnLengthKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;

        // A fixed array reports its count as its minimum too, so switching to Variable would arrive
        // pre-set to "between 4 and 4" — which is the fixed array the user just asked to stop having.
        if (IsDynamic && _existing?.Length is null or ArrayLength.Fixed) MinBox.Text = "0";

        RebuildCountFields();
        UpdateHints();
    }
    private void OnLengthSourceChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) UpdateHints(); }
    private void OnCountFieldChanged(object sender, SelectionChangedEventArgs e) { if (_loaded) UpdateHints(); }

    private void OnCountChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;

        // The capacity decides whether a candidate is wide enough, so its labels go stale as it changes.
        RebuildCountFields();
        UpdateHints();
    }

    private bool IsDynamic => (LengthBox.SelectedItem as LengthOption)?.IsDynamic == true;

    private bool CountsFromField => (SourceBox.SelectedItem as SourceOption)?.FromField == true;

    // ---- count field ------------------------------------------------------------------------------

    /// <summary>
    /// Repopulates the count-field list for the current capacity, keeping the selection where it can.
    /// </summary>
    /// <remarks>
    /// The candidates come from <see cref="CountFieldCandidates"/> rather than being worked out here, so the
    /// list agrees with what the layout engine will accept and what the validator will pass. A dialog is the
    /// one place in this codebase that cannot be tested, which is precisely why the rule does not live in it.
    /// </remarks>
    private void RebuildCountFields()
    {
        var previous = (CountFieldBox.SelectedItem as CountFieldOption)?.Candidate?.Field.Id
                       ?? (_existing?.Length as ArrayLength.CountFromField)?.CountFieldId;

        _countFields.Clear();

        var capacity = int.TryParse(CountBox.Text?.Trim(), out var c) && c > 0 ? c : 1;
        var usages = _existing is null
            ? Array.Empty<ArrayUsage>()
            : CountFieldCandidates.UsagesOf(_project.Project, _existing.Id).ToArray();
        var candidates = _existing is null
            ? Array.Empty<CountFieldCandidate>()
            : CountFieldCandidates.For(_project.Project, _existing.Id, capacity).ToArray();

        var manyMessages = usages.Select(u => u.Message).Distinct().Count() > 1;

        foreach (var candidate in candidates)
        {
            var where = manyMessages ? $"{candidate.Message.Name}." : "";
            var width = candidate.WireBits % 8 == 0
                ? $"{candidate.WireBits / 8} B"
                : $"{candidate.WireBits} bit";
            var shortfall = candidate.IsWideEnough
                ? ""
                : $" — only counts to {candidate.MaxCountable}";

            _countFields.Add(new CountFieldOption(
                $"{where}{candidate.Path}  ({width}{shortfall})", candidate));
        }

        if (_countFields.Count == 0)
        {
            _countFields.Add(new CountFieldOption(
                usages.Length == 0
                    ? "— add this array to a message first —"
                    : "— no unsigned integer field precedes it —",
                null));
        }

        CountFieldBox.ItemsSource = null;
        CountFieldBox.ItemsSource = _countFields;
        CountFieldBox.SelectedItem =
            _countFields.FirstOrDefault(o => o.Candidate?.Field.Id == previous) ?? _countFields[0];

        // Naming the other messages is the whole warning: a FieldId belongs to one of them, so the rest
        // will report PD0030 the moment this is saved. Reported, not refused — the same line the editor
        // takes on a duplicate message id.
        if (manyMessages && CountsFromField)
        {
            var names = string.Join(", ", usages.Select(u => u.Message.Name).Distinct());
            SharedWarning.Text =
                $"'{_existing!.Name}' is used by {names}. A count field belongs to one message, so the "
                + "others will report an error. Give them their own array type, or let this one carry its "
                + "own count.";
            SharedWarning.Visibility = Visibility.Visible;
        }
        else
        {
            SharedWarning.Visibility = Visibility.Collapsed;
        }
    }

    private CountFieldCandidate? SelectedCountField =>
        (CountFieldBox.SelectedItem as CountFieldOption)?.Candidate;

    private void UpdateHints()
    {
        var element = (ElementBox.SelectedItem as TypeOption)?.Type;
        ElementHint.Text = element is null
            ? "Pick the type each slot holds."
            : $"Each slot holds one {element.Name}. Change its wire size by editing that type.";

        // A fixed array's minimum is its count, so there is nothing to ask for.
        MinPanel.Visibility = IsDynamic ? Visibility.Visible : Visibility.Collapsed;
        CountLabel.Text = IsDynamic ? "Maximum elements" : "Number of elements";

        SourcePanel.Visibility = IsDynamic ? Visibility.Visible : Visibility.Collapsed;
        CountFieldPanel.Visibility = IsDynamic && CountsFromField ? Visibility.Visible : Visibility.Collapsed;
        SharedWarning.Visibility = IsDynamic && CountsFromField && SharedWarning.Text.Length > 0
            ? SharedWarning.Visibility
            : Visibility.Collapsed;

        CountFieldHint.Text = SelectedCountField is { } picked
            ? $"'{picked.Path}' holds the count, so nothing extra goes on the wire. It is {picked.WireBits} "
              + $"bit(s) wide and counts to {picked.MaxCountable}."
            : "The count has to be an unsigned integer laid out before this array, in the same message.";

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
        var carried = CountsFromField && SelectedCountField is not null
            ? ", counted by an earlier field"
            : $", plus a {PrefixBitsFor(count) / 8}-byte count in front of them";

        SizeHint.Text =
            $"{min} to {count} elements — {FormatSize(elementBits * min)} to {FormatSize(elementBits * count)} "
            + $"of elements{carried}.";
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

            // Falling back to a length prefix here would put two extra bytes on the wire that the user
            // explicitly said they did not want. Refuse instead, and say what would make it possible.
            if (CountsFromField &&
                SelectedCountField is null &&
                _existing?.Length is not (ArrayLength.Terminated or ArrayLength.FillRemaining))
            {
                ShowError(
                    "No field can count this array. Add an unsigned integer field before it in the "
                    + "message, or let the array carry its own count.");
                return;
            }
        }

        var length = BuildLength(_existing?.Length, count, min);

        var target = _existing ?? _project.Types.AddArray(name!, option.Type.Id, count);
        var edit = new ArrayEdit(name!, option.Type.Id, length);

        // Creating already pushed one command, and undoing it removes the whole type.
        if (_existing is null) edit.ApplyTo(target);
        else _project.Journal.Do(new EditArrayCommand(target, edit));
        Result = target;

        _project.Types.Rebuild();
        _project.RefreshAll();
        DialogResult = true;
    }

    /// <summary>
    /// The length rule to save, given whatever the array had before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dialog now asks the question it used to dodge, so a deliberate switch between "a field counts
    /// it" and "it counts itself" is honoured. Both are wire-format changes and both are the user's to
    /// make — but only when they picked one, which is why the two rules the dialog does not offer,
    /// <see cref="ArrayLength.Terminated"/> and <see cref="ArrayLength.FillRemaining"/>, are still kept
    /// exactly as they were and merely re-bounded. Rewriting a format nobody asked about would change the
    /// bytes and break every deployed peer in silence.
    /// </para>
    /// <para>
    /// A prefix is rounded up to a whole byte so the elements after it stay byte-addressable.
    /// </para>
    /// </remarks>
    private ArrayLength BuildLength(ArrayLength? previous, int count, int min)
    {
        if (!IsDynamic) return new ArrayLength.Fixed(count);

        // Rules the dialog does not offer are preserved rather than replaced.
        if (previous is ArrayLength.Terminated t) return t with { MaxCount = count, MinCount = min };
        if (previous is ArrayLength.FillRemaining r) return r with { MaxCount = count, MinCount = min };

        if (CountsFromField && SelectedCountField is { } picked)
            return new ArrayLength.CountFromField(picked.Field.Id, count, min);

        return new ArrayLength.LengthPrefixed(PrefixBitsFor(count), count, min);
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
