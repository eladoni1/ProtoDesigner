using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application;

/// <summary>One place an array type is used as a field.</summary>
public sealed record ArrayUsage(Bus Bus, Message Message, FieldBinding Field);

/// <summary>
/// A field that could supply a dynamic array's element count.
/// </summary>
/// <param name="Bus">The bus owning the message this candidate lives in.</param>
/// <param name="Message">The message this candidate and the array both belong to.</param>
/// <param name="Field">The binding to point <see cref="ArrayLength.CountFromField"/> at.</param>
/// <param name="Path">
/// How to name it to a user — <c>count</c>, or <c>header.count</c> for a field inside an earlier struct.
/// </param>
/// <param name="WireBits">Its resolved wire width, which is what limits the count it can express.</param>
/// <param name="MaxCountable">The largest count this field can hold.</param>
/// <param name="IsWideEnough">
/// Whether <see cref="MaxCountable"/> reaches the array's declared capacity. A candidate that falls short
/// is still offered — narrowing the array or widening the field are both reasonable answers, and the
/// validator reports the mismatch either way — but it is offered with the shortfall visible.
/// </param>
public sealed record CountFieldCandidate(
    Bus Bus,
    Message Message,
    FieldBinding Field,
    string Path,
    int WireBits,
    ulong MaxCountable,
    bool IsWideEnough);

/// <summary>
/// Which fields could count a dynamic array's elements, and where that array is used.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the array editor because it is policy, not presentation: the answer has to agree with
/// <see cref="Core.Validation.Rules.DynamicArrayRule"/> and with what
/// <see cref="LayoutEngine"/> will accept, and a dialog is the one place in this codebase that cannot be
/// tested. Offering a field the engine then rejects is exactly the failure this avoids.
/// </para>
/// <para>
/// The awkward part is that <see cref="ArrayLength.CountFromField"/> stores a <see cref="FieldId"/> on the
/// <em>type</em>, and a type is project-wide. An array used by two messages can only point at a field in
/// one of them; the other reports <c>PD0030</c>. That is why <see cref="UsagesOf"/> exists — the caller has
/// to be able to say so before the user commits to it.
/// </para>
/// </remarks>
public static class CountFieldCandidates
{
    /// <summary>Every field, on every bus, whose type is <paramref name="arrayTypeId"/>.</summary>
    public static IReadOnlyList<ArrayUsage> UsagesOf(Project project, TypeId arrayTypeId)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Buses
            .SelectMany(bus => bus.Messages
                .SelectMany(message => message.Fields
                    .Where(f => f.TypeId == arrayTypeId)
                    .Select(f => new ArrayUsage(bus, message, f))))
            .ToList();
    }

    /// <summary>
    /// Every field that could count an array of <paramref name="capacity"/> elements, across each message
    /// that uses the array.
    /// </summary>
    /// <remarks>
    /// A candidate must be an unsigned integer laid out strictly before the array in the same message.
    /// Fields inside an <em>earlier struct</em> count, matching the engine, which walks a struct's members
    /// into its "seen" set as it lays the struct out. A struct appearing after the array does not.
    /// </remarks>
    public static IReadOnlyList<CountFieldCandidate> For(Project project, TypeId arrayTypeId, int capacity)
    {
        ArgumentNullException.ThrowIfNull(project);

        var found = new List<CountFieldCandidate>();

        foreach (var usage in UsagesOf(project, arrayTypeId))
            CollectBefore(project, usage, capacity, found);

        return found;
    }

    private static void CollectBefore(
        Project project, ArrayUsage usage, int capacity, List<CountFieldCandidate> sink)
    {
        foreach (var field in usage.Message.Fields)
        {
            // Strictly before: the array cannot be counted by itself or by anything after it.
            if (field.Id == usage.Field.Id) return;

            if (!project.Types.TryGet(field.TypeId, out var type) || type is null) continue;

            switch (type)
            {
                case ParameterType p:
                    if (Accept(p, field) is { } bits)
                        sink.Add(Build(usage, field, field.Name, bits, capacity));
                    break;

                case StructType s:
                    foreach (var inner in s.Fields)
                    {
                        if (!project.Types.TryGet(inner.TypeId, out var innerType) ||
                            innerType is not ParameterType ip) continue;
                        if (Accept(ip, inner) is not { } innerBits) continue;

                        sink.Add(Build(usage, inner, $"{field.Name}.{inner.Name}", innerBits, capacity));
                    }
                    break;
            }
        }
    }

    /// <summary>The field's wire width if it could hold a count, or null if its type rules it out.</summary>
    private static int? Accept(ParameterType type, FieldBinding field)
    {
        if (!type.Kind.IsIntegral() || type.Kind.IsSigned()) return null;

        var bits = field.Encoding.BitWidth ?? type.WireBits ?? type.Kind.NaturalBits();
        return bits is > 0 and <= BitMath.MaxScalarBits ? bits : null;
    }

    private static CountFieldCandidate Build(
        ArrayUsage usage, FieldBinding field, string path, int bits, int capacity)
    {
        var max = BitMath.MaxUnsigned(bits);
        return new CountFieldCandidate(
            usage.Bus, usage.Message, field, path, bits, max, max >= (ulong)capacity);
    }
}
