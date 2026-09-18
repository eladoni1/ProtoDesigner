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
/// Creates or edits a named primitive: host kind plus limits. Choosing an integer kind pre-fills the
/// range with that kind's full span, so a new <c>uint8</c> starts at 0..255 and the user narrows it
/// rather than inventing bounds from scratch.
/// </summary>
public partial class PrimitiveEditorDialog : Window
{
    private readonly ProjectViewModel _project;
    private readonly ParameterType? _existing;
    private bool _loaded;

    private PrimitiveEditorDialog(ProjectViewModel project, ParameterType? existing)
    {
        InitializeComponent();
        _project = project;
        _existing = existing;

        KindBox.ItemsSource = HostKinds;
        WireFormBox.ItemsSource = Enum.GetValues<WireForm>();

        if (existing is null)
        {
            HeadingText.Text = "Create primitive";
            NameBox.Text = "NewPrimitive";
            KindBox.SelectedItem = KindOptionFor(PrimitiveKind.U8);
            WireFormBox.SelectedItem = WireSizePolicy.NaturalFormFor(PrimitiveKind.U8);
        }
        else
        {
            HeadingText.Text = $"Edit '{existing.Name}'";
            NameBox.Text = existing.Name;
            KindBox.SelectedItem = KindOptionFor(existing.Kind);
            WireFormBox.SelectedItem = existing.WireForm;
            if (existing.Range is { } r)
            {
                MinBox.Text = Format(r.Min);
                MaxBox.Text = Format(r.Max);
            }
        }

        if (existing is { WireOffset: not null, WireScale: not null })
        {
            AutoScaleBox.IsChecked = false;
            OffsetBox.Text = Format(existing.WireOffset.Value);
            ScaleBox.Text = Format(existing.WireScale.Value);
        }

        _loaded = true;
        UpdateKindDependentText();
        RebuildWireSizes(existing?.WireBits);
        SyncManualScaleState();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static ParameterType? CreateNew(Window? owner, ProjectViewModel project)
    {
        var dialog = new PrimitiveEditorDialog(project, null) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public static bool Edit(Window? owner, ProjectViewModel project, ParameterType type)
    {
        var dialog = new PrimitiveEditorDialog(project, type) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    public ParameterType? Result { get; private set; }

    /// <summary>
    /// One entry in the host-type dropdown.
    /// </summary>
    /// <remarks>
    /// The raw enum names (<c>U8</c>, <c>I16</c>, <c>F64</c>) are how the model spells these, but nobody
    /// designing a protocol thinks in them — they think in <c>uint8_t</c> and <c>double</c>. This is a
    /// display wrapper only; <see cref="PrimitiveKind"/> is untouched.
    /// </remarks>
    private sealed record KindOption(PrimitiveKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly KindOption[] HostKinds =
    {
        new(PrimitiveKind.Bool, "bool  ·  true / false"),
        new(PrimitiveKind.Char, "char  ·  one byte of text"),
        new(PrimitiveKind.U8,   "uint8_t  ·  unsigned char, 1 byte"),
        new(PrimitiveKind.I8,   "int8_t  ·  signed char, 1 byte"),
        new(PrimitiveKind.U16,  "uint16_t  ·  2 bytes"),
        new(PrimitiveKind.I16,  "int16_t  ·  2 bytes"),
        new(PrimitiveKind.U32,  "uint32_t  ·  4 bytes"),
        new(PrimitiveKind.I32,  "int32_t  ·  4 bytes"),
        new(PrimitiveKind.U64,  "uint64_t  ·  8 bytes"),
        new(PrimitiveKind.I64,  "int64_t  ·  8 bytes"),
        new(PrimitiveKind.F32,  "float  ·  4 bytes"),
        new(PrimitiveKind.F64,  "double  ·  8 bytes"),
    };

    private static KindOption KindOptionFor(PrimitiveKind kind) =>
        HostKinds.FirstOrDefault(k => k.Kind == kind) ?? HostKinds[2];

    private PrimitiveKind SelectedKind =>
        KindBox.SelectedItem is KindOption o ? o.Kind : PrimitiveKind.U8;

    private void OnKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        UpdateKindDependentText();

        // Re-seed the bounds from the newly chosen kind. Editing an existing type keeps whatever the
        // user already entered, since those bounds are deliberate.
        if (_existing is null || string.IsNullOrWhiteSpace(MinBox.Text))
        {
            var natural = SelectedKind.NaturalRange();
            MinBox.Text = natural is { } n ? Format(n.Min) : string.Empty;
            MaxBox.Text = natural is { } x ? Format(x.Max) : string.Empty;
        }

        // The wire representation follows the host until the user says otherwise. Without this, choosing
        // 'double' left the size box on whatever the previous kind had selected — a double reported as
        // one byte, which is not a narrower encoding of a double but a wrong one.
        _loaded = false;
        WireFormBox.SelectedItem = WireSizePolicy.NaturalFormFor(SelectedKind);
        _loaded = true;
        RebuildWireSizes(preferBits: null);
    }

    /// <summary>
    /// Called as the range is typed: entering limits is what unlocks the narrower widths, so the size
    /// list has to be rebuilt rather than waiting for Save to reject the choice.
    /// </summary>
    private void OnRangeChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        RebuildWireSizes(SelectedWireBits);
    }


    private sealed record WireSizeOption(string Label, int Bits)
    {
        public override string ToString() => Label;
    }

    private void OnWireChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (ReferenceEquals(sender, WireFormBox)) RebuildWireSizes(null);
        UpdateWireHint();
    }

    /// <summary>
    /// Offers the widths that make sense for the chosen representation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default is always the host's own width — a type goes on the wire as itself unless the user
    /// asks for something narrower.
    /// </para>
    /// <para>
    /// Narrower widths are offered only once a range has been entered, because compression needs limits:
    /// without them there is nothing to map the smaller field onto, and Save would reject the choice
    /// anyway. Offering an option that cannot be saved is worse than not offering it. Wider than the host
    /// stays available — sending a value with room to grow is legitimate.
    /// </para>
    /// <para>
    /// Signed widths are restricted to whole conventional sizes because a two's-complement field of, say,
    /// 5 bits has no portable representation in a generated struct.
    /// </para>
    /// </remarks>
    private void RebuildWireSizes(int? preferBits)
    {
        var form = WireFormBox.SelectedItem is WireForm f ? f : WireForm.Unsigned;
        var kind = SelectedKind;

        var options = WireSizePolicy.AvailableWidths(kind, form, hasRange: TryCurrentRange(out _))
            .Select(bits => new WireSizeOption(DescribeWidth(bits, form), bits))
            .ToList();

        WireSizeBox.ItemsSource = options;

        var target = preferBits ?? WireSizePolicy.DefaultWidthFor(kind, form);
        WireSizeBox.SelectedItem =
            options.FirstOrDefault(o => o.Bits == target)
            ?? options.FirstOrDefault(o => o.Bits >= target)
            ?? options.LastOrDefault();

        UpdateWireHint();
    }

    private static string DescribeWidth(int bits, WireForm form)
    {
        if (form == WireForm.Float) return bits == 32 ? "4 bytes (float)" : "8 bytes (double)";
        if (bits % 8 != 0) return $"{bits} bit{(bits == 1 ? "" : "s")}";

        var bytes = bits / 8;
        var label = $"{bytes} byte{(bytes == 1 ? "" : "s")}";
        return form == WireForm.Signed ? $"{label} (int{bits})" : label;
    }

    private bool HostIsFloat => SelectedKind.IsFloat();

    private void OnAutoScaleToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        SyncManualScaleState();
    }

