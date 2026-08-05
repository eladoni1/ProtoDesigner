using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application;

/// <summary>
/// What wire representation a primitive type gets by default, and which widths the user may choose.
/// </summary>
/// <remarks>
/// <para>
/// This lives here rather than in the editor dialog because it is policy, not presentation, and policy
/// buried in a WPF code-behind cannot be tested. It got that treatment after a real defect: choosing
/// <c>double</c> as the host left the size box on whatever the previous host had selected, so a double
/// was reported as one byte on the wire — not a narrower encoding of a double, just a wrong one.
/// </para>
/// <para>
/// The rule is: <b>a type goes on the wire as itself.</b> Narrowing is a deliberate act that requires a
/// declared range, because compression needs limits to map onto — without them there is nothing to scale
/// into the smaller field.
/// </para>
/// </remarks>
public static class WireSizePolicy
{
    /// <summary>
    /// How a host kind is represented on the wire when nothing else is said: as itself. A float stays
    /// IEEE-754, a signed integer stays two's complement, everything else is unsigned.
    /// </summary>
    public static WireForm NaturalFormFor(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.F32 or PrimitiveKind.F64 => WireForm.Float,
        PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64 => WireForm.Signed,
        _ => WireForm.Unsigned,
    };

    /// <summary>
    /// The width a type takes when the user has expressed no preference — the host's own width, except on
    /// a float wire, where the two IEEE formats are the only choices.
    /// </summary>
    public static int DefaultWidthFor(PrimitiveKind kind, WireForm form) => form == WireForm.Float
        ? (kind == PrimitiveKind.F32 ? 32 : 64)
        : kind.NaturalBits();

    /// <summary>
    /// The widths offered for a host kind and representation, in ascending order.
    /// </summary>
    /// <param name="kind">The host kind.</param>
    /// <param name="form">The chosen wire representation.</param>
    /// <param name="hasRange">
    /// Whether the type declares limits. Without them, widths below the host's are withheld: Save would
    /// reject the choice anyway, and offering an option that cannot be taken is worse than not offering
    /// it. Widths above the host's stay available — sending a value with room to grow is legitimate.
    /// </param>
    public static IReadOnlyList<int> AvailableWidths(PrimitiveKind kind, WireForm form, bool hasRange)
    {
        var widths = new List<int>();

        switch (form)
        {
            case WireForm.Float:
                // 32 vs 64 is a different IEEE format, not a compression, so both stay regardless of range.
                widths.Add(32);
                widths.Add(64);
                return widths;

            case WireForm.Signed:
                // Whole conventional sizes only: a two's-complement field of, say, 5 bits has no portable
                // representation in a generated struct.
                widths.AddRange(new[] { 8, 16, 32, 64 });
                break;

            default:
                for (var bits = 1; bits <= 7; bits++) widths.Add(bits);
                for (var bytes = 1; bytes <= 8; bytes++) widths.Add(bytes * 8);
                break;
        }

        if (!hasRange)
        {
            var natural = kind.NaturalBits();
            widths.RemoveAll(w => w < natural);
        }

        return widths;
    }
}
