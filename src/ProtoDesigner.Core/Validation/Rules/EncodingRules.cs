using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

/// <summary>
/// Runs across every field, applying width/range/default/alignment checks in a single pass so we walk
/// the graph once. Emits several distinct diagnostic codes.
/// </summary>
public sealed class EncodingFeasibilityRule : IValidationRule
{
    public string Code => DiagnosticCodes.WidthTooSmallForRange;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        var findings = new List<Diagnostic>();

        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
                foreach (var field in message.Fields)
                    Check(ctx, EntityPath.ForField(bus, message, field.Name), field, findings);

        foreach (var type in ctx.Project.Types.All.OfType<StructType>())
            foreach (var field in type.Fields)
                Check(ctx, EntityPath.ForTypeField(type, field.Name), field, findings);

        return findings;
    }

    private static void Check(ValidationContext ctx, EntityPath target, FieldBinding field, List<Diagnostic> sink)
    {
        var encoding = field.Encoding;

        // width validity
        if (encoding.BitWidth is { } w && (w <= 0 || w > BitMath.MaxScalarBits))
        {
            sink.Add(new Diagnostic(DiagnosticCodes.InvalidBitWidth, Severity.Error,
                $"Field '{field.Name}' declares a serialized width of {w} bits; the supported range is 1..{BitMath.MaxScalarBits}.",
                target));
        }

        if (encoding.AlignmentBits is { } a && a <= 0)
        {
            sink.Add(new Diagnostic(DiagnosticCodes.InvalidAlignment, Severity.Error,
                $"Field '{field.Name}' declares an alignment of {a} bits; it must be positive.",
                target));
        }

        if (!ctx.Project.Types.TryGet(field.TypeId, out var type) || type is null) return;

        // width vs range
        if (encoding.BitWidth is { } bw && bw > 0 && bw <= BitMath.MaxScalarBits)
        {
            switch (type)
            {
                case ParameterType p when p.Range is { } range:
                    var required = BitMath.RequiredBits(range, encoding.Transform);
                    if (bw < required)
                        sink.Add(new Diagnostic(DiagnosticCodes.WidthTooSmallForRange, Severity.Error,
                            $"Field '{field.Name}' asks for {bw} bit(s) but its range {range} needs {required}. Widen the field or add a Transform.",
                            target));
                    break;

                case EnumType e when e.Members.Count > 0:
                    var enumRequired = BitMath.RequiredBits(e, encoding.Transform);
                    if (bw < enumRequired)
                        sink.Add(new Diagnostic(DiagnosticCodes.EnumMemberDoesNotFit, Severity.Error,
                            $"Field '{field.Name}' asks for {bw} bit(s) but enum '{e.Name}' needs {enumRequired} to hold every member.",
                            target));
                    break;
            }
        }

        // default value in range
        if (field.DefaultValue is not null && type is ParameterType pt && pt.Range is { } r)
        {
            if (TryToDecimal(field.DefaultValue, out var value) && !r.Contains(value))
                sink.Add(new Diagnostic(DiagnosticCodes.DefaultValueOutOfRange, Severity.Error,
                    $"Field '{field.Name}' has default {value}, which is outside the declared range {r}.",
                    target));
        }
    }

    private static bool TryToDecimal(object value, out decimal d)
    {
        try
        {
            d = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            d = 0m;
            return false;
        }
    }
}
