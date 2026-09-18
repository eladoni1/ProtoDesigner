using System.Globalization;
using System.Text;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Core.Merge;

/// <summary>
/// A string capturing one entity's own state — everything it owns that is not a child entity.
/// </summary>
/// <remarks>
/// <para>
/// The merge asks one question of every entity, three times over: did this change between the baseline
/// and the local copy, and between the baseline and the remote one? Comparing a rendered state string is
/// how it answers, because the alternative — field-by-field equality on six model classes — is the same
/// work with more places to forget a property.
/// </para>
/// <para>
/// <b>A property missing from here is silent data loss</b>, not a cosmetic gap: an edit the merge cannot
/// see is an edit it will happily discard as "unchanged". <c>EntityStateCoverageTests</c> exists for that
/// reason and mutates every property of every entity in turn, asserting that the state moves.
/// </para>
/// <para>
/// Children are deliberately excluded. A message's fields are entities in their own right and are merged
/// as such, so folding them in here would make every field edit look like a message edit and turn two
/// people touching different fields of one message into a conflict.
/// </para>
/// </remarks>
internal static class EntityState
{
    /// <summary>Separates parts so that "a" + "bc" cannot collide with "ab" + "c".</summary>
    private const char Sep = '\u001F';

    public static string Of(Message message) => Join(
        message.Name,
        message.WireId,
        message.Description,
        Of(message.Options),
        string.Join(",", message.Routes.Select(r => $"{r.From.Value}>{r.To.Value}")));

    public static string Of(FieldBinding field) => Join(
        field.Name,
        field.TypeId.Value,
        field.Description,
        field.DefaultValue,
        field.ProtoFieldNumber,
        Of(field.Encoding));

    public static string Of(Bus bus) => Join(bus.Name, bus.Transport, Of(bus.Options));

    public static string Of(Module module) => Join(module.Name);

    public static string Of(TypeDefinition type) => type switch
    {
        ParameterType p => Join("parameter", p.Name, p.Description, p.Kind, Of(p.Range),
            p.WireBits, p.WireForm, p.WireOffset, p.WireScale),

        EnumType e => Join("enum", e.Name, e.Description, e.UnderlyingKind, e.IsFlags, e.Synthetic,
            e.WireBits, e.WireForm, e.WireOffset, e.WireScale,
            // Members are part of the enum rather than entities of their own: nothing references one by
            // id, so there is nothing to merge them against.
            string.Join(",", e.Members.Select(m => $"{m.Name}={m.Value}"))),

        StructType s => Join("struct", s.Name, s.Description,
            // The member *list* only; each binding's own state is merged as a field entity.
            string.Join(",", s.Fields.Select(f => f.Id.Value))),

        ArrayType a => Join("array", a.Name, a.Description, a.ElementTypeId.Value, Of(a.Length)),

        _ => Join("unknown", type.Name),
    };

    private static string Of(LayoutOptions o) => Join(
        o.Endianness, o.BitOrder, o.DefaultAlignmentBits, o.PackingMode, o.PadToByteBoundary);

    private static string Of(FieldEncoding e) => Join(
        e.BitWidth, e.Endianness, e.BitOrder, e.AllowBitPacking, e.AlignmentBits, Of(e.Transform));

    private static string Of(ScalarTransform? t) =>
        t is null ? "~" : Join(t.Value.Offset, t.Value.Scale);

    private static string Of(NumericRange? r) =>
        r is null ? "~" : Join(r.Value.Min, r.Value.Max);

    private static string Of(ArrayLength length) => length switch
    {
        ArrayLength.Fixed f => Join("fixed", f.Count),
        ArrayLength.CountFromField c => Join("count", c.CountFieldId.Value, c.MaxCount, c.MinCount),
        ArrayLength.LengthPrefixed l => Join("prefix", l.PrefixBits, l.MaxCount, l.MinCount),
        ArrayLength.Terminated t => Join("term", Convert.ToHexString(t.Sentinel.ToArray()), t.MaxCount, t.MinCount),
        ArrayLength.FillRemaining r => Join("fill", r.MaxCount, r.MinCount),
        _ => Join("unknown-length", length.GetType().Name),
    };

    private static string Join(params object?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            sb.Append(part switch
            {
                null => "~",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => part.ToString(),
            });
            sb.Append(Sep);
        }
        return sb.ToString();
    }
}
