using ProtoDesigner.CodeGen;
using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Application.Tests;

/// <summary>
/// Turning the frame-budget warning into a build failure, on request.
/// </summary>
/// <remarks>
/// <para>
/// <c>PD0050</c> stays a <see cref="Severity.Warning"/> in the validator and that is deliberate: a budget
/// is a property of the link, not of the protocol. A message too big for one UART frame is fine on a bus
/// that fragments, and the validator cannot know which it is looking at — refusing outright would be it
/// deciding something it has no way to decide.
/// </para>
/// <para>
/// A build, on the other hand, is being run for a particular link, and its operator can say so. That is
/// the whole distinction this option encodes, which is why it lives on the generation call rather than in
/// the rule.
/// </para>
/// </remarks>
public class FrameBudgetPolicyTests
{
    /// <summary>A UART bus carrying one message well past the 256-byte budget.</summary>
    private static (Project Project, IReadOnlyList<GenerationScope> Scopes) Oversized()
    {
        var project = new Project("Big");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var block = project.Types.Add(new ArrayType(TypeId.New(), "Block", u8.Id,
            new ArrayLength.Fixed(400)));

        var bus = new Bus(BusId.New(), "Link", Transport.Uart);
        var message = new Message(MessageId.New(), "Bulk") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "block", block.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        return (project, GenerationScopes.ForBus(bus));
    }

    private static CodeGenerationResult Run(FrameBudgetPolicy policy)
    {
        var (project, scopes) = Oversized();
        return CodeGenerationService.Generate(
            project, GeneratorCatalog.Find("c")!, scopes, new GeneratorOptions("proto"), policy);
    }

    [Fact]
    public void The_budget_is_only_a_warning_by_default()
    {
        // It has to still generate: a bit-packed message that overruns one link is exactly the thing
        // somebody is designing when they reach for this tool.
        var result = Run(FrameBudgetPolicy.Warn);

        Assert.False(result.Refused);
        Assert.NotEmpty(result.Files.Files);
    }

    [Fact]
    public void Blocking_refuses_and_reports_the_same_diagnostic()
    {
        var result = Run(FrameBudgetPolicy.Block);

        Assert.True(result.Refused);

        // The code is the rule's own, not a second error invented at the gate — that is what lets a
        // caller look it up and what keeps the report and the refusal saying one thing.
        var blocking = Assert.Single(result.BlockingErrors);
        Assert.Equal(DiagnosticCodes.MtuExceeded, blocking.Code);
        Assert.Contains("Bulk", blocking.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_inside_its_budget_generates_under_either_policy()
    {
        var project = new Project("Small");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var bus = new Bus(BusId.New(), "Link", Transport.Uart);
        var message = new Message(MessageId.New(), "Tiny") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "value", u8.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        foreach (var policy in new[] { FrameBudgetPolicy.Warn, FrameBudgetPolicy.Block })
        {
            var result = CodeGenerationService.Generate(
                project, GeneratorCatalog.Find("c")!, GenerationScopes.ForBus(bus),
                new GeneratorOptions("proto"), policy);

            Assert.False(result.Refused);
        }
    }

    /// <summary>
    /// A real error still refuses under either policy — the option adds a reason to stop, it does not
    /// replace the ones already there.
    /// </summary>
    [Fact]
    public void An_ordinary_validation_error_still_refuses_when_the_budget_is_only_a_warning()
    {
        var project = new Project("Broken");
        var wide = project.Types.Add(new ParameterType(TypeId.New(), "Wide", PrimitiveKind.U16,
            new NumericRange(0, 1000)));
        var bus = new Bus(BusId.New(), "Link", Transport.Uart);
        var message = new Message(MessageId.New(), "Bad") { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "tooSmall", wide.Id, FieldEncoding.Packed(4)));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var result = CodeGenerationService.Generate(
            project, GeneratorCatalog.Find("c")!, GenerationScopes.ForBus(bus),
            new GeneratorOptions("proto"), FrameBudgetPolicy.Warn);

        Assert.True(result.Refused);
        Assert.Contains(result.BlockingErrors, d => d.Code == DiagnosticCodes.WidthTooSmallForRange);
    }
}
