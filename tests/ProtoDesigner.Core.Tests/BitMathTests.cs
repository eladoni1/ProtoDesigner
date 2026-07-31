namespace ProtoDesigner.Core.Tests;

public class BitMathTests
{
    [Theory]
    [InlineData(0, 8, 0)]
    [InlineData(1, 8, 8)]
    [InlineData(7, 8, 8)]
    [InlineData(8, 8, 8)]
    [InlineData(9, 8, 16)]
    [InlineData(12, 32, 32)]
    [InlineData(64, 32, 64)]
    [InlineData(5, 1, 5)]
    public void AlignUp_rounds_to_the_next_multiple(int offset, int alignment, int expected) =>
        Assert.Equal(expected, BitMath.AlignUp(offset, alignment));

    [Fact]
    public void AlignUp_rejects_a_non_positive_alignment() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BitMath.AlignUp(8, 0));

    [Theory]
    [InlineData(0ul, 1)]
    [InlineData(1ul, 1)]
    [InlineData(2ul, 2)]
    [InlineData(3ul, 2)]
    [InlineData(4ul, 3)]
    [InlineData(7ul, 3)]
    [InlineData(10ul, 4)]
    [InlineData(255ul, 8)]
    [InlineData(256ul, 9)]
    [InlineData(65535ul, 16)]
    [InlineData(ulong.MaxValue, 64)]
    public void BitsForUnsignedMax_counts_significant_bits(ulong max, int expected) =>
        Assert.Equal(expected, BitMath.BitsForUnsignedMax(max));

    [Theory]
    [InlineData(0L, 0L, 1)]
    [InlineData(-1L, 0L, 1)]
    [InlineData(-2L, 1L, 2)]
    [InlineData(0L, 7L, 4)]
    [InlineData(-8L, 7L, 4)]
    [InlineData(-128L, 127L, 8)]
    [InlineData(-129L, 127L, 9)]
    [InlineData(long.MinValue, long.MaxValue, 64)]
    public void BitsForSignedRange_uses_twos_complement(long min, long max, int expected) =>
        Assert.Equal(expected, BitMath.BitsForSignedRange(min, max));

    [Theory]
    [InlineData(1, 1ul)]
    [InlineData(4, 15ul)]
    [InlineData(8, 255ul)]
    [InlineData(64, ulong.MaxValue)]
    public void MaxUnsigned_returns_the_widest_value(int bits, ulong expected) =>
        Assert.Equal(expected, BitMath.MaxUnsigned(bits));

    [Theory]
    [InlineData(0, 65)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public void MaxUnsigned_rejects_widths_outside_1_to_64(int _, int bits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BitMath.MaxUnsigned(bits));

    // The headline compression case: eleven values fit in four bits.
    [Fact]
    public void RequiredBits_packs_a_small_unsigned_range()
    {
        Assert.Equal(4, BitMath.RequiredBits(new NumericRange(0, 10)));
    }

    [Fact]
    public void RequiredBits_without_an_offset_pays_for_the_whole_magnitude()
    {
        Assert.Equal(7, BitMath.RequiredBits(new NumericRange(100, 110)));
    }

    [Fact]
    public void RequiredBits_biases_by_the_transform_offset()
    {
        var transform = new ScalarTransform(offset: 100, scale: 1);
        Assert.Equal(4, BitMath.RequiredBits(new NumericRange(100, 110), transform));
    }

    [Fact]
    public void RequiredBits_biases_a_negative_range_into_unsigned_codes()
    {
        Assert.Equal(11, BitMath.RequiredBits(new NumericRange(-1000, -990)));
        Assert.Equal(4, BitMath.RequiredBits(new NumericRange(-1000, -990), new ScalarTransform(-1000, 1)));
    }

    [Fact]
    public void RequiredBits_accounts_for_quantization_scale()
    {
        // 0..100 in steps of 0.5 is 201 distinct codes.
        var transform = new ScalarTransform(offset: 0, scale: 0.5m);
        Assert.Equal(8, BitMath.RequiredBits(new NumericRange(0, 100), transform));
    }

    [Fact]
    public void RequiredBits_falls_back_to_signed_when_codes_go_negative()
    {
        Assert.Equal(7, BitMath.RequiredBits(new NumericRange(-50, 50)));
    }

    [Fact]
    public void RequiredBits_of_a_constant_is_the_width_of_that_value()
    {
        // A constant still occupies bits unless a later pass elides it entirely.
        Assert.Equal(3, BitMath.RequiredBits(new NumericRange(5, 5)));
    }

    [Fact]
    public void RequiredBits_of_an_enum_spans_its_members()
    {
        var mode = new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
            .With("Idle", 0)
            .With("Running", 5)
            .With("Fault", 10);

        Assert.Equal(4, BitMath.RequiredBits(mode));
    }

    [Fact]
    public void RequiredBits_of_an_enum_starting_at_100_shrinks_with_an_offset()
    {
        var code = new EnumType(TypeId.New(), "Code", PrimitiveKind.U32)
            .With("First", 100)
            .With("Last", 110);

        Assert.Equal(7, BitMath.RequiredBits(code));
        Assert.Equal(4, BitMath.RequiredBits(code, new ScalarTransform(100, 1)));
    }

    [Fact]
    public void RequiredBits_of_a_flag_enum_covers_every_union()
    {
        var flags = new EnumType(TypeId.New(), "Flags", PrimitiveKind.U8, isFlags: true)
            .With("A", 1).With("B", 2).With("C", 4).With("D", 8);

        // Not 4 because the max member is 8 — 4 because A|B|C|D is 15.
        Assert.Equal(4, BitMath.RequiredBits(flags));

        flags.With("E", 16);
        Assert.Equal(5, BitMath.RequiredBits(flags));
    }

    [Fact]
    public void RequiredBits_rejects_an_enum_with_no_members()
    {
        var empty = new EnumType(TypeId.New(), "Empty", PrimitiveKind.U8);
        Assert.Throws<ArgumentException>(() => BitMath.RequiredBits(empty));
    }

    [Fact]
    public void NumericRange_rejects_inverted_bounds() =>
        Assert.Throws<ArgumentException>(() => new NumericRange(10, 0));

    [Fact]
    public void NumericRange_with_equal_bounds_is_a_constant() =>
        Assert.True(new NumericRange(7, 7).IsConstant);

    [Fact]
    public void ScalarTransform_rejects_a_non_positive_scale() =>
        Assert.Throws<ArgumentException>(() => new ScalarTransform(0, 0));

    [Fact]
    public void ScalarTransform_round_trips_a_value()
    {
        var transform = new ScalarTransform(offset: -40, scale: 0.25m);
        Assert.Equal(12.5m, transform.FromWire(transform.ToWire(12.5m)));
    }
}
