using static ProtoDesigner.Core.Tests.ModelBuilder;

namespace ProtoDesigner.Core.Tests;

public class BitPackingTests
{
    private readonly ModelBuilder _b = new();

    private static EffectiveLayoutOptions StorageUnit =>
        EffectiveLayoutOptions.Default with { PackingMode = BitPackingMode.StorageUnit };

    // The motivating example: an enum that is four bytes in C++ but only ever holds 0..10.
    [Fact]
    public void An_enum_can_be_serialized_in_four_bits()
    {
        var mode = _b.Enum("Mode", PrimitiveKind.U32,
            ("Idle", 0), ("Arming", 1), ("Running", 5), ("Fault", 10));

        var layout = _b.Layout(
            F("mode", mode, FieldEncoding.Packed(4)),
            F("subMode", mode, FieldEncoding.Packed(4)));

        layout.At("mode", 0, 4);
        layout.At("subMode", 4, 4);
        layout.Sized(8);

        Assert.Equal(4, BitMath.RequiredBits(mode));
        Assert.Equal(LayoutNodeKind.Enum, layout["mode"].Kind);
    }

    // The same type in two messages at two widths — the reason encoding lives on the binding, not the type.
    [Fact]
    public void One_type_can_have_different_widths_in_different_messages()
    {
        var mode = _b.Enum("Mode", PrimitiveKind.U32, ("Idle", 0), ("Fault", 10));

        var compact = _b.Layout(Msg("compact", F("mode", mode, FieldEncoding.Packed(4))));
        var native = _b.Layout(Msg("native", F("mode", mode)));

        Assert.Equal(4, compact["mode"].BitWidth);
        Assert.Equal(32, native["mode"].BitWidth);
        Assert.Equal(compact["mode"].TypeId, native["mode"].TypeId);
    }

    [Fact]
    public void Contiguous_packing_places_fields_back_to_back()
    {
        var layout = _b.Layout(
            F("a", _b.U8(), FieldEncoding.Packed(4)),
            F("b", _b.U8(), FieldEncoding.Packed(4)),
            F("c", _b.U8(), FieldEncoding.Packed(4)));

        layout.At("a", 0, 4);
        layout.At("b", 4, 4);
        layout.At("c", 8, 4);
    }

    [Fact]
    public void Contiguous_packing_allows_a_field_to_straddle_a_byte_boundary()
    {
        var layout = _b.Layout(
            F("a", _b.U8(), FieldEncoding.Packed(6)),
            F("b", _b.U8(), FieldEncoding.Packed(6)));

        layout.At("a", 0, 6);
        layout.At("b", 6, 6); // spans bits 6..11, crossing the byte boundary
        layout.Sized(16);
    }

    [Fact]
    public void Storage_unit_packing_starts_a_new_unit_rather_than_straddling()
    {
        var layout = _b.Layout(
            Msg("m",
                F("a", _b.U8(), FieldEncoding.Packed(6)),
                F("b", _b.U8(), FieldEncoding.Packed(6))),
            StorageUnit);

        layout.At("a", 0, 6);
        layout.At("b", 8, 6); // two bits left in the byte are not enough, so b starts the next one
        layout.Sized(16);
    }

    [Fact]
    public void Storage_unit_size_comes_from_the_underlying_primitive()
    {
        var wide = _b.Param("Wide", PrimitiveKind.U16);

        var layout = _b.Layout(
            Msg("m",
                F("a", wide, FieldEncoding.Packed(12)),
                F("b", wide, FieldEncoding.Packed(12))),
            StorageUnit);

        layout.At("a", 0, 12);
        layout.At("b", 16, 12); // 16-bit unit, so b starts at the next 16-bit boundary
        layout.Sized(32);
    }

    [Fact]
    public void Storage_unit_packing_still_shares_a_unit_when_the_field_fits()
    {
        var layout = _b.Layout(
            Msg("m",
                F("a", _b.U8(), FieldEncoding.Packed(4)),
                F("b", _b.U8(), FieldEncoding.Packed(4)),
                F("c", _b.U8(), FieldEncoding.Packed(4))),
            StorageUnit);

        layout.At("a", 0, 4);
        layout.At("b", 4, 4);
        layout.At("c", 8, 4);
    }

    [Fact]
    public void An_unpacked_field_after_a_packed_run_realigns()
    {
        var layout = _b.Layout(
            F("packed", _b.U8(), FieldEncoding.Packed(4)),
            F("aligned", _b.U16()));

        layout.At("packed", 0, 4);
        layout.At("aligned", 8, 16);
        layout.Sized(24);

        var pad = Assert.Single(layout.Padding());
        Assert.Equal((4, 4), (pad.BitOffset, pad.BitWidth));
    }

    [Fact]
    public void Explicit_alignment_overrides_packing()
    {
        var layout = _b.Layout(
            F("a", _b.U8(), FieldEncoding.Packed(4)),
            F("b", _b.U8(), new FieldEncoding { BitWidth = 4, AllowBitPacking = true, AlignmentBits = 8 }));

        layout.At("a", 0, 4);
        layout.At("b", 8, 4);
    }

    [Fact]
    public void A_single_bit_flag_field_costs_one_bit()
    {
        var layout = _b.Layout(
            F("ready", _b.Bool(), FieldEncoding.Packed(1)),
            F("armed", _b.Bool(), FieldEncoding.Packed(1)),
            F("fault", _b.Bool(), FieldEncoding.Packed(1)));

        layout.At("ready", 0, 1);
        layout.At("armed", 1, 1);
        layout.At("fault", 2, 1);
        layout.Sized(8);
    }

    [Fact]
    public void A_flag_enum_packs_into_the_width_of_its_union()
    {
        var flags = _b.Flags("Status", PrimitiveKind.U8, ("Ready", 1), ("Armed", 2), ("Fault", 4), ("Stale", 8));

        var required = BitMath.RequiredBits(flags);
        var layout = _b.Layout(F("status", flags, FieldEncoding.Packed(required)));

        Assert.Equal(4, required);
        layout.At("status", 0, 4);
    }

    [Fact]
    public void Bit_order_falls_through_like_endianness()
    {
        var message = Msg("m", F("value", _b.U16()));
        message.Options.BitOrder = BitOrder.LsbFirst;

        Assert.Equal(BitOrder.LsbFirst, _b.Layout(message)["value"].BitOrder);
    }
}
