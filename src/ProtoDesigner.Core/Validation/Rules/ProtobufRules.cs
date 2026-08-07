using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Validation.Rules;

/// <summary>
/// Whether a message can be represented in protobuf's type system.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> in <see cref="Validator.DefaultRules"/>. A bit-packed, quantized message is
/// entirely correct — it is the thing this tool exists to design — and it must never fail ordinary
/// generation because one optional target cannot express it. These rules answer a different question:
/// "could this particular message also be exported as .proto?"
/// </para>
/// <para>
/// The point of asking at all is that protobuf silently accepts a lossy translation. Without a gate, a
/// user sets a field to 4 bits, watches the byte map agree, exports, and ships 2 bytes. Refusing with
/// the offending field named is the difference between a tool that is honest and one that is not.
/// </para>
/// </remarks>
public static class ProtobufRules
{
    /// <summary>The gate, as a rule set. Run it with <c>new Validator(ProtobufRules.All)</c>.</summary>
    public static IReadOnlyList<IValidationRule> All { get; } = new IValidationRule[]
    {
        new ProtoSubByteFieldRule(),
        new ProtoScaledFieldRule(),
        new ProtoEndiannessRule(),
        new ProtoDuplicateFieldNumberRule(),
    };

    /// <summary>Every leaf value node of a message, or nothing when the layout is impossible.</summary>
    /// <remarks>
    /// <para>
    /// Reads the computed layout rather than <see cref="FieldEncoding"/> directly, because a binding's
    /// width is frequently null and inherited from the message, bus or project. The layout is where those
    /// have been resolved, and it reaches leaves nested inside structs and arrays as well.
    /// </para>
    /// <para>
    /// <b>Leaves only.</b> <c>MessageLayout.Values()</c> also yields the struct and array nodes that
    /// contain them, and those carry the *aggregate* width — a struct holding a 4-bit field spans 52
    /// bits, which is not a whole number of bytes either. Reporting the container is not wrong so much as
    /// useless: it refuses the message while pointing at <c>header</c> rather than at
    /// <c>header.randomType</c>, and naming the offending field is the entire reason this gate exists
    /// instead of a flat "unsupported". A struct is representable in protobuf as a nested message; only
    /// its scalars can fail.
    /// </para>
    /// </remarks>
    internal static IEnumerable<(Bus Bus, Message Message, LayoutNode Node)> ValueNodes(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
            {
                var layout = ctx.TryLayout(bus, message, out _);
                if (layout is null) continue;   // an impossible layout is another rule's finding

                foreach (var node in layout.Values())
                    if (node.Kind is LayoutNodeKind.Parameter or LayoutNodeKind.Enum)
                        yield return (bus, message, node);
            }
    }
}

/// <summary>A field narrower than a byte, or not a whole number of bytes, has no protobuf equivalent.</summary>
/// <remarks>
/// Protobuf's smallest addressable unit is a byte and its integers are varints; there is no way to say
/// "four bits". Packing several such fields into one opaque <c>bytes</c> blob would compile, but the
/// blob is unreadable to every other protobuf consumer — which is the entire reason to reach for
/// protobuf — so it is refused rather than papered over.
/// </remarks>
public sealed class ProtoSubByteFieldRule : IValidationRule
{
    public string Code => DiagnosticCodes.ProtoSubByteField;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var (bus, message, node) in ProtobufRules.ValueNodes(ctx))
        {
            if (node.BitWidth % 8 == 0) continue;

            yield return new Diagnostic(Code, Severity.Error,
                $"Field '{node.Path}' is {node.BitWidth} bits. Protobuf has no sub-byte fields, so this "
                + "message cannot be exported as .proto. Widen it to a whole number of bytes, or keep "
                + "this message on the C target.",
                EntityPath.ForField(bus, message, node.Path));
        }
    }
}

