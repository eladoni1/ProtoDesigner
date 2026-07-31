using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

/// <summary>The CRC field must not be inside its own coverage span.</summary>
public sealed class CrcCoversItselfRule : IValidationRule
{
    public string Code => DiagnosticCodes.CrcCoversItself;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
                for (var i = 0; i < message.Fields.Count; i++)
                {
                    var field = message.Fields[i];
                    if (field.Crc is null) continue;

                    var (fromIdx, toIdx) = ResolveSpan(field.Crc.Coverage, message, i);
                    if (fromIdx <= i && i <= toIdx)
                        yield return new Diagnostic(Code, Severity.Error,
                            $"CRC field '{field.Name}' is inside its own coverage span. Adjust the coverage to end before it.",
                            EntityPath.ForField(bus, message, field.Name));
                }
    }

    private static (int From, int To) ResolveSpan(CrcCoverage coverage, Message message, int crcIndex)
    {
        var from = 0;
        if (coverage.FromFieldId is { } fromId)
            from = Math.Max(0, message.Fields.FindIndex(f => f.Id == fromId));

        var to = message.Fields.Count - 1;
        if (coverage.ToFieldId is { } toId)
        {
            var idx = message.Fields.FindIndex(f => f.Id == toId);
            if (idx >= 0) to = idx;
        }
        else
        {
            // "to end" excludes the CRC field itself.
            to = crcIndex - 1;
        }
        return (from, to);
    }
}

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
            if (!reachable.Contains(type.Id))
                yield return new Diagnostic(Code, Severity.Info,
                    $"Type '{type.Name}' is not used by any message. Remove it or add a field that references it.",
                    EntityPath.ForType(type));
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
