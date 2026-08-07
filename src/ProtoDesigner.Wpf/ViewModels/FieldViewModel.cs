using ProtoDesigner.Application.Commands;
using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;
using ProtoDesigner.Wpf.Mvvm;

namespace ProtoDesigner.Wpf.ViewModels;

/// <summary>Unit a wire width is entered in. Sub-byte widths are the whole point, so bits stay available.</summary>
public enum WireUnit
{
    Bits,
    Bytes,
}

/// <summary>
/// One row of the field grid. Presents the split the tool is built on: the <em>type</em> says what a
/// value means on the host (kind + range), the <em>binding</em> says how wide it is on the wire and how
/// it is scaled to get there. Every edit routes through the command journal.
/// </summary>
public sealed class FieldViewModel : ObservableObject
{
    private readonly MessageViewModel _message;

    public FieldViewModel(MessageViewModel message, FieldBinding field)
    {
        _message = message;
        Field = field;
    }

    public FieldBinding Field { get; }

    private Project Project => _message.Project.Project;

    // ---- identity -------------------------------------------------------------------------------

    public string Name
    {
        get => Field.Name;
        set
        {
            if (Field.Name == value || string.IsNullOrWhiteSpace(value)) return;
            _message.Project.Journal.Do(new RenameFieldCommand(Field, value));
            OnPropertyChanged();
        }
    }

    /// <summary>The declared type's name — shown before the instance name, e.g. "Temperature coolantTemp".</summary>
    public string TypeName => _message.Project.TypeName(Field.TypeId);

    private TypeDefinition? Type =>
        Project.Types.TryGet(Field.TypeId, out var t) ? t : null;

    private ParameterType? AsParameter => Type as ParameterType;
    private EnumType? AsEnum => Type as EnumType;

    // ---- host side ------------------------------------------------------------------------------

    /// <summary>What the value looks like in the generated host struct — never packed, never scaled.</summary>
    public string HostLabel => Type switch
    {
        ParameterType p => $"{p.Kind} · {Bytes(p.Kind.NaturalBits())}",
        EnumType e => $"enum · {Bytes(e.UnderlyingKind.NaturalBits())}",
        StructType s => $"struct · {s.Fields.Count} field(s)",
        ArrayType a => a.Length.IsExactCount
            ? $"array · {a.Length.Capacity}"
            : $"array · {a.Length.MinimumCount}..{a.Length.Capacity}",
        _ => "—",
    };

    /// <summary>The declared limits, which are what make compression possible.</summary>
    public string RangeLabel => Type switch
    {
        ParameterType { Range: { } r } => $"{Trim(r.Min)} .. {Trim(r.Max)}",
        ParameterType => "unbounded",
        EnumType { MemberRange: { } r } => $"{Trim(r.Min)} .. {Trim(r.Max)}",
        _ => "—",
    };

    /// <summary>Range of the underlying type, when it has one — the input to the factor maths.</summary>
    private NumericRange? EffectiveRange => Type switch
    {
        ParameterType p => p.Range,
        EnumType e => e.MemberRange,
        _ => null,
    };

    // ---- wire side ------------------------------------------------------------------------------

    /// <summary>Natural width of the type in bits, used as the default when no width is declared.</summary>
    private int NaturalBits => Type switch
    {
        ParameterType p => p.Kind.NaturalBits(),
        EnumType e => e.UnderlyingKind.NaturalBits(),
        _ => 0,
    };

    /// <summary>Effective wire width in bits.</summary>
    public int WireBits => Field.Encoding.BitWidth ?? NaturalBits;

    /// <summary>
    /// Serialised size, read-only. The width belongs to the type — every occurrence of a type serialises
    /// identically — so it is chosen once in the type's edit dialog and propagated here.
    /// A struct or array shows "—": its size is the sum of what it is built from.
    /// </summary>
    public string WireLabel
    {
        get
        {
            if (!SupportsWireWidth) return "—";
            return WireBits % 8 == 0 ? $"{WireBits / 8} B" : $"{WireBits} bit";
        }
    }

