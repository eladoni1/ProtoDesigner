using static ProtoDesigner.Core.Tests.ModelBuilder;

namespace ProtoDesigner.Core.Tests;

public class DynamicArrayTests
{
    private readonly ModelBuilder _b = new();

    // A dynamic array splits the message into regions. Offsets stay constant within a region; only the
    // region's own start moves at parse time. This is what lets codegen emit constant offsets either side.
    [Fact]
    public void A_count_field_drives_a_variable_region()
    {
        var count = F("count", _b.U8());
        var samples = _b.Array("Samples", _b.U16(), new ArrayLength.CountFromField(count.Id, 10));

        var layout = _b.Layout(count, F("samples", samples));

        Assert.Equal(2, layout.Regions.Count);
        Assert.Equal(LayoutRegionKind.Fixed, layout.Regions[0].Kind);
        Assert.Equal(LayoutRegionKind.Variable, layout.Regions[1].Kind);

        layout.At("count", region: 0, bitOffset: 0, bitWidth: 8);
        layout.At("samples", region: 1, bitOffset: 0, bitWidth: 16);

        Assert.Equal(count.Id, layout.Regions[1].CountFieldId);
        Assert.Equal(16, layout.Regions[1].ElementBits);
        Assert.Equal(10, layout.Regions[1].MaxElements);

        layout.Spans(8, 168);
        Assert.False(layout.IsFixedSize);
        Assert.True(layout.HasVariableRegions);
    }

    [Fact]
    public void Fields_after_a_dynamic_array_start_a_new_region()
    {
        var count = F("count", _b.U8());
        var samples = _b.Array("Samples", _b.U16(), new ArrayLength.CountFromField(count.Id, 10));

        var layout = _b.Layout(count, F("samples", samples), F("crc", _b.U16()));

        Assert.Equal(3, layout.Regions.Count);
        layout.At("crc", region: 2, bitOffset: 0, bitWidth: 16);
        layout.Spans(24, 184);
    }

    [Fact]
    public void A_length_prefix_lives_in_the_preceding_fixed_region()
    {
        var payload = _b.Array("Payload", _b.U8(), new ArrayLength.LengthPrefixed(PrefixBits: 8, MaxCount: 64));

        var layout = _b.Layout(F("payload", payload));

        layout.At("payload.__length", region: 0, bitOffset: 0, bitWidth: 8);
        layout.At("payload", region: 1, bitOffset: 0, bitWidth: 8);
        Assert.Equal(8, layout.Regions[1].PrefixBits);
        layout.Spans(8, 520);
    }

