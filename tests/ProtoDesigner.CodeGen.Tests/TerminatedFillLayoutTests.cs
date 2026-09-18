using ProtoDesigner.CodeGen.Runtime;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// The wire layout the two self-describing array kinds actually produce, checked against the C# reference
/// codec rather than against the C generator.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CTerminatedFillCrossCheck"/> is the real proof that the generated C decodes these without
/// being handed a count, and it needs MSVC. This does the half that can be checked anywhere: that the
/// bytes the cross-check expects are the bytes the declaration actually produces. Getting those wrong
/// would make the cross-check assert the wrong thing in a way no toolchain-less run could catch.
/// </para>
/// <para>
/// The reference codec is an independent implementation — it is what the whole cross-check suite compares
/// the generated C against — so agreeing with it here is a real check and not the generator marking its
/// own work.
/// </para>
/// </remarks>
public class TerminatedFillLayoutTests
{
    private readonly ReferenceCodec _codec = new();

    private static ProtocolIr Build(ArrayLength length)
    {
        var project = new Project("Sample");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var samples = project.Types.Add(new ArrayType(TypeId.New(), "Samples", u8.Id, length));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "M") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "lead", u8.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "samples", samples.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "tail", u8.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        return new IrBuilder().Build(project, bus);
    }

    private static Dictionary<string, object?> Values(params int[] samples) => new()
    {
        ["lead"] = 0xAA,
        ["samples"] = samples.Select(s => (object?)s).ToArray(),
        ["tail"] = 0xBB,
    };

    private string Hex(ArrayLength length, params int[] samples)
    {
        var ir = Build(length);
        return Convert.ToHexString(_codec.Encode(ir.Messages.Single(), Values(samples)));
    }

    [Fact]
    public void A_terminated_array_writes_its_elements_then_the_sentinel_then_the_trailing_field()
    {
        var hex = Hex(new ArrayLength.Terminated(new byte[] { 0x00 }, MaxCount: 8), 0x11, 0x22, 0x33);

        Assert.Equal("AA11223300BB", hex);
    }

    [Fact]
    public void An_empty_terminated_array_is_just_the_sentinel()
    {
        var hex = Hex(new ArrayLength.Terminated(new byte[] { 0x00 }, MaxCount: 8));

        Assert.Equal("AA00BB", hex);
    }

    [Fact]
    public void A_fill_remaining_array_writes_its_elements_and_nothing_else()
    {
        // No count, no prefix, no terminator — which is why the decoder has to measure what is left.
        var hex = Hex(new ArrayLength.FillRemaining(MaxCount: 8), 0x11, 0x22, 0x33);

        Assert.Equal("AA112233BB", hex);
    }

    /// <summary>
    /// The boundary the trailing reservation exists for: with no elements at all, the frame is the two
    /// fixed bytes back to back, and a decoder that measured the remainder without reserving
    /// <c>tail</c> would read it as an element and report a count of one.
    /// </summary>
    [Fact]
    public void An_empty_fill_remaining_array_leaves_the_two_fixed_fields_adjacent()
    {
        var hex = Hex(new ArrayLength.FillRemaining(MaxCount: 8));

        Assert.Equal("AABB", hex);
    }

    [Fact]
    public void A_full_terminated_array_still_carries_its_sentinel()
    {
        // 1 + 8 + 1 + 1 = 11 bytes, which is what the cross-check asserts as FULLLEN.
        var hex = Hex(new ArrayLength.Terminated(new byte[] { 0x00 }, MaxCount: 8),
                      1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Equal("AA010203040506070800BB", hex);
        Assert.Equal(11, hex.Length / 2);
    }

    [Fact]
    public void A_full_fill_remaining_array_has_no_framing_of_its_own()
    {
        // 1 + 8 + 1 = 10 bytes, matching the cross-check's FULLLEN.
        var hex = Hex(new ArrayLength.FillRemaining(MaxCount: 8), 1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Equal("AA0102030405060708BB", hex);
        Assert.Equal(10, hex.Length / 2);
    }
}
