using ProtoDesigner.Core.Validation;

namespace ProtoDesigner.Core.Tests.Validation;

/// <summary>
/// The bus's identity enums must not be reported as broken for being what they are.
/// </summary>
/// <remarks>
/// They declare no members on purpose — the bus fills them at generation — and they are seeded into every
/// project. Both rules below fired on them, and PD0012 is an Error, so <b>every project containing one
/// refused to generate</b>. That is the defect these pin.
/// </remarks>
public class SyntheticEnumValidationTests
{
    private static Project WithSyntheticEnums()
    {
        var project = new Project("Ids");
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        project.Types.Add(new EnumType(TypeId.New(), "MessageId", PrimitiveKind.U32)
            { Synthetic = SyntheticEnum.MessageId });
        project.Types.Add(new EnumType(TypeId.New(), "ModuleId", PrimitiveKind.U32)
            { Synthetic = SyntheticEnum.ModuleId });

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        bus.AddModule("Sensor");
        var m = new Message(MessageId.New(), "Ping") { WireId = 1 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "counter", u32.Id));
        bus.Messages.Add(m);
        project.Buses.Add(bus);
        return project;
    }

    [Fact]
    public void An_empty_synthetic_enum_is_not_an_error()
    {
        var findings = new Validator().Validate(WithSyntheticEnums());

        Assert.DoesNotContain(findings, d => d.Code == DiagnosticCodes.EnumWithoutMembers);
        Assert.DoesNotContain(findings, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void An_unused_synthetic_enum_is_not_even_worth_a_note()
    {
        // Seeded into every project rather than built by anyone, so an unused one is vocabulary — the
        // same reason unused primitives are not reported.
        var findings = new Validator().Validate(WithSyntheticEnums());

        Assert.DoesNotContain(findings, d => d.Code == DiagnosticCodes.UnreferencedType);
    }

    [Fact]
    public void An_ordinary_empty_enum_is_still_an_error()
    {
        // The exemption must be narrow: a user who creates an enum and forgets its members still hears
        // about it.
        var project = WithSyntheticEnums();
        project.Types.Add(new EnumType(TypeId.New(), "Forgotten", PrimitiveKind.U8));

        Assert.Contains(new Validator().Validate(project),
            d => d.Code == DiagnosticCodes.EnumWithoutMembers);
    }
}
