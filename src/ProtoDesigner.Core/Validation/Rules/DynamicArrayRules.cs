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

/// <summary>
/// An array element that code generation cannot describe: another array, or a struct carrying one.
/// </summary>
/// <remarks>
/// <para>
/// Struct elements <em>are</em> supported. The layout engine flattens the element into
/// <c>items[].x</c> nodes and computes a stride, <see cref="Core.Ir.IrArrayInfo.ElementFields"/>
/// carries that through, and the C generator emits a member-by-member inner loop. This rule used to
/// refuse them outright, back when <c>IrArrayInfo</c> could describe an element only as a single scalar
/// kind and the fallback emitted <c>uint8_t</c> elements that compiled cleanly and encoded nothing like
/// the declared model.
/// </para>
/// <para>
/// What it refuses now is everything that still has no shape in the IR, and the blanket refusal used to
/// hide all of it. An element must have <b>one constant stride</b>, so:
/// </para>
/// <list type="bullet">
/// <item>an array <em>of</em> arrays has no single stride at all;</item>
/// <item>a <em>dynamic</em> array inside an element makes the stride depend on data, so every offset
/// after it would too;</item>
/// <item>a fixed array <em>of composites</em> inside an element has a constant stride, but describing it
/// would need a second level of nesting that <c>IrElementField</c> does not have — it names one value,
/// optionally repeated.</item>
/// </list>
/// <para>
/// A fixed array of primitives or enums inside an element is fine, and is the common case — a payload
/// buffer inside a channel record. So is a nested struct, at any depth.
/// </para>
/// <para>
/// All three are reported here rather than left to the builder, which is rule 5: the layout engine lays
/// every one of them out perfectly well, so they are wrong-but-computable, and a user meets them as a
/// diagnostic naming the member rather than an <see cref="InvalidOperationException"/> at generate time.
/// </para>
/// </remarks>
public sealed class ArrayOfCompositeElementRule : IValidationRule
{
    public string Code => DiagnosticCodes.ArrayOfCompositeElement;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var array in ctx.Project.Types.All.OfType<ArrayType>())
        {
            if (!ctx.Project.Types.TryGet(array.ElementTypeId, out var element) || element is null) continue;

            if (element is ArrayType)
            {
                yield return new Diagnostic(Code, Severity.Error,
                    $"Array '{array.Name}' has elements of type '{element.Name}', which is itself an array. "
                    + "An array element must have one fixed stride; put the inner array inside a struct "
                    + "and use that as the element instead.",
                    EntityPath.ForType(array));
                continue;
            }

            if (element is not StructType structElement) continue;

            foreach (var finding in Inspect(ctx, array, structElement, prefix: "", new HashSet<TypeId>()))
                yield return finding;
        }
    }

    /// <summary>
    /// Walks one element's members, reporting any that would give the element a stride the IR cannot
    /// describe. Recurses through nested structs, which are supported and may hide an offender.
    /// </summary>
    /// <remarks>
    /// <paramref name="seen"/> guards a recursive struct. That is already <c>PD0010</c>'s job and the
    /// engine rejects it too, but a validator must not hang on a model that is merely wrong.
    /// </remarks>
    private IEnumerable<Diagnostic> Inspect(
        ValidationContext ctx, ArrayType array, StructType element, string prefix, HashSet<TypeId> seen)
    {
        if (!seen.Add(element.Id)) yield break;

        foreach (var member in element.Fields)
        {
            if (!ctx.Project.Types.TryGet(member.TypeId, out var type) || type is null) continue;
            var path = prefix.Length == 0 ? member.Name : $"{prefix}.{member.Name}";

            switch (type)
            {
                case StructType nested:
                    foreach (var finding in Inspect(ctx, array, nested, path, seen)) yield return finding;
                    break;

                case ArrayType inner when inner.Length is not ArrayLength.Fixed:
                    yield return new Diagnostic(Code, Severity.Error,
                        $"Array '{array.Name}' has elements of type '{element.Name}', whose member "
                        + $"'{path}' is a variable-length array. Every element of '{array.Name}' has to "
                        + "be the same size, so a length that changes per element cannot be placed. Give "
                        + $"'{path}' a fixed count.",
                        EntityPath.ForType(array));
                    break;

                case ArrayType inner
                    when ctx.Project.Types.TryGet(inner.ElementTypeId, out var item)
                         && item is StructType or ArrayType:
                    yield return new Diagnostic(Code, Severity.Error,
                        $"Array '{array.Name}' has elements of type '{element.Name}', whose member "
                        + $"'{path}' is an array of '{item!.Name}'. Code generation can repeat a "
                        + "primitive or an enum inside an element, but not a composite. Flatten "
                        + $"'{item.Name}' into '{element.Name}', or drop one level of nesting.",
                        EntityPath.ForType(array));
                    break;
            }
        }

        seen.Remove(element.Id);
    }
}

/// <summary>
/// An array's declared minimum element count must be reachable: at least zero, and no more than its
/// capacity.
/// </summary>
/// <remarks>
/// The layout engine also refuses a minimum above the capacity, because a region cannot have
/// <c>MinBits &gt; MaxBits</c> — but it throws, and a user who typed 10 into a box that holds 8 deserves
/// a diagnostic pointing at the array, not an exception. This runs first and says which two numbers
/// disagree.
/// </remarks>
public sealed class ArrayMinCountRule : IValidationRule
{
    public string Code => DiagnosticCodes.ArrayMinCountUnreachable;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var array in ctx.Project.Types.All.OfType<ArrayType>())
        {
            var length = array.Length;

            if (length.MinimumCount < 0)
            {
                yield return new Diagnostic(Code, Severity.Error,
                    $"Array '{array.Name}' declares a minimum of {length.MinimumCount} elements; "
                    + "a minimum cannot be negative.",
                    EntityPath.ForType(array));
            }
            else if (length.MinimumCount > length.Capacity)
            {
                yield return new Diagnostic(Code, Severity.Error,
                    $"Array '{array.Name}' requires at least {length.MinimumCount} elements but holds at "
                    + $"most {length.Capacity}. No message can satisfy both.",
                    EntityPath.ForType(array));
            }
        }
    }
}
