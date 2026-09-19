namespace ProtoDesigner.Core.Tests.Validation;

/// <summary>
/// The two rules that notice a container nobody has filled in yet: a bus with no messages, a message with
/// no fields.
/// </summary>
/// <remarks>
/// Both are <see cref="Severity.Info"/> on purpose. A half-built project is the normal state of one being
/// built, so these say "you are not finished" rather than "you are wrong" — which is why they must never
/// become Errors, since an Error blocks code generation and there is nothing wrong with generating an
/// empty bus's header.
/// <para>
/// They are here because a mutation pass found them: removing either rule from
/// <see cref="Validator.DefaultRules"/> left every suite in the repo green. The rules worked; nothing
/// asked whether they shipped.
/// </para>
/// </remarks>
public class EmptyContainerTests
{
    [Fact]
    public void A_bus_with_no_messages_reports_PD0061()
    {
        var b = new ValidationBuilder();

        var d = b.Run().Has(DiagnosticCodes.BusHasNoMessages);
        Assert.Equal(Severity.Info, d.Severity);
    }

    [Fact]
    public void A_message_with_no_fields_reports_PD0062()
    {
        var b = new ValidationBuilder();
        b.NewMessage("Empty");

        var d = b.Run().Has(DiagnosticCodes.MessageHasNoFields);
        Assert.Equal(Severity.Info, d.Severity);
    }

    [Fact]
    public void A_bus_carrying_a_message_with_a_field_reports_neither()
    {
        var b = new ValidationBuilder();
        b.NewMessage("Telemetry", ValidationBuilder.F("x", b.Prim("u8", PrimitiveKind.U8)));

        var diagnostics = b.Run();
        diagnostics.HasNo(DiagnosticCodes.BusHasNoMessages);
        diagnostics.HasNo(DiagnosticCodes.MessageHasNoFields);
    }
}
