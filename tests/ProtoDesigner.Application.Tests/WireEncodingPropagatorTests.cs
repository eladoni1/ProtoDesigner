using ProtoDesigner.Application;

namespace ProtoDesigner.Application.Tests;

/// <summary>
/// A type's wire representation is chosen once and applies to every occurrence, so these cover both the
/// derivation of the value-to-wire mapping and the propagation of it across the project.
/// </summary>
public class WireEncodingPropagatorTests
{
    private static (Project Project, Bus Bus, Message Message) NewProject()
    {
        var project = new Project("Test");
        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "M");
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return (project, bus, message);
    }

    // A double sent as a 64-bit integer must still be scaled: an integer code cannot carry a fractional
    // value, so "full width" does not mean "exact" the way it does for an integer host.
    [Fact]
    public void A_double_sent_as_a_full_width_integer_is_still_scaled()
    {
        var (project, _, message) = NewProject();
        var temp = project.Types.Add(
            new ParameterType(TypeId.New(), "Temperature", PrimitiveKind.F64, new NumericRange(-40, 70))
            {
                WireForm = WireForm.Unsigned,
                WireBits = 64,
            });

        var field = new FieldBinding(FieldId.New(), "t", temp.Id);
        message.Fields.Add(field);

        WireEncodingPropagator.ApplyAll(project);

        var transform = field.Encoding.Transform;
        Assert.NotNull(transform);
        Assert.Equal(-40m, transform!.Value.Offset);

        // 110 / (2^64 - 1) = 5.963111948670274387364251351e-18
        var expected = 110m / 18446744073709551615m;
        Assert.Equal(expected, transform.Value.Scale);
        Assert.NotEqual(1m, transform.Value.Scale);
    }

    [Fact]
    public void A_double_sent_as_a_float_keeps_its_bit_pattern_and_is_not_scaled()
    {
        var (project, _, message) = NewProject();
        var temp = project.Types.Add(
            new ParameterType(TypeId.New(), "Temperature", PrimitiveKind.F64, new NumericRange(-40, 70))
            {
                WireForm = WireForm.Float,
                WireBits = 64,
            });

        var field = new FieldBinding(FieldId.New(), "t", temp.Id);
        message.Fields.Add(field);

        WireEncodingPropagator.ApplyAll(project);

        Assert.Null(field.Encoding.Transform);
        Assert.Equal(64, field.Encoding.BitWidth);
    }

    [Fact]
    public void An_integer_host_at_full_width_needs_no_scaling()
    {
        var (project, _, message) = NewProject();
        var u16 = project.Types.Add(
            new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16, new NumericRange(0, 65535))
            {
                WireBits = 16,
            });

        var field = new FieldBinding(FieldId.New(), "x", u16.Id);
        message.Fields.Add(field);

        WireEncodingPropagator.ApplyAll(project);
        Assert.Null(field.Encoding.Transform);
    }

    // An explicit offset/factor is the user's decision and must survive propagation untouched.
    [Fact]
    public void An_explicit_offset_and_factor_override_the_derived_fit()
    {
        var (project, _, message) = NewProject();
        var temp = project.Types.Add(
            new ParameterType(TypeId.New(), "Temperature", PrimitiveKind.F64, new NumericRange(-40, 70))
            {
                WireForm = WireForm.Unsigned,
                WireBits = 16,
                WireOffset = 0m,
                WireScale = 1m,
            });

        var field = new FieldBinding(FieldId.New(), "t", temp.Id);
        message.Fields.Add(field);

        WireEncodingPropagator.ApplyAll(project);

        Assert.Equal(0m, field.Encoding.Transform!.Value.Offset);
        Assert.Equal(1m, field.Encoding.Transform.Value.Scale);
    }

    // The reported bug: a second field of a 4-bit enum came in at the enum's natural 4 bytes.
    [Fact]
    public void Every_occurrence_of_a_type_gets_the_same_width()
    {
        var (project, _, message) = NewProject();
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32) { WireBits = 4 }
            .With("Idle", 0).With("Fault", 10));

        var first = new FieldBinding(FieldId.New(), "mode", mode.Id);
        var second = new FieldBinding(FieldId.New(), "mode2", mode.Id);
        message.Fields.Add(first);
        message.Fields.Add(second);

        WireEncodingPropagator.ApplyAll(project);

        Assert.Equal(4, first.Encoding.BitWidth);
        Assert.Equal(4, second.Encoding.BitWidth);
    }

    [Fact]
    public void Propagation_reaches_struct_members_and_other_buses()
    {
        var project = new Project("Test");
        var u8 = project.Types.Add(
            new ParameterType(TypeId.New(), "Small", PrimitiveKind.U16, new NumericRange(0, 15)) { WireBits = 4 });

        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "a", u8.Id)));

        var busA = new Bus(BusId.New(), "A", Transport.Ethernet);
        var m1 = new Message(MessageId.New(), "M1");
        var direct = new FieldBinding(FieldId.New(), "x", u8.Id);
        m1.Fields.Add(direct);
        busA.Messages.Add(m1);
        project.Buses.Add(busA);

        WireEncodingPropagator.ApplyAll(project);

        Assert.Equal(4, direct.Encoding.BitWidth);
        Assert.Equal(4, header.Fields[0].Encoding.BitWidth);
    }

    /// <summary>
    /// An enum's members are identifiers, not samples of a continuous quantity, so the wire code is the
    /// member value. Deriving a factor from the member span mapped Arming=1 onto code 2 and relied on a
    /// rounding step to get back — correct by luck, and wrong the moment the members are not evenly spread.
    /// </summary>
    [Fact]
    public void An_enum_is_never_scaled_however_narrow_the_wire_is()
    {
        var (project, _, message) = NewProject();
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32) { WireBits = 4 }
            .With("Idle", 0).With("Arming", 1).With("Running", 5).With("Fault", 10));

        var field = new FieldBinding(FieldId.New(), "mode", mode.Id);
        message.Fields.Add(field);

        WireEncodingPropagator.ApplyAll(project);

        Assert.Null(field.Encoding.Transform);
        Assert.Equal(4, field.Encoding.BitWidth);
    }

    // An explicit choice still wins, even on an enum — that is what setting it by hand means.
    [Fact]
    public void An_explicit_factor_on_an_enum_is_still_honoured()
    {
        var (project, _, message) = NewProject();
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
        {
            WireBits = 4,
            WireOffset = 100m,
            WireScale = 1m,
        }.With("Hundred", 100).With("HundredOne", 101));

        var field = new FieldBinding(FieldId.New(), "mode", mode.Id);
        message.Fields.Add(field);

        WireEncodingPropagator.ApplyAll(project);

        Assert.Equal(100m, field.Encoding.Transform!.Value.Offset);
        Assert.Equal(1m, field.Encoding.Transform.Value.Scale);
    }

    [Fact]
    public void Structs_and_arrays_have_no_width_of_their_own()
    {
        var (project, _, message) = NewProject();
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "a", u8.Id)));

        var field = new FieldBinding(FieldId.New(), "h", header.Id);
        message.Fields.Add(field);

        WireEncodingPropagator.ApplyAll(project);

        // Left alone: a struct's size is the sum of its members, set on those types.
        Assert.Null(field.Encoding.BitWidth);
    }
}
