using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;
using ProtoDesigner.Wpf.ViewModels;

namespace ProtoDesigner.Wpf.Views.Dialogs;

/// <summary>One editable row in the member list.</summary>
public sealed class EnumMemberRow : ObservableObject
{
    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private string _value = "0";
    public string Value { get => _value; set => SetProperty(ref _value, value); }

    /// <summary>
    /// False for a bus-filled enum, whose members are derived and must not be typed over.
    /// </summary>
    /// <remarks>
    /// Shown rather than hidden: "0 members" on a MessageId reads as broken, when the truth is that the
    /// bus supplies them and here is exactly what it supplies. Showing them read-only answers the
    /// question without letting anyone edit a list that is regenerated on every build.
    /// </remarks>
    public bool IsEditable { get; init; } = true;
}

/// <summary>
/// Creates or edits an enum: name, host type, wire size, and the member list. Several members may share
/// a value — aliases are a normal C++ idiom and are deliberately allowed.
/// </summary>
public partial class EnumEditorDialog : Window
{
    private readonly ProjectViewModel _project;
    private readonly EnumType? _existing;
    private readonly ObservableCollection<EnumMemberRow> _members = new();
    private bool _loaded;

    private EnumEditorDialog(ProjectViewModel project, EnumType? existing)
    {
        InitializeComponent();
        _project = project;
        _existing = existing;

        // Only integral kinds: an enum has no floating-point representation.
        KindBox.ItemsSource = Enum.GetValues<PrimitiveKind>().Where(k => k.IsIntegral()).ToArray();
        WireFormBox.ItemsSource = WireForms;
        MembersList.ItemsSource = _members;

        if (existing is null)
        {
            HeadingText.Text = "Create enum";
            NameBox.Text = "NewEnum";
            KindBox.SelectedItem = PrimitiveKind.U8;
            WireFormBox.SelectedItem = WireForms[0];
            _members.Add(new EnumMemberRow { Name = "First", Value = "0" });
        }
        else
        {
            HeadingText.Text = $"Edit '{existing.Name}'";
            NameBox.Text = existing.Name;
            KindBox.SelectedItem = existing.UnderlyingKind;
            WireFormBox.SelectedItem = WireForms.FirstOrDefault(f => f.Value == existing.WireForm) ?? WireForms[0];
            if (existing.Synthetic != SyntheticEnum.None) LoadSyntheticMembers(existing);
            else
                foreach (var m in existing.Members)
                    _members.Add(new EnumMemberRow { Name = m.Name, Value = m.Value.ToString(CultureInfo.InvariantCulture) });
        }

        _loaded = true;
        RebuildWireSizes(existing?.WireBits);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>
    /// Fills the member list from every bus, since a bus-filled enum resolves differently on each.
    /// </summary>
    /// <remarks>
    /// The bus name is appended when the project has more than one, because the same type genuinely means
    /// different things on each and a bare list would look self-contradictory.
    /// </remarks>
    private void LoadSyntheticMembers(EnumType existing)
    {
        var buses = _project.Project.Buses;
        var qualify = buses.Count > 1;

        foreach (var bus in buses)
            foreach (var m in SyntheticEnumMembers.For(bus, existing.Synthetic))
            {
                // NotAssigned is the same on every bus, so listing it per bus is noise.
                if (qualify && m.Value == 0 && !ReferenceEquals(bus, buses[0])) continue;

                _members.Add(new EnumMemberRow
                {
                    Name = qualify ? $"{m.Name}  ({bus.Name})" : m.Name,
                    Value = m.Value.ToString(CultureInfo.InvariantCulture),
                    IsEditable = false,
                });
            }

        AddMemberButton.Visibility = Visibility.Collapsed;
        SyntheticHint.Visibility = Visibility.Visible;
        SyntheticHint.Text = existing.Synthetic == SyntheticEnum.MessageId
            ? "Filled from the bus: every message that carries a wire id. Add or rename a message and this "
              + "list follows — it is regenerated on every build, so it cannot go stale and cannot be edited here."
            : "Filled from the bus: every module, numbered in declaration order. Add or rename a module on "
              + "the bus and this list follows.";
    }

    public static EnumType? CreateNew(Window? owner, ProjectViewModel project)
    {
        var d = new EnumEditorDialog(project, null) { Owner = owner };
        return d.ShowDialog() == true ? d.Result : null;
    }

    public static bool Edit(Window? owner, ProjectViewModel project, EnumType type)
    {
        var d = new EnumEditorDialog(project, type) { Owner = owner };
        return d.ShowDialog() == true;
    }

    public EnumType? Result { get; private set; }

    private PrimitiveKind SelectedKind =>
        KindBox.SelectedItem is PrimitiveKind k ? k : PrimitiveKind.U8;

    private sealed record WireSizeOption(string Label, int Bits)
    {
        public override string ToString() => Label;
    }

    private sealed record WireFormOption(string Label, WireForm Value)
    {
        public override string ToString() => Label;
    }

    // Float is absent by construction: an enum has no floating-point representation.
    private static readonly WireFormOption[] WireForms =
    {
        new("Unsigned integer", WireForm.Unsigned),
        new("Signed integer", WireForm.Signed),
    };

    private WireForm SelectedWireForm =>
        WireFormBox.SelectedItem is WireFormOption o ? o.Value : WireForm.Unsigned;

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        RebuildWireSizes(null);
    }

