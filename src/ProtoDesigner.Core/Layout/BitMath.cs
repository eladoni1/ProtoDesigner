using System.Numerics;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Layout;

/// <summary>
/// Pure bit arithmetic shared by the layout engine and (later) the validation rules that decide whether a
/// user-requested serialized width can actually hold a field's declared range.
/// </summary>
public static class BitMath
{
    public const int MaxScalarBits = 64;

    /// <summary>Rounds <paramref name="bitOffset"/> up to the next multiple of <paramref name="alignmentBits"/>.</summary>
    public static int AlignUp(int bitOffset, int alignmentBits)
    {
        if (bitOffset < 0) throw new ArgumentOutOfRangeException(nameof(bitOffset));
        if (alignmentBits <= 0) throw new ArgumentOutOfRangeException(nameof(alignmentBits), "Alignment must be positive.");
        var remainder = bitOffset % alignmentBits;
        return remainder == 0 ? bitOffset : bitOffset + (alignmentBits - remainder);
    }

    /// <summary>Bits needed to hold the unsigned values 0..<paramref name="maxValue"/>. Zero still needs one bit.</summary>
    public static int BitsForUnsignedMax(ulong maxValue) =>
        maxValue == 0 ? 1 : BitOperations.Log2(maxValue) + 1;

    /// <summary>Bits needed to hold <paramref name="min"/>..<paramref name="max"/> in two's complement.</summary>
    public static int BitsForSignedRange(long min, long max)
    {
        if (min > max) throw new ArgumentException($"Range minimum {min} exceeds maximum {max}.", nameof(min));

        for (var bits = 1; bits < MaxScalarBits; bits++)
        {
            var limit = 1L << (bits - 1);
            if (min >= -limit && max <= limit - 1) return bits;
        }
        return MaxScalarBits;
    }

    /// <summary>Largest unsigned value representable in <paramref name="bits"/> bits.</summary>
    public static ulong MaxUnsigned(int bits)
    {
        if (bits is <= 0 or > MaxScalarBits)
            throw new ArgumentOutOfRangeException(nameof(bits), bits, $"Width must be 1..{MaxScalarBits}.");
        return bits == MaxScalarBits ? ulong.MaxValue : (1UL << bits) - 1;
    }

    /// <summary>
    /// Minimum serialized width for a range once the transform is applied. This is the number the
    /// "impossible compression size" validation rule compares a user's requested width against.
    /// </summary>
    /// <remarks>
    /// Wire codes below zero force a signed encoding. Set the transform's offset to the range minimum to
    /// bias the codes non-negative and get the tightest possible width.
    /// </remarks>
    public static int RequiredBits(NumericRange range, ScalarTransform? transform = null)
    {
        var t = transform ?? ScalarTransform.Identity;
        var lowCode = FloorToInt64(t.ToWire(range.Min));
        var highCode = CeilingToInt64(t.ToWire(range.Max));

        if (lowCode >= 0) return BitsForUnsignedMax((ulong)highCode);
        return BitsForSignedRange(lowCode, highCode);
    }

    /// <summary>Convenience overload for an enum: the span of its declared members.</summary>
    public static int RequiredBits(EnumType type, ScalarTransform? transform = null)
    {
        var range = type.MemberRange
            ?? throw new ArgumentException($"Enum '{type.Name}' declares no members.", nameof(type));

        if (!type.IsFlags) return RequiredBits(range, transform);

        // Flag enums must represent every union of their members, so the width is driven by the highest bit set.
        ulong mask = 0;
        foreach (var member in type.Members)
        {
            if (member.Value < 0)
                throw new ArgumentException($"Flag enum '{type.Name}' has a negative member '{member.Name}'.", nameof(type));
            mask |= (ulong)member.Value;
        }
        return BitsForUnsignedMax(mask);
    }

    /// <summary>
    /// The smallest scale factor that lets <paramref name="range"/> fit in <paramref name="bits"/> bits,
    /// i.e. the finest resolution the width can carry: <c>(Max - Min) / (2^bits - 1)</c>.
    /// </summary>
    /// <remarks>
    /// This is the number the editor shows when a user narrows a field's wire size. Pair it with an offset
    /// of <see cref="NumericRange.Min"/> and the top of the range lands exactly on the largest wire code:
    /// a -40..70 temperature in 5 bits gives 110/31 = 3.548..., and (70 - -40) / 3.548... = 31.
    /// A larger scale is always legal (coarser, still fits); a smaller one overflows the width.
    /// </remarks>
    public static decimal MinimumScale(NumericRange range, int bits)
    {
        if (bits is <= 0 or > MaxScalarBits)
            throw new ArgumentOutOfRangeException(nameof(bits), bits, $"Width must be 1..{MaxScalarBits}.");

        var span = range.Max - range.Min;
        if (span <= 0m) return 1m;   // a constant needs no resolution

        var codes = MaxUnsigned(bits);
        return span / codes;
    }

    /// <summary>
    /// Inverse of <see cref="MinimumScale"/>: the narrowest width that can carry <paramref name="range"/>
    /// at the given <paramref name="scale"/>. Used to tell a user which width their chosen factor implies.
    /// </summary>
    public static int BitsForScale(NumericRange range, decimal scale)
    {
        if (scale <= 0m)
            throw new ArgumentException($"Scale must be positive, got {scale}.", nameof(scale));

        var span = range.Max - range.Min;
        if (span <= 0m) return 1;

        var topCode = decimal.Ceiling(SnapToWholeCode(span / scale));
        if (topCode > ulong.MaxValue)
            throw new OverflowException($"Range {range} at scale {scale} needs more than 64 bits.");

        return BitsForUnsignedMax((ulong)topCode);
    }

    private static long FloorToInt64(decimal value)
    {
        var floored = decimal.Floor(SnapToWholeCode(value));
        if (floored < long.MinValue || floored > long.MaxValue)
            throw new OverflowException($"Wire code {floored} does not fit in 64 bits.");
        return (long)floored;
    }

    private static long CeilingToInt64(decimal value)
    {
        var ceiled = decimal.Ceiling(SnapToWholeCode(value));
        if (ceiled < long.MinValue || ceiled > long.MaxValue)
            throw new OverflowException($"Wire code {ceiled} does not fit in 64 bits.");
        return (long)ceiled;
    }

    /// <summary>
    /// Treats a code that sits a hair either side of a whole number as being exactly that number.
    /// </summary>
    /// <remarks>
    /// <see cref="MinimumScale"/> returns <c>span / codes</c> rounded to decimal's 28 significant digits.
    /// Dividing the span back by that scale therefore lands an ulp above or below <c>codes</c>, and which
    /// way it falls is pure luck: 110/255 rounds up, so a -40..70 field in 8 bits reports 255 codes, while
    /// 100/255 rounds down, so a 0..100 field in the same 8 bits reports 256 and gets rejected as needing
    /// 9. Without this snap the validator refuses the very scale the editor derived for the width the user
    /// picked. The tolerance is relative and far below any width a real protocol distinguishes.
    /// </remarks>
    private static decimal SnapToWholeCode(decimal value)
    {
        var nearest = decimal.Round(value, MidpointRounding.ToEven);
        var tolerance = Math.Max(Math.Abs(value), 1m) * 0.00000000000000000001m;   // 1e-20, relative
        return Math.Abs(value - nearest) <= tolerance ? nearest : value;
    }
}
