using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

/// <summary>Warns when a message might not fit in the transport's frame budget.</summary>
public sealed class TransportBudgetRule : IValidationRule
{
    public string Code => DiagnosticCodes.MtuExceeded;

    // Conservative single-frame budgets before framing overhead. Ethernet ignores jumbo frames on purpose:
    // the point is to warn about the common case, not to be exhaustive.
    public const int EthernetMaxPayloadBytes = 1500;
    public const int UartMaxPayloadBytes     = 256;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
        {
            var limit = bus.Transport switch
            {
                Transport.Ethernet => EthernetMaxPayloadBytes,
                Transport.Uart     => UartMaxPayloadBytes,
                _ => int.MaxValue,
            };

            foreach (var message in bus.Messages)
            {
                var layout = ctx.TryLayout(bus, message, out _);
                if (layout is null) continue;

                if (layout.MaxBytes > limit)
                    yield return new Diagnostic(DiagnosticCodes.MtuExceeded, Severity.Warning,
                        $"Message '{message.Name}' can reach {layout.MaxBytes} bytes, above the {limit}-byte payload budget for {bus.Transport}.",
                        EntityPath.ForMessage(bus, message));
                else if (layout.MaxBytes > limit * 4 / 5)
                    yield return new Diagnostic(DiagnosticCodes.MtuAtRisk, Severity.Info,
                        $"Message '{message.Name}' at {layout.MaxBytes} bytes uses more than 80% of the {limit}-byte payload budget for {bus.Transport}.",
                        EntityPath.ForMessage(bus, message));
            }
        }
    }
}

/// <summary>Types no message uses, transitively. Informational — dead code warning for the type library.</summary>
public sealed class UnreferencedTypeRule : IValidationRule
{
    public string Code => DiagnosticCodes.UnreferencedType;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        var reachable = ctx.ReachableTypes;
        foreach (var type in ctx.Project.Types.All)
        {
            // Plain primitives are vocabulary, not design. A project seeds bool, char and the integer
            // widths so they are there when a field needs one; flagging each unused u64 buried the real
            // findings under a dozen notes about types the user never chose to create. An unused enum,
            // struct or array is different — somebody built that and then did not use it, which is worth
            // saying. Nothing is generated for an unreferenced type either way.
            if (type is ParameterType) continue;

            // Same argument for the bus's own identity enums: they are seeded into every project rather
            // than built by anyone, so an unused one is vocabulary, not a leftover.
            if (type is EnumType { Synthetic: not SyntheticEnum.None }) continue;

            if (!reachable.Contains(type.Id))
                yield return new Diagnostic(Code, Severity.Info,
                    $"Type '{type.Name}' is not used by any message. Remove it or add a field that references it.",
                    EntityPath.ForType(type));
        }
    }
}

public sealed class BusHasNoMessagesRule : IValidationRule
{
    public string Code => DiagnosticCodes.BusHasNoMessages;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
            if (bus.Messages.Count == 0)
                yield return new Diagnostic(Code, Severity.Info,
                    $"Bus '{bus.Name}' has no messages.",
                    EntityPath.ForBus(bus));
    }
}

public sealed class MessageHasNoFieldsRule : IValidationRule
{
    public string Code => DiagnosticCodes.MessageHasNoFields;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
                if (message.Fields.Count == 0)
                    yield return new Diagnostic(Code, Severity.Info,
                        $"Message '{message.Name}' has no fields.",
                        EntityPath.ForMessage(bus, message));
    }
}
