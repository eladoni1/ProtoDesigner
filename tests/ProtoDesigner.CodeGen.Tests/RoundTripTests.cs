using ProtoDesigner.CodeGen.Runtime;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Encode-then-decode through the reference codec. These assert the actual wire semantics — widths,
/// endianness, sub-byte packing, transforms, region splits — independently of any generator's text.
/// </summary>
public class RoundTripTests
{
    private readonly ReferenceCodec _codec = new();

    [Fact]
    public void Scalars_round_trip_and_respect_declared_size()
    {
        var ir = Corpus.BuildIr(Corpus.Scalars());
        var msg = ir.Messages.Single();

        var values = new Dictionary<string, object?>
        {
            ["id"] = 200,
            ["sequence"] = 4660,      // 0x1234 — big-endian on the wire
            ["delta"] = -12345,
        };

        var bytes = _codec.Encode(msg, values);
        Assert.Equal((msg.MaxBits + 7) / 8, bytes.Length);

        var decoded = _codec.Decode(msg, bytes);
        Assert.Equal(200m, decoded["id"]);
        Assert.Equal(4660m, decoded["sequence"]);
        Assert.Equal(-12345m, decoded["delta"]);
    }

    [Fact]
    public void A_big_endian_field_is_byte_swapped_on_the_wire()
    {
        var ir = Corpus.BuildIr(Corpus.Scalars());
        var msg = ir.Messages.Single();

        var bytes = _codec.Encode(msg, new Dictionary<string, object?>
        {
            ["id"] = 0,
            ["sequence"] = 0x1234,
            ["delta"] = 0,
        });

        // id occupies byte 0; sequence is big-endian across bytes 1..2.
        Assert.Equal(0x12, bytes[1]);
        Assert.Equal(0x34, bytes[2]);
    }

    [Fact]
    public void A_four_bit_enum_and_a_compressed_range_share_one_byte()
    {
        var ir = Corpus.BuildIr(Corpus.PackedBits());
        var msg = ir.Messages.Single();

        // The whole point: Mode (needs 4 bits) + Temperature 1000..1015 (4 bits after offset) = 1 byte.
        Assert.Equal(4, msg.Fields.Single(f => f.Path == "mode").BitWidth);
        Assert.Equal(4, msg.Fields.Single(f => f.Path == "temperature").BitWidth);

        var bytes = _codec.Encode(msg, new Dictionary<string, object?>
        {
            ["mode"] = 10,             // Fault
            ["temperature"] = 1006,    // wire code 6
            ["checksum"] = 0xBEEF,
        });

        // 0xA in the high nibble, 0x6 in the low nibble.
        Assert.Equal(0xA6, bytes[0]);

        var decoded = _codec.Decode(msg, bytes);
        Assert.Equal(10m, decoded["mode"]);
        Assert.Equal(1006m, decoded["temperature"]);
        Assert.Equal((decimal)0xBEEF, decoded["checksum"]);
    }

    [Fact]
    public void A_transform_survives_the_round_trip_at_every_point_in_its_range()
    {
        var ir = Corpus.BuildIr(Corpus.PackedBits());
        var msg = ir.Messages.Single();

        for (var t = 1000; t <= 1015; t++)
        {
            var bytes = _codec.Encode(msg, new Dictionary<string, object?>
            {
                ["mode"] = 0,
                ["temperature"] = t,
                ["checksum"] = 0,
            });
            var decoded = _codec.Decode(msg, bytes);
            Assert.Equal((decimal)t, decoded["temperature"]);
        }
    }

    // ---- quantization -----------------------------------------------------------------------------

    /// <summary>
    /// The top of a quantized range must reach the top code the width was chosen for. Truncating instead
    /// of rounding put 70.0 at 254.99999999999997 and stored 254, so the last code was unreachable and the
    /// decoded maximum came back low.
    /// </summary>
    [Fact]
    public void The_top_of_a_quantized_range_reaches_the_top_wire_code()
    {
        var ir = Corpus.BuildIr(Corpus.Quantized());
        var msg = ir.Messages.Single();

        var bytes = _codec.Encode(msg, new Dictionary<string, object?>
        {
            ["temperature"] = 70m,
            ["battery"] = 100m,
        });

        Assert.Equal(255, bytes[0]);   // temperature at its maximum
        Assert.Equal(255, bytes[1]);   // battery at its maximum
    }

    [Fact]
    public void The_bottom_of_a_quantized_range_is_wire_code_zero()
    {
        var ir = Corpus.BuildIr(Corpus.Quantized());
        var msg = ir.Messages.Single();

        var bytes = _codec.Encode(msg, new Dictionary<string, object?>
        {
            ["temperature"] = -40m,
            ["battery"] = 0m,
        });

        Assert.Equal(0, bytes[0]);
        Assert.Equal(0, bytes[1]);
    }

