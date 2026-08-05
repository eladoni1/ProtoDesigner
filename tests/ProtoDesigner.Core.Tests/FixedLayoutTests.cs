using static ProtoDesigner.Core.Tests.ModelBuilder;

namespace ProtoDesigner.Core.Tests;

public class FixedLayoutTests
{
    private readonly ModelBuilder _b = new();

    [Fact]
    public void Fields_are_placed_in_declaration_order()
    {
        var layout = _b.Layout(
            F("a", _b.U8()),
            F("b", _b.U8()),
            F("c", _b.U8()));

        layout.At("a", 0, 8);
        layout.At("b", 8, 8);
        layout.At("c", 16, 8);
        layout.Sized(24);
        Assert.Equal(3, layout.MinBytes);
    }

    [Fact]
    public void Unpacked_fields_are_byte_aligned_not_naturally_aligned()
    {
        // A u32 after a u8 starts at bit 8, not bit 32. Natural alignment is opt-in via AlignmentBits,
        // because a wire format is not a C struct.
        var layout = _b.Layout(
            F("flag", _b.U8()),
            F("value", _b.U32()));

        layout.At("value", 8, 32);
        layout.Sized(40);
    }

    [Fact]
    public void Explicit_alignment_inserts_padding()
    {
        var layout = _b.Layout(
            F("flag", _b.U8()),
            F("value", _b.U32(), new FieldEncoding { AlignmentBits = 32 }));

        layout.At("value", 32, 32);
        layout.Sized(64);

        var pad = Assert.Single(layout.Padding());
        Assert.Equal((8, 24), (pad.BitOffset, pad.BitWidth));
    }

    [Fact]
    public void An_explicit_width_narrower_than_the_type_is_honoured()
    {
        var layout = _b.Layout(
            F("small", _b.U32(), FieldEncoding.Sized(12)),
            F("after", _b.U8()));

        layout.At("small", 0, 12);
        layout.At("after", 16, 8); // "after" is not packed, so it byte-aligns past the 12-bit field
        layout.Sized(24);
    }

    [Fact]
    public void An_explicit_width_wider_than_the_type_is_allowed_as_reserved_space()
    {
        var layout = _b.Layout(F("padded", _b.U8(), FieldEncoding.Sized(16)));
        layout.At("padded", 0, 16);
        layout.Sized(16);
    }

    [Fact]
    public void The_message_is_padded_to_a_whole_byte_by_default()
    {
        var layout = _b.Layout(
            F("a", _b.U8(), FieldEncoding.Packed(4)),
            F("b", _b.U8(), FieldEncoding.Packed(4)),
            F("c", _b.U8(), FieldEncoding.Packed(4)));

        layout.Sized(16);
        var pad = Assert.Single(layout.Padding());
        Assert.Equal((12, 4), (pad.BitOffset, pad.BitWidth));
    }

    [Fact]
    public void Byte_padding_can_be_disabled_for_a_bit_stream_protocol()
    {
        var options = EffectiveLayoutOptions.Default with { PadToByteBoundary = false };

        var layout = _b.Layout(
            Msg("m",
                F("a", _b.U8(), FieldEncoding.Packed(4)),
                F("b", _b.U8(), FieldEncoding.Packed(4)),
                F("c", _b.U8(), FieldEncoding.Packed(4))),
            options);

        layout.Sized(12);
        Assert.Empty(layout.Padding());
    }

    [Fact]
    public void An_empty_message_has_one_region_and_no_bits()
    {
        var layout = _b.Layout(Msg("empty"));

        layout.Sized(0);
        Assert.Single(layout.Regions);
        Assert.Equal(LayoutRegionKind.Fixed, layout.Regions[0].Kind);
    }

    // The requirement "changing field size updates the layout and shifts later fields" is met by not
    // storing offsets at all: edit the model, recompute, and everything downstream moves.

    [Fact]
    public void Resizing_a_field_shifts_every_later_field()
    {
        var resized = F("head", _b.U32(), FieldEncoding.Sized(16));
        var message = Msg("m", resized, F("tail", _b.U16()));

        _b.Layout(message).At("tail", 16, 16);

        resized.Encoding.BitWidth = 24;

        _b.Layout(message).At("tail", 24, 16);
    }

    [Fact]
    public void Reordering_fields_recomputes_offsets()
    {
        var a = F("a", _b.U8());
        var b = F("b", _b.U32());
        var message = Msg("m", a, b);

        _b.Layout(message).Order("a", "b");

        message.MoveField(a.Id, 1);

        var layout = _b.Layout(message);
        layout.Order("b", "a");
        layout.At("b", 0, 32);
        layout.At("a", 32, 8);
    }

    [Fact]
    public void Removing_a_field_closes_the_gap()
    {
        var middle = F("middle", _b.U16());
        var message = Msg("m", F("first", _b.U8()), middle, F("last", _b.U8()));

        _b.Layout(message).At("last", 24, 8);

        Assert.True(message.RemoveField(middle.Id));

        _b.Layout(message).At("last", 8, 8);
    }

    [Fact]
    public void Renaming_a_field_changes_only_its_path()
    {
        var field = F("oldName", _b.U16());
        var message = Msg("m", F("before", _b.U8()), field);

        field.Name = "newName";

        var layout = _b.Layout(message);
        layout.Order("before", "newName");
        layout.At("newName", 8, 16);
    }

    [Fact]
    public void Endianness_falls_through_from_the_project_to_the_field()
    {
        _b.Options.Endianness = Endianness.Big;

        var message = Msg("m",
            F("inherited", _b.U16()),
            F("overridden", _b.U16(), new FieldEncoding { Endianness = Endianness.Little }));

        var layout = _b.Layout(message);

        Assert.Equal(Endianness.Big, layout["inherited"].Endianness);
        Assert.Equal(Endianness.Little, layout["overridden"].Endianness);
    }

    [Fact]
    public void A_message_option_beats_the_project_option()
    {
        _b.Options.Endianness = Endianness.Big;
        var message = Msg("m", F("value", _b.U16()));
        message.Options.Endianness = Endianness.Little;

        Assert.Equal(Endianness.Little, _b.Layout(message)["value"].Endianness);
    }

    [Fact]
    public void Default_alignment_can_be_widened_for_a_whole_message()
    {
        var message = Msg("m", F("a", _b.U8()), F("b", _b.U8()));
        message.Options.DefaultAlignmentBits = 16;

        var layout = _b.Layout(message);
        layout.At("a", 0, 8);
        layout.At("b", 16, 8);
        layout.Sized(24);
    }

    [Fact]
    public void The_transform_is_carried_onto_the_layout_node()
    {
        var transform = new ScalarTransform(offset: 1000, scale: 1);
        var temperature = _b.Param("Temperature", PrimitiveKind.U16, new NumericRange(1000, 1015));

        var layout = _b.Layout(F("temperature", temperature, new FieldEncoding { BitWidth = 4, AllowBitPacking = true, Transform = transform }));

        var node = layout["temperature"];
        Assert.Equal(4, node.BitWidth);
        Assert.Equal(transform, node.Transform);
        Assert.Equal(4, BitMath.RequiredBits(temperature.Range!.Value, node.Transform));
    }

    [Fact]
    public void Bool_and_char_occupy_a_byte_unless_narrowed()
    {
        var layout = _b.Layout(
            F("enabled", _b.Bool()),
            F("initial", _b.Char()),
            F("bit", _b.Bool(), FieldEncoding.Packed(1)));

        layout.At("enabled", 0, 8);
        layout.At("initial", 8, 8);
        layout.At("bit", 16, 1);
    }
}
