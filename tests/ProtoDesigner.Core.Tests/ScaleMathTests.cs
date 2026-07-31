namespace ProtoDesigner.Core.Tests;

/// <summary>
/// The maths behind the editor's "wire size" column: pick a width, get the finest factor that still
/// covers the declared range. These are the worked examples the feature was specified against, so they
/// assert exact expected values rather than approximate behaviour.
/// </summary>
public class ScaleMathTests
{
    // Battery percentage 0.00..100.00 squeezed into one byte.
    [Fact]
    public void Battery_percentage_in_one_byte()
    {
        var scale = BitMath.MinimumScale(new NumericRange(0, 100), bits: 8);
        Assert.Equal(100m / 255m, scale);
        Assert.Equal(0.3922m, decimal.Round(scale, 4));
    }

    // Temperature -40..70 in one byte.
    [Fact]
    public void Temperature_in_one_byte()
    {
        var scale = BitMath.MinimumScale(new NumericRange(-40, 70), bits: 8);
        Assert.Equal(110m / 255m, scale);
        Assert.Equal(0.4314m, decimal.Round(scale, 4));
    }

    // Temperature -40..70 in five bits: 110 / 31.
    [Fact]
    public void Temperature_in_five_bits()
    {
        var scale = BitMath.MinimumScale(new NumericRange(-40, 70), bits: 5);
        Assert.Equal(110m / 31m, scale);
        Assert.Equal(3.5484m, decimal.Round(scale, 4));
    }

    // The point of the minimum: the top of the range must land on the largest available wire code.
    [Theory]
    [InlineData(-40, 70, 5, 31)]
    [InlineData(-40, 70, 8, 255)]
    [InlineData(0, 100, 8, 255)]
    [InlineData(1000, 1015, 4, 15)]
    public void The_range_maximum_maps_onto_the_largest_wire_code(int min, int max, int bits, int expectedTopCode)
    {
        var range = new NumericRange(min, max);
        var scale = BitMath.MinimumScale(range, bits);
        var transform = new ScalarTransform(offset: range.Min, scale: scale);

        Assert.Equal(0m, transform.ToWire(range.Min));
        Assert.Equal(expectedTopCode, decimal.Round(transform.ToWire(range.Max), 6));
    }

    [Fact]
    public void The_minimum_scale_is_exactly_wide_enough()
    {
        var range = new NumericRange(-40, 70);
        var scale = BitMath.MinimumScale(range, bits: 5);

        // At the computed scale the range fits in 5 bits...
        Assert.Equal(5, BitMath.BitsForScale(range, scale));

        // ...and any finer resolution needs more room.
        Assert.True(BitMath.BitsForScale(range, scale / 2m) > 5);
    }

    [Fact]
    public void A_coarser_factor_than_the_minimum_still_fits()
    {
        // The user overriding -40..70 to a whole-degree factor of 1.0 needs 111 codes, so 7 bits.
        var range = new NumericRange(-40, 70);
        Assert.Equal(7, BitMath.BitsForScale(range, 1m));

        // Which means it comfortably fits the byte they asked for.
        Assert.True(BitMath.BitsForScale(range, 1m) <= 8);
    }

    /// <summary>
    /// The closing of the loop: a scale derived for N bits must be accepted as fitting in N bits. These
    /// two are computed by different code paths — one divides, the other divides back — so decimal's
    /// 28-digit rounding lands the second an ulp either side of a whole code. 0..100 in 8 bits used to
    /// come back as needing 9 and was reported as an error against a project the editor itself produced.
    /// </summary>
    [Theory]
    [InlineData(0, 100, 8)]
    [InlineData(-40, 70, 8)]
    [InlineData(-40, 70, 5)]
    [InlineData(1000, 1015, 4)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 359, 9)]
    [InlineData(-180, 180, 12)]
    [InlineData(0, 1000000, 20)]
    public void A_derived_scale_always_fits_the_width_it_was_derived_for(int min, int max, int bits)
    {
        var range = new NumericRange(min, max);
        var scale = BitMath.MinimumScale(range, bits);
        var transform = new ScalarTransform(range.Min, scale);

        Assert.Equal(bits, BitMath.RequiredBits(range, transform));
        Assert.True(BitMath.BitsForScale(range, scale) <= bits);
    }

    // The snap must not paper over a width that is genuinely one bit short.
    [Fact]
    public void A_scale_that_is_one_code_too_fine_still_reports_the_wider_width()
    {
        var range = new NumericRange(0, 100);
        var scale = BitMath.MinimumScale(range, bits: 8) * 255m / 256m;   // needs 256 codes, not 255

        Assert.Equal(9, BitMath.RequiredBits(range, new ScalarTransform(range.Min, scale)));
    }

    [Fact]
    public void A_constant_range_needs_no_resolution()
    {
        Assert.Equal(1m, BitMath.MinimumScale(new NumericRange(5, 5), bits: 8));
        Assert.Equal(1, BitMath.BitsForScale(new NumericRange(5, 5), 1m));
    }

    [Fact]
    public void An_invalid_width_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BitMath.MinimumScale(new NumericRange(0, 10), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BitMath.MinimumScale(new NumericRange(0, 10), 65));
    }

    [Fact]
    public void A_non_positive_scale_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => BitMath.BitsForScale(new NumericRange(0, 10), 0m));
    }

    // ---- natural ranges -------------------------------------------------------------------------

    [Theory]
    [InlineData(PrimitiveKind.U8, 0, 255)]
    [InlineData(PrimitiveKind.I8, -128, 127)]
    [InlineData(PrimitiveKind.U16, 0, 65535)]
    [InlineData(PrimitiveKind.I16, -32768, 32767)]
    [InlineData(PrimitiveKind.Bool, 0, 1)]
    public void Integer_kinds_report_their_full_span(PrimitiveKind kind, long min, long max)
    {
        var range = kind.NaturalRange();
        Assert.NotNull(range);
        Assert.Equal(min, range!.Value.Min);
        Assert.Equal(max, range.Value.Max);
    }

    [Fact]
    public void Wide_integer_kinds_survive_the_decimal_round_trip()
    {
        Assert.Equal(long.MinValue, PrimitiveKind.I64.NaturalRange()!.Value.Min);
        Assert.Equal(long.MaxValue, PrimitiveKind.I64.NaturalRange()!.Value.Max);
        Assert.Equal(ulong.MaxValue, PrimitiveKind.U64.NaturalRange()!.Value.Max);
    }

    [Theory]
    [InlineData(PrimitiveKind.F32)]
    [InlineData(PrimitiveKind.F64)]
    public void Float_kinds_have_no_natural_range(PrimitiveKind kind)
    {
        // Their span exceeds decimal, and an unbounded float cannot be compressed anyway — the editor
        // asks for explicit bounds instead of inventing them.
        Assert.Null(kind.NaturalRange());
    }
}
