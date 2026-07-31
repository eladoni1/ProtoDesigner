using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

/// <summary>
/// Rules governing dynamic arrays: the count field must exist, precede the array, be an unsigned integer,
/// and its declared range must at least cover the array's capacity. A length prefix must be wide enough.
/// </summary>
public sealed class DynamicArrayRule : IValidationRule
{
    public string Code => DiagnosticCodes.CountFieldMissing;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        var findings = new List<Diagnostic>();
        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
                CheckMessage(ctx, bus, message, findings);
        return findings;
    }

    private static void CheckMessage(ValidationContext ctx, Bus bus, Message message, List<Diagnostic> sink)
    {
        var precedingFields = new Dictionary<FieldId, FieldBinding>();

        foreach (var field in message.Fields)
        {
            // Struct's inner fields precede the array too.
            if (ctx.Project.Types.TryGet(field.TypeId, out var innerType) && innerType is StructType s)
                foreach (var inner in s.Fields)
                    precedingFields.TryAdd(inner.Id, inner);

            if (ctx.Project.Types.TryGet(field.TypeId, out var arrType) && arrType is ArrayType arr)
                CheckArray(ctx, bus, message, field, arr, precedingFields, sink, priorInMessage: true);

            precedingFields.TryAdd(field.Id, field);
        }
    }

    private static void CheckArray(
        ValidationContext ctx,
        Bus bus,
        Message message,
        FieldBinding field,
        ArrayType array,
        Dictionary<FieldId, FieldBinding> preceding,
        List<Diagnostic> sink,
        bool priorInMessage)
    {
        var target = EntityPath.ForField(bus, message, field.Name);

        switch (array.Length)
        {
            case ArrayLength.CountFromField cff:
            {
                if (!preceding.TryGetValue(cff.CountFieldId, out var countField))
                {
                    sink.Add(new Diagnostic(DiagnosticCodes.CountFieldMissing, Severity.Error,
                        $"Array '{field.Name}' references count field {cff.CountFieldId} which is not laid out before it. " +
                        "Move the count field earlier in the message, or place it inside an earlier struct.",
                        target));
                    return;
                }

                // Kind must be an unsigned integer that can hold the capacity.
                if (ctx.Project.Types.TryGet(countField.TypeId, out var countType) && countType is ParameterType pt)
                {
                    if (!pt.Kind.IsIntegral() || pt.Kind.IsSigned())
                        sink.Add(new Diagnostic(DiagnosticCodes.CountFieldNotInteger, Severity.Error,
                            $"Array '{field.Name}' takes its count from '{countField.Name}', which is not an unsigned integer.",
                            target));

                    var width = countField.Encoding.BitWidth ?? pt.Kind.NaturalBits();
                    if (width is > 0 and <= BitMath.MaxScalarBits)
                    {
                        var maxCountable = BitMath.MaxUnsigned(width);
                        if (maxCountable < (ulong)cff.MaxCount)
                            sink.Add(new Diagnostic(DiagnosticCodes.CountFieldTooNarrow, Severity.Error,
                                $"Array '{field.Name}' has capacity {cff.MaxCount}, but its count field '{countField.Name}' is only {width} bit(s), max {maxCountable}.",
                                target));
                    }
                }
                break;
            }

            case ArrayLength.LengthPrefixed lp:
            {
                if (lp.PrefixBits is <= 0 or > BitMath.MaxScalarBits)
                {
                    sink.Add(new Diagnostic(DiagnosticCodes.LengthPrefixTooNarrow, Severity.Error,
                        $"Array '{field.Name}' has a length prefix of {lp.PrefixBits} bit(s); the supported range is 1..{BitMath.MaxScalarBits}.",
                        target));
                }
                else if (BitMath.MaxUnsigned(lp.PrefixBits) < (ulong)lp.MaxCount)
                {
                    sink.Add(new Diagnostic(DiagnosticCodes.LengthPrefixTooNarrow, Severity.Error,
                        $"Array '{field.Name}' has a {lp.PrefixBits}-bit length prefix which cannot express its capacity of {lp.MaxCount}.",
                        target));
                }
                break;
            }
        }
    }
}
