namespace ProtoDesigner.Core.Tests;

/// <summary>
/// Where an alignment policy can be set, and which level wins.
/// </summary>
/// <remarks>
/// <para>
/// Alignment resolves outward like every other layout option — field, then message, then bus, then
/// project, then the built-in default. <see cref="FixedLayoutTests"/> covers the two innermost levels;
/// these are the two outer ones and, more to the point, the precedence between them.
/// </para>
/// <para>
/// That precedence is the part worth pinning. Each level is easy to get right on its own and easy to get
/// wrong together: a chain that consulted the project before the bus would look correct in every
/// single-level test and be wrong the moment someone set both.
/// </para>
/// </remarks>
public class AlignmentPolicyTests
{
    /// <summary>A bus carrying one message of two bytes, so any widening is visible as a gap.</summary>
    private static (Project Project, Bus Bus, Message Message) Build()
    {
        var project = new Project("Align");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));

        var message = new Message(MessageId.New(), "M") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "a", u8.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "b", u8.Id));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return (project, bus, message);
    }

    private static MessageLayout Layout(Project p, Bus b, Message m) => new LayoutEngine().Compute(p, b, m);

    [Fact]
    public void Byte_alignment_is_the_default_when_nobody_says_otherwise()
    {
        var (project, bus, message) = Build();

        var layout = Layout(project, bus, message);

        Assert.Equal(0, layout["a"].BitOffset);
        Assert.Equal(8, layout["b"].BitOffset);
    }

    [Fact]
    public void A_bus_can_widen_the_alignment_for_every_message_on_it()
    {
        var (project, bus, message) = Build();
        bus.Options.DefaultAlignmentBits = 16;

        var layout = Layout(project, bus, message);

        Assert.Equal(0, layout["a"].BitOffset);
        Assert.Equal(16, layout["b"].BitOffset);
    }

    [Fact]
    public void A_project_can_widen_the_alignment_for_every_bus_in_it()
    {
        var (project, bus, message) = Build();
        project.Options.DefaultAlignmentBits = 32;

        var layout = Layout(project, bus, message);

        Assert.Equal(0, layout["a"].BitOffset);
        Assert.Equal(32, layout["b"].BitOffset);
    }

    // ---- precedence ------------------------------------------------------------------------------

    [Fact]
    public void A_bus_overrides_the_project()
    {
        var (project, bus, message) = Build();
        project.Options.DefaultAlignmentBits = 32;
        bus.Options.DefaultAlignmentBits = 16;

        Assert.Equal(16, Layout(project, bus, message)["b"].BitOffset);
    }

    [Fact]
    public void A_message_overrides_its_bus()
    {
        var (project, bus, message) = Build();
        bus.Options.DefaultAlignmentBits = 32;
        message.Options.DefaultAlignmentBits = 16;

        Assert.Equal(16, Layout(project, bus, message)["b"].BitOffset);
    }

    [Fact]
    public void A_field_overrides_everything_above_it()
    {
        var (project, bus, message) = Build();
        project.Options.DefaultAlignmentBits = 8;
        bus.Options.DefaultAlignmentBits = 8;
        message.Options.DefaultAlignmentBits = 8;
        message.Fields[1].Encoding.AlignmentBits = 32;

        Assert.Equal(32, Layout(project, bus, message)["b"].BitOffset);
    }

    /// <summary>
    /// The whole chain at once, each level set to something different, so a resolver that consulted them
    /// in any other order would land on the wrong number rather than coincidentally the right one.
    /// </summary>
    [Fact]
    public void The_innermost_level_that_says_anything_wins()
    {
        var (project, bus, message) = Build();
        project.Options.DefaultAlignmentBits = 64;
        bus.Options.DefaultAlignmentBits = 32;
        message.Options.DefaultAlignmentBits = 16;

        var resolved = project.OptionsFor(bus, message);
        Assert.Equal(16, resolved.DefaultAlignmentBits);

        // Remove them one at a time and the next level out takes over.
        message.Options.DefaultAlignmentBits = null;
        Assert.Equal(32, project.OptionsFor(bus, message).DefaultAlignmentBits);

        bus.Options.DefaultAlignmentBits = null;
        Assert.Equal(64, project.OptionsFor(bus, message).DefaultAlignmentBits);

        project.Options.DefaultAlignmentBits = null;
        Assert.Equal(EffectiveLayoutOptions.Default.DefaultAlignmentBits,
                     project.OptionsFor(bus, message).DefaultAlignmentBits);
    }
}
