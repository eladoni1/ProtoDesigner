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

    // ---- the factor ---------------------------------------------------------------------------------

    /// <summary>The worked example from the samples: a u16 declared 1000..1015, so 16 values.</summary>
    private static readonly NumericRange Temperature = new(1000, 1015);

    [Theory]
    [InlineData(4)]    // exactly enough: 16 values in 16 codes
    [InlineData(5)]    // room to spare
    [InlineData(8)]
    [InlineData(16)]
    public void An_integer_never_gets_a_factor_below_one(int bits)
    {
        // The reported bug. MinimumScale answers "the finest step these bits allow", which at 5 bits is
        // 15/31 and at 16 bits is 15/65535 — correct arithmetic, wrong question for a u16. There is
        // nothing between 1000 and 1001 to resolve, and dividing by 0.4838 makes a stored 1001 come back
        // as 1000.96. The offset already does the compressing; the factor's only job is to be 1.
        Assert.Equal(1m, WireSizePolicy.FittedScale(Temperature, bits, hostIsFloat: false));
    }

    [Fact]
    public void An_integer_too_wide_for_its_bits_still_gets_a_coarse_factor()
    {
        // The genuinely lossy direction, which stays. 1001 values cannot fit in 16 codes, so each code
        // has to stand for several — the user asked for that by choosing the width.
        var scale = WireSizePolicy.FittedScale(new NumericRange(0, 1000), bits: 4, hostIsFloat: false);

        Assert.True(scale > 1m, $"a range wider than its width should still be scaled, got {scale}");
    }

    [Fact]
    public void A_float_still_gets_the_finest_factor_its_width_allows()
    {
        // The clamp must not reach floats: quantizing a continuous quantity into the available codes is
        // the entire point of a scaled encoding, and 1 would throw away the resolution paid for.
        var scale = WireSizePolicy.FittedScale(new NumericRange(-1, 1), bits: 12, hostIsFloat: true);

        Assert.True(scale < 1m, $"a float should use a sub-unit step, got {scale}");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void Narrowing_an_integer_type_propagates_an_offset_and_no_scaling(int bits)
    {
        // The same bug seen from the binding side: this is what actually reaches the layout engine and
        // the generators, so a factor leaking in here changes the bytes on the wire.
        var project = new Project("Temp");
        var type = new ParameterType(TypeId.New(), "Temperature", PrimitiveKind.U16, Temperature)
        {
            WireForm = WireForm.Unsigned,
            WireBits = bits,
        };
        project.Types.Add(type);

        var binding = new FieldBinding("temperature", type.Id);
        WireEncodingPropagator.Apply(type, binding);

        var transform = Assert.NotNull(binding.Encoding.Transform);
        Assert.Equal(1000m, transform.Offset);
        Assert.Equal(1m, transform.Scale);
    }

    [Fact]
    public void A_narrowed_integer_stays_protobuf_exportable()
    {
        // Why the factor matters beyond arithmetic: the protobuf gate refuses any field whose scale is
        // not 1. A spurious 0.4838 would silently drop this message from every .proto export.
        var project = new Project("Temp");
        var type = new ParameterType(TypeId.New(), "Temperature", PrimitiveKind.U16, Temperature)
        {
            WireForm = WireForm.Unsigned,
            WireBits = 8,
        };
        project.Types.Add(type);

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Reading") { WireId = 1 };
        var binding = new FieldBinding("temperature", type.Id);
        WireEncodingPropagator.Apply(type, binding);
        message.Fields.Add(binding);
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var eligibility = ProtobufCompatibility.ForBus(project, bus).Single();

        Assert.True(eligibility.IsEligible, $"a narrowed integer was refused: {eligibility.Reason}");
    }
}
