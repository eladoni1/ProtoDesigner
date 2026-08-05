namespace ProtoDesigner.Application.Tests;

/// <summary>
/// The rule that a type goes on the wire as itself until the user says otherwise.
/// </summary>
/// <remarks>
/// This policy used to live inside the primitive editor dialog, where it could not be tested — and it was
/// wrong: choosing <c>double</c> as the host left the wire size on whatever the previous host had, so a
/// double reported as one byte. These tests exist so that particular defect cannot come back.
/// </remarks>
public class WireSizePolicyTests
{
    [Theory]
    [InlineData(PrimitiveKind.F32, WireForm.Float)]
    [InlineData(PrimitiveKind.F64, WireForm.Float)]
    [InlineData(PrimitiveKind.I8, WireForm.Signed)]
    [InlineData(PrimitiveKind.I64, WireForm.Signed)]
    [InlineData(PrimitiveKind.U8, WireForm.Unsigned)]
    [InlineData(PrimitiveKind.U64, WireForm.Unsigned)]
    [InlineData(PrimitiveKind.Bool, WireForm.Unsigned)]
    [InlineData(PrimitiveKind.Char, WireForm.Unsigned)]
    public void A_host_kind_goes_on_the_wire_as_itself(PrimitiveKind kind, WireForm expected) =>
        Assert.Equal(expected, WireSizePolicy.NaturalFormFor(kind));

    [Theory]
    [InlineData(PrimitiveKind.F64, 64)]
    [InlineData(PrimitiveKind.F32, 32)]
    [InlineData(PrimitiveKind.U8, 8)]
    [InlineData(PrimitiveKind.I16, 16)]
    [InlineData(PrimitiveKind.U32, 32)]
    [InlineData(PrimitiveKind.I64, 64)]
    public void The_default_width_is_the_host_width(PrimitiveKind kind, int expected)
    {
        var form = WireSizePolicy.NaturalFormFor(kind);
        Assert.Equal(expected, WireSizePolicy.DefaultWidthFor(kind, form));
    }

    [Fact]
    public void A_double_defaults_to_eight_bytes_not_one()
    {
        // The exact defect: a double reported as a single byte on the wire.
        const PrimitiveKind kind = PrimitiveKind.F64;
        var form = WireSizePolicy.NaturalFormFor(kind);
        var widths = WireSizePolicy.AvailableWidths(kind, form, hasRange: false);

        Assert.Equal(WireForm.Float, form);
        Assert.Equal(64, WireSizePolicy.DefaultWidthFor(kind, form));
        Assert.DoesNotContain(8, widths);
    }

    [Fact]
    public void Without_a_range_nothing_narrower_than_the_host_is_offered()
    {
        // Compression needs limits to map onto. Offering a width that Save would reject is worse than
        // not offering it.
        var widths = WireSizePolicy.AvailableWidths(PrimitiveKind.U32, WireForm.Unsigned, hasRange: false);

        Assert.All(widths, w => Assert.True(w >= 32, $"{w} is narrower than the u32 host"));
        Assert.Contains(32, widths);
    }

    [Fact]
    public void With_a_range_the_narrow_widths_appear()
    {
        var widths = WireSizePolicy.AvailableWidths(PrimitiveKind.U32, WireForm.Unsigned, hasRange: true);

        Assert.Contains(1, widths);
        Assert.Contains(4, widths);
        Assert.Contains(32, widths);
    }

    [Fact]
    public void Wider_than_the_host_stays_available_without_a_range()
    {
        // Sending a value with room to grow is legitimate and loses nothing.
        var widths = WireSizePolicy.AvailableWidths(PrimitiveKind.U8, WireForm.Unsigned, hasRange: false);

        Assert.Contains(8, widths);
        Assert.Contains(64, widths);
    }

    [Fact]
    public void Both_float_formats_stay_available_because_neither_is_a_compression()
    {
        // 32 vs 64 bits is a different IEEE format, not a narrowing, so a range is not required.
        var widths = WireSizePolicy.AvailableWidths(PrimitiveKind.F64, WireForm.Float, hasRange: false);

        Assert.Equal(new[] { 32, 64 }, widths);
    }

    [Fact]
    public void A_signed_wire_offers_only_whole_conventional_sizes()
    {
        // A two's-complement field of 5 bits has no portable representation in a generated struct.
        var widths = WireSizePolicy.AvailableWidths(PrimitiveKind.I32, WireForm.Signed, hasRange: true);

        Assert.Equal(new[] { 8, 16, 32, 64 }, widths);
    }

    [Fact]
    public void Every_offered_width_is_one_the_propagator_would_accept()
    {
        // The dialog and the propagator have to agree: a width the picker offers must survive being
        // pushed onto a binding, or the type and its fields end up describing different wire formats.
        var project = new Project("Widths");
        var type = new ParameterType(TypeId.New(), "Value", PrimitiveKind.U32,
            new NumericRange(0, 1000)) { WireForm = WireForm.Unsigned };
        project.Types.Add(type);

        foreach (var width in WireSizePolicy.AvailableWidths(PrimitiveKind.U32, WireForm.Unsigned, hasRange: true))
        {
            type.WireBits = width;
            var binding = new FieldBinding("v", type.Id);

            WireEncodingPropagator.Apply(type, binding);

            Assert.Equal(width, binding.Encoding.BitWidth);
        }
    }
}