    [Fact]
    public void A_length_prefix_too_narrow_for_the_capacity_is_rejected()
    {
        var payload = _b.Array("Payload", _b.U8(), new ArrayLength.LengthPrefixed(PrefixBits: 8, MaxCount: 300));

        var error = _b.LayoutThrows(F("payload", payload));
        Assert.Contains("capacity", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sentinel_terminated_array_always_costs_the_sentinel()
    {
        var text = _b.Array("Text", _b.Char(), new ArrayLength.Terminated(new byte[] { 0 }, MaxCount: 16));

        var layout = _b.Layout(F("text", text));

        layout.Spans(8, 136); // sentinel alone, up to sixteen chars plus the sentinel
    }

    [Fact]
    public void A_fill_remaining_array_can_be_empty()
    {
        var tail = _b.Array("Tail", _b.U8(), new ArrayLength.FillRemaining(MaxCount: 32));

        var layout = _b.Layout(F("header", _b.U16()), F("tail", tail));

        layout.Spans(16, 272);
        Assert.Equal(0, layout.Regions[1].MinBits);
    }

    [Fact]
    public void A_dynamic_array_at_the_start_needs_no_leading_fixed_region()
    {
        var payload = _b.Array("Payload", _b.U8(), new ArrayLength.FillRemaining(MaxCount: 4));

        var layout = _b.Layout(F("payload", payload));

        Assert.Single(layout.Regions);
        Assert.Equal(LayoutRegionKind.Variable, layout.Regions[0].Kind);
        layout.Spans(0, 32);
    }

    [Fact]
    public void Two_dynamic_arrays_produce_two_variable_regions()
    {
        var first = F("firstCount", _b.U8());
        var second = F("secondCount", _b.U8());
        var a = _b.Array("A", _b.U8(), new ArrayLength.CountFromField(first.Id, 4));
        var bArray = _b.Array("B", _b.U8(), new ArrayLength.CountFromField(second.Id, 4));

        var layout = _b.Layout(first, second, F("a", a), F("b", bArray));

        Assert.Equal(3, layout.Regions.Count);
        Assert.Equal(LayoutRegionKind.Fixed, layout.Regions[0].Kind);
        Assert.Equal(LayoutRegionKind.Variable, layout.Regions[1].Kind);
        Assert.Equal(LayoutRegionKind.Variable, layout.Regions[2].Kind);
        layout.Spans(16, 80);
    }

    [Fact]
    public void A_count_field_declared_after_the_array_is_rejected()
    {
        var count = F("count", _b.U8());
        var samples = _b.Array("Samples", _b.U16(), new ArrayLength.CountFromField(count.Id, 10));

        var error = _b.LayoutThrows(F("samples", samples), count);
        Assert.Contains("earlier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_count_field_that_does_not_exist_is_rejected()
    {
        var samples = _b.Array("Samples", _b.U16(), new ArrayLength.CountFromField(FieldId.New(), 10));

        _b.LayoutThrows(F("samples", samples));
    }

    [Fact]
    public void A_count_field_inside_an_earlier_struct_is_accepted()
    {
        var count = F("count", _b.U8());
        var header = _b.Struct("Header", F("tag", _b.U8()), count);
        var samples = _b.Array("Samples", _b.U16(), new ArrayLength.CountFromField(count.Id, 4));

        var layout = _b.Layout(F("header", header), F("samples", samples));

        layout.At("header.count", region: 0, bitOffset: 8, bitWidth: 8);
        layout.At("samples", region: 1, bitOffset: 0, bitWidth: 16);
    }

    [Fact]
    public void A_dynamic_array_inside_a_dynamic_array_is_rejected()
    {
        var inner = _b.Array("Inner", _b.U8(), new ArrayLength.FillRemaining(MaxCount: 4));
        var outer = _b.Array("Outer", inner, new ArrayLength.FillRemaining(MaxCount: 4));

        var error = _b.LayoutThrows(F("outer", outer));
        Assert.Contains("fixed size", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sub_byte_element_stride_is_rejected_while_byte_padding_is_on()
    {
        var mode = _b.Enum("Mode", PrimitiveKind.U8, ("Idle", 0), ("Fault", 10));
        var modes = _b.Array("Modes", mode, new ArrayLength.FillRemaining(MaxCount: 10));

        var error = _b.LayoutThrows(F("modes", modes, FieldEncoding.Packed(4)));
        Assert.Contains("byte-addressable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_sub_byte_element_stride_is_fine_in_a_bit_stream_layout()
    {
        var mode = _b.Enum("Mode", PrimitiveKind.U8, ("Idle", 0), ("Fault", 10));
        var count = F("count", _b.U8());
        var modes = _b.Array("Modes", mode, new ArrayLength.CountFromField(count.Id, 10));

        var layout = _b.Layout(
            Msg("m", count, F("modes", modes, FieldEncoding.Packed(4))),
            EffectiveLayoutOptions.Default with { PadToByteBoundary = false });

        Assert.Equal(4, layout.Regions[1].ElementBits);
        layout.Spans(8, 48);
    }

    [Fact]
    public void A_variable_region_is_byte_aligned_when_byte_padding_is_on()
    {
        var count = F("count", _b.U8(), FieldEncoding.Packed(4));
        var samples = _b.Array("Samples", _b.U8(), new ArrayLength.CountFromField(count.Id, 4));

        var layout = _b.Layout(count, F("samples", samples));

        // The 4-bit count is padded out so the array starts on a byte.
        Assert.Equal(8, layout.Regions[0].MinBits);
        layout.Spans(8, 40);
    }

    [Fact]
    public void A_struct_containing_a_dynamic_array_spans_regions()
    {
        var count = F("count", _b.U8());
        var samples = _b.Array("Samples", _b.U8(), new ArrayLength.CountFromField(count.Id, 4));
        var block = _b.Struct("Block", count, F("samples", samples));

        var layout = _b.Layout(F("block", block), F("crc", _b.U16()));

        layout.At("block.count", region: 0, bitOffset: 0, bitWidth: 8);
        layout.At("block.samples", region: 1, bitOffset: 0, bitWidth: 8);
        layout.At("crc", region: 2, bitOffset: 0, bitWidth: 16);
        layout.Spans(24, 56);
    }

    // ---- declared minimums -------------------------------------------------------------------------

    [Fact]
    public void A_declared_minimum_raises_the_message_floor()
    {
        // Without a minimum the smallest legal message carries an empty array. With one it carries that
        // many elements, and MinBits has to say so — a frame budget measured against an empty array is
        // measuring a message the sender promised never to send.
        var count = F("count", _b.U8());
        var samples = _b.Array("Samples", _b.U16(),
            new ArrayLength.CountFromField(count.Id, MaxCount: 10, MinCount: 3));

        var layout = _b.Layout(count, F("samples", samples));

        // 8 bits of count + 3 x 16 bits of guaranteed elements.
        Assert.Equal(56, layout.MinBits);
        Assert.Equal(168, layout.MaxBits);
        Assert.Equal(3, layout.Regions[1].MinElements);
        Assert.Equal(10, layout.Regions[1].MaxElements);
    }

    [Fact]
    public void No_declared_minimum_still_means_an_empty_array_is_possible()
    {
        // The default, and the behaviour every existing project relies on.
        var count = F("count", _b.U8());
        var samples = _b.Array("Samples", _b.U16(), new ArrayLength.CountFromField(count.Id, 10));

        var layout = _b.Layout(count, F("samples", samples));

        Assert.Equal(8, layout.MinBits);
        Assert.Equal(0, layout.Regions[1].MinElements);
    }

    [Fact]
    public void A_minimum_equal_to_the_capacity_makes_the_region_fixed_size()
    {
        // Pinning both ends of a variable array collapses it to a constant size. The region still exists
        // — it is still a Variable region carrying its own count — but it can no longer vary.
        var payload = _b.Array("Payload", _b.U8(),
            new ArrayLength.LengthPrefixed(PrefixBits: 8, MaxCount: 4, MinCount: 4));

        var layout = _b.Layout(F("payload", payload));

        Assert.True(layout.Regions[1].IsFixedSize);
        Assert.Equal(40, layout.MinBits);
        Assert.Equal(40, layout.MaxBits);
    }

    [Fact]
    public void A_sentinel_is_present_at_both_ends_of_the_span()
    {
        // The terminator is written whether the array is at its floor or its ceiling, so it belongs in
        // MinBits and MaxBits alike — a minimum must add to it, not replace it.
        var text = _b.Array("Text", _b.U8(),
            new ArrayLength.Terminated(new byte[] { 0 }, MaxCount: 8, MinCount: 2));

        var layout = _b.Layout(F("text", text));

        Assert.Equal(8 + (2 * 8), layout.MinBits);
        Assert.Equal(8 + (8 * 8), layout.MaxBits);
    }

    [Fact]
    public void A_minimum_above_the_capacity_describes_no_message_at_all()
    {
        // The validator reports this first (PD0037); the engine is the backstop that refuses to build a
        // region whose floor is above its ceiling.
        var payload = _b.Array("Payload", _b.U8(),
            new ArrayLength.LengthPrefixed(PrefixBits: 8, MaxCount: 4, MinCount: 5));

        var ex = Assert.Throws<LayoutException>(() => _b.Layout(F("payload", payload)));

        Assert.Contains("at least 5", ex.Message, StringComparison.Ordinal);
    }
}