    private void OnWireFormChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        RebuildWireSizes(SelectedWireBits);
    }

    private void OnWireChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded) UpdateWireHint();
    }

    /// <summary>
    /// Rebuilds the width list for the chosen wire form. Widths are not capped by the host type: narrower
    /// is the whole point of compression, and wider is a legitimate choice when the wire slot is reserved
    /// for values the enum may grow into.
    /// </summary>
    private void RebuildWireSizes(int? preferBits)
    {
        var options = new List<WireSizeOption>();

        if (SelectedWireForm == WireForm.Signed)
        {
            // A signed field of an odd bit count has no portable representation in a generated struct.
            foreach (var bytes in new[] { 1, 2, 4, 8 })
                options.Add(new WireSizeOption($"{bytes} byte{(bytes == 1 ? "" : "s")} (int{bytes * 8})", bytes * 8));
        }
        else
        {
            for (var bits = 1; bits <= 7; bits++)
                options.Add(new WireSizeOption($"{bits} bit{(bits == 1 ? "" : "s")}", bits));
            for (var bytes = 1; bytes <= 8; bytes++)
                options.Add(new WireSizeOption($"{bytes} byte{(bytes == 1 ? "" : "s")}", bytes * 8));
        }

        WireSizeBox.ItemsSource = options;
        var target = preferBits ?? SelectedKind.NaturalBits();
        WireSizeBox.SelectedItem =
            options.FirstOrDefault(o => o.Bits == target)
            ?? options.FirstOrDefault(o => o.Bits >= target)
            ?? options.LastOrDefault();
        UpdateWireHint();
    }

    private int SelectedWireBits =>
        WireSizeBox.SelectedItem is WireSizeOption o ? o.Bits : SelectedKind.NaturalBits();

    private void UpdateWireHint()
    {
        if (WireHint is null) return;

        if (!TryReadMembers(out var parsed, out _) || parsed.Count == 0)
        {
            WireHint.Text = "Add members to see how many bits they need.";
            return;
        }

        var min = parsed.Min(m => m.Value);
        var max = parsed.Max(m => m.Value);
        var needed = RequiredBitsForForm(min, max);
        var chosen = SelectedWireBits;

        WireHint.Text = chosen >= needed
            ? $"Values {min}..{max} need {needed} bit(s); {chosen} is enough."
            : $"Values {min}..{max} need {needed} bit(s) — {chosen} is too narrow.";
    }

    /// <summary>
    /// Bits needed to carry the member span. An unsigned wire cannot represent a negative member at all,
    /// so that case reports the signed requirement and the save is refused with a specific message.
    /// </summary>
    private int RequiredBitsForForm(long min, long max) =>
        SelectedWireForm == WireForm.Signed || min < 0
            ? BitMath.BitsForSignedRange(min, max)
            : BitMath.RequiredBits(new NumericRange(min, max));

    private void OnAddMember(object sender, RoutedEventArgs e)
    {
        var next = 0L;
        if (TryReadMembers(out var parsed, out _) && parsed.Count > 0)
            next = parsed.Max(m => m.Value) + 1;

        _members.Add(new EnumMemberRow { Name = $"Member{_members.Count + 1}", Value = next.ToString(CultureInfo.InvariantCulture) });
        UpdateWireHint();
    }

    private void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EnumMemberRow row }) _members.Remove(row);
        UpdateWireHint();
    }

    private bool TryReadMembers(out List<EnumMember> members, out string? error)
    {
        members = new List<EnumMember>();
        error = null;

        // A bus-filled enum's rows are a read-only view of what the bus resolves to, not something to
        // write back. Storing them would recreate exactly the stale list this type exists to avoid.
        if (_existing?.Synthetic is not null and not SyntheticEnum.None) return true;

        foreach (var row in _members)
        {
            var name = row.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { error = "Every member needs a name."; return false; }
            if (!long.TryParse(row.Value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                error = $"'{row.Value}' is not a whole number (member '{name}').";
                return false;
            }
            members.Add(new EnumMember(name!, value));
        }
        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ShowError("Give the enum a name."); return; }

        if (_project.Project.Types.All.Any(t => t.Id != _existing?.Id &&
                                                string.Equals(t.Name, name, StringComparison.Ordinal)))
        {
            ShowError($"Another type is already called '{name}'."); return;
        }

        if (!TryReadMembers(out var members, out var memberError)) { ShowError(memberError!); return; }
        if (members.Count == 0) { ShowError("An enum needs at least one member."); return; }

        // Duplicate names are a real mistake; duplicate values are intentional aliases.
        var duplicateName = members.GroupBy(m => m.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicateName is not null) { ShowError($"Two members are both called '{duplicateName.Key}'."); return; }

        var bits = SelectedWireBits;
        var min = members.Min(m => m.Value);
        var max = members.Max(m => m.Value);

        if (min < 0 && SelectedWireForm != WireForm.Signed)
        {
            ShowError($"Member value {min} is negative — set the wire form to Signed integer to carry it.");
            return;
        }

        var needed = RequiredBitsForForm(min, max);
        if (bits < needed)
        {
            ShowError($"{bits} bit(s) cannot hold values {min}..{max}; {needed} are needed.");
            return;
        }

        var target = _existing;
        if (target is null)
        {
            target = _project.Types.AddEnum(name!, SelectedKind);
        }
        else
        {
            target.Name = name!;
            target.UnderlyingKind = SelectedKind;
        }

        if (target.Synthetic == SyntheticEnum.None)
        {
            target.Members.Clear();
            foreach (var m in members) target.Members.Add(m);
        }
        target.WireForm = SelectedWireForm;
        target.WireBits = bits;

        _project.Types.Rebuild();
        _project.PropagateWireEncoding(target);
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
