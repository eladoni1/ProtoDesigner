using ProtoDesigner.Core.Layout;
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
        _ when kind.IsFloat() => WireForm.Float,
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

    /// <summary>
    /// The factor a fitted mapping should use for <paramref name="range"/> in <paramref name="bits"/>.
    /// </summary>
    /// <param name="range">The declared limits the mapping has to cover.</param>
    /// <param name="bits">The chosen wire width.</param>
    /// <param name="hostIsFloat">
    /// Whether the host type is <c>f32</c>/<c>f64</c>. This is the whole distinction: a float is a sample
    /// of a continuous quantity, so a finer step is more resolution and worth having. An integer has no
    /// values between its values.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>An integer never gets a factor below 1.</b> <see cref="BitMath.MinimumScale"/> answers "what is
    /// the finest step these bits allow across this range", which for 1000..1015 in 5 bits is 15/31 —
    /// mathematically right and meaningless for a <c>u16</c>, because there is nothing between 1000 and
    /// 1001 to resolve. Worse, it is *lossy in practice*: the encoder divides by 0.4838, so a stored 1001
    /// comes back as 1000.96 and rounds to whatever the decoder's rounding mode says. The offset alone
    /// already compresses the range; the factor's only job is to be 1.
    /// </para>
    /// <para>
    /// A factor above 1 is kept. That is the genuinely lossy case — a range too wide for the bits, where
    /// each code has to stand for several values — and the user asked for it by choosing the width.
    /// </para>
    /// </remarks>
    public static decimal FittedScale(NumericRange range, int bits, bool hostIsFloat)
    {
        var minimum = BitMath.MinimumScale(range, bits);
        return hostIsFloat ? minimum : Math.Max(1m, minimum);
    }

}