    /// <summary>
    /// A quantized value comes back within half a code of what went in — that half is the definition of
    /// round-to-nearest, and truncation would have made it a full code on the low side.
    /// </summary>
    [Theory]
    [InlineData(-40)]
    [InlineData(-12.5)]
    [InlineData(0)]
    [InlineData(21.7)]
    [InlineData(69.9)]
    [InlineData(70)]
    public void A_quantized_value_round_trips_within_half_a_code(double input)
    {
        var ir = Corpus.BuildIr(Corpus.Quantized());
        var msg = ir.Messages.Single();
        var step = 110m / 255m;   // one wire code, in degrees

        var bytes = _codec.Encode(msg, new Dictionary<string, object?>
        {
            ["temperature"] = (decimal)input,
            ["battery"] = 0m,
        });
        var decoded = _codec.Decode(msg, bytes);

        var error = Math.Abs((decimal)decoded["temperature"]! - (decimal)input);
        Assert.True(error <= step / 2m + 0.0000001m,
            $"{input} came back with error {error}, more than half a code ({step / 2m}).");
    }

    /// <summary>
    /// A float sent at its natural width carries its IEEE bit pattern, so it must come back bit-exact —
    /// including a value with no short decimal form. Casting the value to an integer instead would have
    /// kept 3 of 3.14159.
    /// </summary>
    [Theory]
    [InlineData(3.14159265358979)]
    [InlineData(-0.5)]
    [InlineData(0)]
    [InlineData(1e-9)]
    public void A_raw_float_field_round_trips_bit_exactly(double value)
    {
        var ir = Corpus.BuildIr(Corpus.RawFloats());
        var msg = ir.Messages.Single();

        var bytes = _codec.Encode(msg, new Dictionary<string, object?> { ["reading"] = (decimal)value });
        var decoded = _codec.Decode(msg, bytes);

        Assert.Equal(value, (double)(decimal)decoded["reading"]!, precision: 12);
    }

    [Fact]
    public void A_flattened_struct_and_a_static_array_round_trip()
    {
        var ir = Corpus.BuildIr(Corpus.StructAndArray());
        var msg = ir.Messages.Single();

        var values = new Dictionary<string, object?>
        {
            ["header.messageId"] = 7,
            ["header.timestamp"] = 123456789,
            ["samples"] = new object[] { 1, 2, 3, 4 },
        };

        var bytes = _codec.Encode(msg, values);
        var decoded = _codec.Decode(msg, bytes);

        Assert.Equal(7m, decoded["header.messageId"]);
        Assert.Equal(123456789m, decoded["header.timestamp"]);
        var samples = Assert.IsAssignableFrom<IReadOnlyList<object>>(decoded["samples"]);
        Assert.Equal(new decimal[] { 1, 2, 3, 4 }, samples.Cast<decimal>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(32)]
    public void A_dynamic_array_round_trips_at_any_length_and_the_trailing_field_survives(int count)
    {
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var msg = ir.Messages.Single();

        var payload = Enumerable.Range(0, count).Select(i => (object)(i % 251)).ToArray();
        var values = new Dictionary<string, object?>
        {
            ["count"] = count,
            ["payload"] = payload,
            ["crc"] = 0xCAFE,
        };

        var bytes = _codec.Encode(msg, values);
        var decoded = _codec.Decode(msg, bytes);

        Assert.Equal((decimal)count, decoded["count"]);
        var got = Assert.IsAssignableFrom<IReadOnlyList<object>>(decoded["payload"]);
        Assert.Equal(count, got.Count);
        Assert.Equal(payload.Select(o => (decimal)(int)o), got.Cast<decimal>());

        // The field after the variable region is the real test: its offset is region-relative, so it
        // must land correctly regardless of how long the array turned out to be.
        Assert.Equal((decimal)0xCAFE, decoded["crc"]);
    }

    [Fact]
    public void The_encoded_length_of_a_dynamic_message_tracks_the_element_count()
    {
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var msg = ir.Messages.Single();

        int Length(int count) => _codec.Encode(msg, new Dictionary<string, object?>
        {
            ["count"] = count,
            ["payload"] = Enumerable.Range(0, count).Select(i => (object)i).ToArray(),
            ["crc"] = 0,
        }).Length;

        // 1 byte count + N bytes payload + 2 bytes crc
        Assert.Equal(3, Length(0));
        Assert.Equal(7, Length(4));
        Assert.Equal(13, Length(10));
    }

    [Fact]
    public void A_negative_value_sign_extends_correctly_at_a_narrow_width()
    {
        var buf = new BitBuffer();
        buf.WriteSigned(-3, 6, Endianness.Little);
        var bytes = buf.ToArray();

        var reader = new BitBuffer(bytes);
        Assert.Equal(-3, reader.ReadSigned(6, Endianness.Little));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(64)]
    public void Unsigned_values_round_trip_at_every_awkward_width(int width)
    {
        var max = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        foreach (var value in new[] { 0UL, 1UL, max / 2, max })
        {
            var buf = new BitBuffer();
            buf.WriteSigned(0, 3, Endianness.Little);   // deliberately misalign the cursor
            buf.WriteUnsigned(value, width, Endianness.Little);

            var reader = new BitBuffer(buf.ToArray());
            reader.ReadSigned(3, Endianness.Little);
            Assert.Equal(value, reader.ReadUnsigned(width, Endianness.Little));
        }
    }
}