/// <summary>
/// A scalar transform with a scale other than 1 is quantization, which protobuf cannot carry.
/// </summary>
/// <remarks>
/// <para>
/// The offset half of a transform is fine at any value, and that asymmetry is the whole rule. An offset
/// exists only to narrow the bit width — a 1000..1015 field is sent as codes 0..15 to fit in 4 bits.
/// Protobuf sends the value itself, so the offset is simply not needed, and the declared range survives
/// intact as a protovalidate constraint.
/// </para>
/// <para>
/// A scale is different because it is <em>lossy</em>. A float 0..100 quantized into 8 bits holds 256
/// distinct values; protobuf would carry the host value at full precision. The same number would then
/// round-trip differently through the C codec than through protobuf, and two peers reading the same
/// design would disagree about what was sent. That is not a translation, so it is refused.
/// </para>
/// </remarks>
public sealed class ProtoScaledFieldRule : IValidationRule
{
    public string Code => DiagnosticCodes.ProtoScaledField;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var (bus, message, node) in ProtobufRules.ValueNodes(ctx))
        {
            if (node.Transform.Scale == 1m) continue;

            yield return new Diagnostic(Code, Severity.Error,
                $"Field '{node.Path}' is quantized (one wire step = {node.Transform.Scale}). Protobuf "
                + "carries the value at full precision, so a protobuf peer and a C peer would disagree "
                + "about what was sent. Remove the scale, or keep this message on the C target.",
                EntityPath.ForField(bus, message, node.Path));
        }
    }
}

/// <summary>Protobuf fixes byte order by specification, so an explicit choice is discarded.</summary>
/// <remarks>
/// A warning rather than an error: nothing is lost in the data, only in the design's intent. Both peers
/// speak protobuf and will agree with each other regardless. It is reported because someone who
/// deliberately set big-endian deserves to know it stopped applying.
/// </remarks>
public sealed class ProtoEndiannessRule : IValidationRule
{
    public string Code => DiagnosticCodes.ProtoEndiannessIgnored;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var (bus, message, node) in ProtobufRules.ValueNodes(ctx))
        {
            // Only multi-byte fields have an observable byte order to lose.
            if (node.Endianness != Endianness.Big || node.BitWidth <= 8) continue;

            yield return new Diagnostic(Code, Severity.Warning,
                $"Field '{node.Path}' is big-endian. Protobuf fixes byte order in its own specification, "
                + "so the export drops this and both protobuf peers will still agree with each other.",
                EntityPath.ForField(bus, message, node.Path));
        }
    }
}

/// <summary>Two fields in one message must never share a protobuf field number.</summary>
/// <remarks>
/// A reused number is the one protobuf mistake with no recovery: an old peer decodes the new field into
/// the old one's slot and reports no error at all. Numbers are assigned once and kept, which is why they
/// are persisted on the model rather than derived from field order.
/// </remarks>
public sealed class ProtoDuplicateFieldNumberRule : IValidationRule
{
    public string Code => DiagnosticCodes.ProtoDuplicateFieldNumber;

    public IEnumerable<Diagnostic> Validate(ValidationContext ctx)
    {
        foreach (var bus in ctx.Project.Buses)
            foreach (var message in bus.Messages)
                foreach (var clash in Duplicates(message.Fields))
                    yield return new Diagnostic(Code, Severity.Error,
                        $"Fields {clash.Names} in '{message.Name}' all claim protobuf field number "
                        + $"{clash.Number}. A number identifies one field forever; reusing one makes an "
                        + "older peer decode the wrong field without reporting an error.",
                        EntityPath.ForMessage(bus, message));

        // Struct types own bindings too, and a struct becomes a nested protobuf message with its own
        // number space — so the same clash is possible there.
        foreach (var type in ctx.Project.Types.All)
        {
            if (type is not StructType structType) continue;

            foreach (var clash in Duplicates(structType.Fields))
                yield return new Diagnostic(Code, Severity.Error,
                    $"Fields {clash.Names} in struct '{structType.Name}' all claim protobuf field number "
                    + $"{clash.Number}.",
                    EntityPath.ForType(structType));
        }
    }

    private static IEnumerable<(int Number, string Names)> Duplicates(IEnumerable<FieldBinding> fields) =>
        fields
            .Where(f => f.ProtoFieldNumber is not null)
            .GroupBy(f => f.ProtoFieldNumber!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, string.Join(", ", g.Select(f => $"'{f.Name}'"))));
}
