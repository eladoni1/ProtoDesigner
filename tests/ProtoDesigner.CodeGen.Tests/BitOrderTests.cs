using ProtoDesigner.CodeGen.Runtime;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// LSB-first bit packing, which UART links commonly use.
/// </summary>
/// <remarks>
/// Bit order decides which end of a <em>value</em> is consumed first; the stream itself always fills from
/// the top of each byte downwards. That is why the byte-copy fast path is only valid MSB-first — LSB-first
/// reverses the bits inside every byte, so copying bytes across would be wrong rather than merely slower.
/// </remarks>
public class BitOrderTests
{
    private static byte[] Write(ulong value, int width, BitOrder order)
    {
        var buf = new BitBuffer();
        buf.WriteUnsigned(value, width, Endianness.Little, order);
        return buf.ToArray();
    }

    [Fact]
    public void Lsb_first_reverses_the_bits_msb_first_would_have_written()
    {
        // 0b1011 in four bits. MSB-first puts 1,0,1,1 into the stream; LSB-first puts 1,1,0,1.
        Assert.Equal(0b1011_0000, Write(0b1011, 4, BitOrder.MsbFirst)[0]);
        Assert.Equal(0b1101_0000, Write(0b1011, 4, BitOrder.LsbFirst)[0]);
    }

    [Fact]
    public void A_whole_byte_is_reversed_too_so_the_fast_path_cannot_be_taken()
    {
        // The case the fast path would silently get wrong: byte-aligned and a whole byte wide, where a
        // straight copy looks correct until you check the bits.
        Assert.Equal(0b1000_0001, Write(0b1000_0001, 8, BitOrder.MsbFirst)[0]);
        Assert.Equal(0b1000_0001, Write(0b1000_0001, 8, BitOrder.LsbFirst)[0]);   // palindrome
        Assert.Equal(0b1100_0000, Write(0b0000_0011, 8, BitOrder.LsbFirst)[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(33)]
    [InlineData(64)]
    public void Every_width_round_trips_lsb_first(int width)
    {
        var value = width == 64 ? 0xDEAD_BEEF_CAFE_F00DUL : (0xA5A5_A5A5_A5A5_A5A5UL >> (64 - width));

        var buf = new BitBuffer();
        buf.WriteUnsigned(value, width, Endianness.Little, BitOrder.LsbFirst);

        var reader = new BitBuffer(buf.ToArray());
        Assert.Equal(value, reader.ReadUnsigned(width, Endianness.Little, BitOrder.LsbFirst));
    }

    [Fact]
    public void Consecutive_lsb_first_fields_pack_without_gaps()
    {
        // Straddling a byte boundary is where an off-by-one in the cursor shows up.
        var buf = new BitBuffer();
        buf.WriteUnsigned(0b101, 3, Endianness.Little, BitOrder.LsbFirst);
        buf.WriteUnsigned(0b1101, 4, Endianness.Little, BitOrder.LsbFirst);
        buf.WriteUnsigned(0b11, 2, Endianness.Little, BitOrder.LsbFirst);

        var reader = new BitBuffer(buf.ToArray());
        Assert.Equal(0b101UL, reader.ReadUnsigned(3, Endianness.Little, BitOrder.LsbFirst));
        Assert.Equal(0b1101UL, reader.ReadUnsigned(4, Endianness.Little, BitOrder.LsbFirst));
        Assert.Equal(0b11UL, reader.ReadUnsigned(2, Endianness.Little, BitOrder.LsbFirst));
    }

    [Fact]
    public void A_signed_value_round_trips_lsb_first()
    {
        var buf = new BitBuffer();
        buf.WriteSigned(-90, 12, Endianness.Little, BitOrder.LsbFirst);

        var reader = new BitBuffer(buf.ToArray());
        Assert.Equal(-90, reader.ReadSigned(12, Endianness.Little, BitOrder.LsbFirst));
    }

    [Fact]
    public void The_two_orders_disagree_which_is_the_whole_point()
    {
        // If this ever passed, the bit order would be decorative — the failure mode a picker in the UI
        // would hide completely.
        Assert.NotEqual(Write(0b0000_0011, 8, BitOrder.MsbFirst), Write(0b0000_0011, 8, BitOrder.LsbFirst));
    }
}