    /// <summary>
    /// The byte and bit order this field will actually serialise with — "little · MSB".
    /// </summary>
    /// <remarks>
    /// Both come from the bus, so every row shows the same answer; they are here rather than stated once
    /// in the header because <em>which of them applies</em> varies per row, and that is the part a reader
    /// cannot work out at a glance:
    /// <list type="bullet">
    /// <item>1 bit — neither applies. There is one bit; no ordering of it exists.</item>
    /// <item>2..8 bits — bit order only. A byte has no byte order, but its bits can still be reversed.</item>
    /// <item>over 8 bits — both.</item>
    /// </list>
    /// No star marking inheritance any more: both are bus-level agreements, so there is nowhere else the
    /// answer could have come from and marking it "inherited" would imply an override exists somewhere.
    /// </remarks>
    public string WireOrderLabel
    {
        get
        {
            if (!SupportsWireWidth || WireBits < 2) return "—";

            var bits = _message.ResolvedBitOrder == BitOrder.LsbFirst ? "LSB" : "MSB";
            if (WireBits <= 8) return bits;

            var bytes = _message.ResolvedEndianness == Endianness.Big ? "big" : "little";
            return $"{bytes} · {bits}";
        }
    }

    // ---- factor ---------------------------------------------------------------------------------

    /// <summary>Value represented by one wire step. Derived from the type's range and wire size.</summary>
    public decimal Factor => Field.Encoding.Transform?.Scale ?? 1m;

    /// <summary>Read-only display of the factor; "—" for types that carry no single scalar value.</summary>
    public string FactorLabel => SupportsFactor
        ? decimal.Round(Factor, 6).ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "—";

    /// <summary>True when the field carries a scale, i.e. it is compressed rather than stored exactly.</summary>
    public bool IsCompressed => Field.Encoding.Transform is not null;

    /// <summary>Set when the chosen factor is too fine for the chosen width — surfaced inline on the row.</summary>
    public string? FactorProblem
    {
        get
        {
            if (EffectiveRange is not { } range || Field.Encoding.Transform is null) return null;
            var needed = BitMath.BitsForScale(range, Factor);
            return needed > WireBits
                ? $"needs {needed} bits at this factor"
                : null;
        }
    }

    public bool HasFactorProblem => FactorProblem is not null;

    // ---- editability ----------------------------------------------------------------------------

    /// <summary>Structs and arrays have no single scalar width, so their wire cells are inert.</summary>
    public bool SupportsWireWidth => Type is ParameterType or EnumType;

    public bool SupportsFactor => SupportsWireWidth && EffectiveRange is not null;

    // ---- refresh --------------------------------------------------------------------------------

    private void RaiseWireChanged()
    {
        OnPropertyChanged(nameof(WireBits));
        OnPropertyChanged(nameof(WireLabel));
        OnPropertyChanged(nameof(WireOrderLabel));
        OnPropertyChanged(nameof(Factor));
        OnPropertyChanged(nameof(FactorLabel));
        OnPropertyChanged(nameof(IsCompressed));
        OnPropertyChanged(nameof(FactorProblem));
        OnPropertyChanged(nameof(HasFactorProblem));
    }

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(TypeName));
        OnPropertyChanged(nameof(HostLabel));
        OnPropertyChanged(nameof(RangeLabel));
        OnPropertyChanged(nameof(SupportsWireWidth));
        OnPropertyChanged(nameof(SupportsFactor));
        RaiseWireChanged();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static string Bytes(int bits) =>
        bits % 8 == 0 ? $"{bits / 8} B" : $"{bits} bit";

    private static string Trim(decimal value) =>
        value == decimal.Truncate(value) && Math.Abs(value) < 1e15m
            ? decimal.Truncate(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