    private void OnManualScaleChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded) UpdateScaleHint();
    }

    /// <summary>
    /// Enables the manual boxes only where scaling is meaningful, and pre-fills them with the derived
    /// values so switching to manual starts from the fitted answer rather than from blank.
    /// </summary>
    private void SyncManualScaleState()
    {
        var scalable = WireFormBox.SelectedItem is not WireForm.Float;
        AutoScaleBox.IsEnabled = scalable;
        var manual = scalable && AutoScaleBox.IsChecked != true;
        ManualScaleGrid.IsEnabled = manual;

        if (manual && string.IsNullOrWhiteSpace(ScaleBox.Text) && TryDerive(out var offset, out var scale))
        {
            OffsetBox.Text = Format(offset);
            ScaleBox.Text = Format(scale);
        }
        UpdateScaleHint();
    }

    /// <summary>
    /// The fitted mapping: wire 0 is the range minimum, and one step is the finest the width allows —
    /// but never finer than 1 for an integer host, which has nothing between its values.
    /// </summary>
    private bool TryDerive(out decimal offset, out decimal scale)
    {
        offset = 0m;
        scale = 1m;
        if (!TryCurrentRange(out var range) || range.IsConstant) return false;

        offset = range.Min;
        scale = WireSizePolicy.FittedScale(range, SelectedWireBits, HostIsFloat);
        return true;
    }

    private void UpdateWireHint()
    {
        var bits = SelectedWireBits;
        var natural = SelectedKind.NaturalBits();

        if (WireFormBox.SelectedItem is WireForm.Float)
        {
            WireHint.Text = "Sent as an IEEE-754 value; the range is not used to compress it.";
            SyncManualScaleState();
            return;
        }

        // A fractional host cannot ride on an integer wire without a mapping, no matter how wide the
        // wire is — 8 bytes of integer still has to be told what one step means.
        if (!HostIsFloat && bits >= natural)
            WireHint.Text = "Full width — values are sent exactly, with no scaling.";
        else if (!TryCurrentRange(out _))
            WireHint.Text = HostIsFloat
                ? "A floating-point value sent as an integer needs a range so each step has a meaning."
                : "Set a range above to compress this type into fewer bits.";
        else
            WireHint.Text = $"{bits} bit(s) give {(bits >= 64 ? "1.8e19" : Math.Pow(2, bits).ToString("N0", CultureInfo.InvariantCulture))} steps across the range.";

        SyncManualScaleState();
    }

    private void UpdateScaleHint()
    {
        if (WireFormBox.SelectedItem is WireForm.Float)
        {
            ScaleHint.Text = "";
            return;
        }

        if (AutoScaleBox.IsChecked == true)
        {
            if (!TryDerive(out var offset, out var scale))
            {
                ScaleHint.Text = "";
                return;
            }
            ScaleHint.Text = $"wire = (value - {Format(offset)}) / {Format(scale)}";
            return;
        }

        if (!TryParse(OffsetBox.Text, out var o) || !TryParse(ScaleBox.Text, out var s) || s <= 0m)
        {
            ScaleHint.Text = "Enter a numeric offset and a positive factor.";
            return;
        }

        ScaleHint.Text = TryCurrentRange(out var range)
            ? $"wire = (value - {Format(o)}) / {Format(s)}   spans codes " +
              $"{decimal.Round((range.Min - o) / s, 2)} .. {decimal.Round((range.Max - o) / s, 2)}"
            : $"wire = (value - {Format(o)}) / {Format(s)}";
    }

    private int SelectedWireBits =>
        WireSizeBox.SelectedItem is WireSizeOption o ? o.Bits : SelectedKind.NaturalBits();

    private bool TryCurrentRange(out NumericRange range)
    {
        range = default;
        if (!TryParse(MinBox.Text, out var min) || !TryParse(MaxBox.Text, out var max)) return false;
        if (min > max) return false;
        range = new NumericRange(min, max);
        return true;
    }

    private void UpdateKindDependentText()
    {
        var kind = SelectedKind;
        HostSizeText.Text = $"Stored as {kind} — {kind.NaturalBits() / 8} byte(s) on the host.";

        RangeHint.Text = kind.NaturalRange() is null
            ? "Floating-point types have no automatic range. Enter the real-world limits you expect; " +
              "without them this value cannot be compressed on the wire."
            : "Pre-filled with the full span of the host type. Narrow it to describe what the value " +
              "actually holds — a tighter range compresses to fewer bits.";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        HideError();

        var name = NameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Give the type a name.");
            return;
        }

        var clashes = _project.Project.Types.All
            .Any(t => t.Id != _existing?.Id && string.Equals(t.Name, name, StringComparison.Ordinal));
        if (clashes)
        {
            ShowError($"Another type is already called '{name}'.");
            return;
        }

        NumericRange? range = null;
        var hasMin = !string.IsNullOrWhiteSpace(MinBox.Text);
        var hasMax = !string.IsNullOrWhiteSpace(MaxBox.Text);

        if (hasMin != hasMax)
        {
            ShowError("Enter both a minimum and a maximum, or leave both empty.");
            return;
        }

        if (hasMin)
        {
            if (!TryParse(MinBox.Text, out var min)) { ShowError($"'{MinBox.Text}' is not a number."); return; }
            if (!TryParse(MaxBox.Text, out var max)) { ShowError($"'{MaxBox.Text}' is not a number."); return; }
            if (min > max) { ShowError("The minimum must not exceed the maximum."); return; }
            range = new NumericRange(min, max);
        }

        var form = WireFormBox.SelectedItem is WireForm f ? f : WireForm.Unsigned;
        var bits = SelectedWireBits;

        var hostIsFloat = HostIsFloat;
        var isIntegerWire = form != WireForm.Float;
        var needsMapping = isIntegerWire && (hostIsFloat || bits < SelectedKind.NaturalBits());

        if (needsMapping && range is null)
        {
            ShowError(hostIsFloat
                ? "A floating-point value sent as an integer needs a range, so each wire step has a defined meaning."
                : "A narrower wire size needs a range — without limits there is nothing to scale into the smaller field.");
            return;
        }

        decimal? wireOffset = null;
        decimal? wireScale = null;

        if (isIntegerWire && AutoScaleBox.IsChecked != true)
        {
            if (!TryParse(OffsetBox.Text, out var o)) { ShowError($"'{OffsetBox.Text}' is not a valid offset."); return; }
            if (!TryParse(ScaleBox.Text, out var s) || s <= 0m) { ShowError("The factor must be a positive number."); return; }

            if (range is { } rr)
            {
                // Every value in the range has to land on a code the width can actually hold. A signed
                // wire spends one bit on the sign but reaches below zero, so its bounds differ.
                var lowCode = (rr.Min - o) / s;
                var highCode = (rr.Max - o) / s;

                decimal maxCode, minCode;
                if (form == WireForm.Signed)
                {
                    maxCode = bits >= 64 ? long.MaxValue : (decimal)(Math.Pow(2, bits - 1) - 1);
                    minCode = bits >= 64 ? long.MinValue : -(decimal)Math.Pow(2, bits - 1);
                }
                else
                {
                    maxCode = bits >= 64 ? ulong.MaxValue : (decimal)(Math.Pow(2, bits) - 1);
                    minCode = 0m;
                }

                if (lowCode < minCode)
                {
                    ShowError($"With this offset the minimum {rr.Min} maps to code {decimal.Round(lowCode, 4)}, " +
                              $"below the {minCode} this width reaches. " +
                              (form == WireForm.Signed ? "Raise the factor." : "Lower the offset, or switch to a signed wire."));
                    return;
                }
                if (highCode > maxCode)
                {
                    ShowError($"With this factor the maximum {rr.Max} needs code {decimal.Round(highCode, 2)}, " +
                              $"but {bits} bit(s) only reach {maxCode}. Increase the factor or the wire size.");
                    return;
                }
            }

            wireOffset = o;
            wireScale = s;
        }

        if (_existing is null)
        {
            var created = _project.Types.AddPrimitive(name, SelectedKind, range);
            created.WireForm = form;
            created.WireBits = bits;
            created.WireOffset = wireOffset;
            created.WireScale = wireScale;
            _project.PropagateWireEncoding(created);
            Result = created;
        }
        else
        {
            // A type serialises the same way everywhere, so the command pushes this choice onto every
            // field that uses it — and takes those writes back with it on undo.
            _project.Journal.Do(new EditPrimitiveCommand(_existing, new PrimitiveEdit(
                name, SelectedKind, range, form, bits, wireOffset, wireScale)));
            _project.Types.Rebuild();
            Result = _existing;
        }

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

    private static bool TryParse(string? text, out decimal value) =>
        decimal.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(decimal value) =>
        value == decimal.Truncate(value) && Math.Abs(value) < 1e15m
            ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
}
