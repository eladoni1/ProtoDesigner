using ProtoDesigner.Core.Layout;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.Application;

/// <summary>
/// Pushes a type's wire representation onto every field binding that references it, anywhere in the
/// project.
/// </summary>
/// <remarks>
/// The layout engine only ever reads <see cref="FieldEncoding"/> on a binding, which is what keeps it
/// language- and policy-free. But the product rule is that a type serialises the same way everywhere, so
/// this is the one place that reconciles the two: edit the type, and every occurrence follows.
///
/// The scale is derived, never typed by hand: given a range and a width, the finest factor that still
/// covers the range is <c>(max - min) / (2^bits - 1)</c>, with the offset pinned to the range minimum.
/// </remarks>
public static class WireEncodingPropagator
{
    /// <summary>Applies every type's wire settings across the whole project. Returns the number of bindings changed.</summary>
    public static int ApplyAll(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var changed = 0;

        foreach (var binding in AllBindings(project))
        {
            if (!project.Types.TryGet(binding.TypeId, out var type) || type is null) continue;
            if (Apply(type, binding)) changed++;
        }
        return changed;
    }

    /// <summary>Applies one type's wire settings to every binding that uses it.</summary>
    public static int Apply(Project project, TypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(type);

        var changed = 0;
        foreach (var binding in AllBindings(project))
        {
            if (binding.TypeId != type.Id) continue;
            if (Apply(type, binding)) changed++;
        }
        return changed;
    }

    /// <summary>
    /// Writes the type's representation onto a single binding. Returns true if anything changed.
    /// Structs and arrays are skipped: their size is the sum of what they are built from, all the way
    /// down to primitives and enums, so they have no width of their own to set.
    /// </summary>
    public static bool Apply(TypeDefinition type, FieldBinding binding)
    {
        if (type is not IWireSized sized) return false;

        var naturalBits = type switch
        {
            ParameterType p => p.Kind.NaturalBits(),
            EnumType e => e.UnderlyingKind.NaturalBits(),
            _ => 0,
        };
        if (naturalBits == 0) return false;

        var bits = sized.WireBits ?? naturalBits;
        var range = type switch
        {
            ParameterType p => p.Range,
            EnumType e => e.MemberRange,
            _ => null,
        };

        var transform = ResolveTransform(type, sized, range, bits, naturalBits);

        var encoding = binding.Encoding;
        var same = encoding.BitWidth == bits
                   && encoding.AllowBitPacking
                   && Equals(encoding.Transform, transform);
        if (same) return false;

        var updated = encoding.Clone();
        updated.BitWidth = bits;
        updated.AllowBitPacking = true;   // every field packs on the wire
        updated.Transform = transform;
        binding.Encoding = updated;
        return true;
    }

    /// <summary>
    /// Works out the value-to-wire mapping for a type, honouring an explicit offset/scale when the user
    /// has set one and deriving the tightest fit otherwise.
    /// </summary>
    /// <remarks>
    /// The subtle case is a floating-point host sent as an <em>integer</em>. An integer wire code cannot
    /// carry a fractional value directly, so such a field needs a scale even at full width — a
    /// <c>double</c> in 8 wire bytes still has to map 64 bits of integer onto its declared range. Only a
    /// <see cref="WireForm.Float"/> wire skips scaling, because there the IEEE bit pattern passes through
    /// untouched and scaling would corrupt it.
    /// </remarks>
    public static ScalarTransform? ResolveTransform(
        TypeDefinition type, IWireSized sized, NumericRange? range, int bits, int naturalBits)
    {
        if (sized.WireForm == WireForm.Float) return null;

        // An explicit choice wins outright — that is the whole point of exposing it.
        if (sized.WireOffset is { } offset && sized.WireScale is { } scale && scale > 0m)
            return new ScalarTransform(offset, scale);

        // An enum is never scaled. Its members are identifiers, not samples of a continuous quantity, so
        // the wire code IS the member value — scaling would map Arming=1 onto code 2 and back through a
        // rounding step for no benefit. If the members do not fit the declared width that is a validation
        // error (PD0021), not something to paper over with a factor.
        if (type is EnumType) return null;

        if (range is not { } r || r.IsConstant) return null;

        var hostIsFloat = type is ParameterType { Kind: PrimitiveKind.F32 or PrimitiveKind.F64 };
        if (!hostIsFloat && bits >= naturalBits) return null;   // integer host at full width is exact

        // A signed wire keeps zero at zero, so negatives stay negative and the value stays readable in a
        // packet dump. It pays for that with one bit of sign, halving the codes on each side.
        if (sized.WireForm == WireForm.Signed)
        {
            var magnitude = Math.Max(Math.Abs(r.Min), Math.Abs(r.Max));
            var codes = bits >= 64 ? long.MaxValue : (1L << (bits - 1)) - 1;
            if (codes <= 0) return null;
            return new ScalarTransform(0m, magnitude / codes);
        }

        return new ScalarTransform(r.Min, WireSizePolicy.FittedScale(r, bits, hostIsFloat));
    }

    /// <summary>Every binding in the project: message fields and struct members alike.</summary>
    public static IEnumerable<FieldBinding> AllBindings(Project project)
    {
        foreach (var bus in project.Buses)
            foreach (var message in bus.Messages)
                foreach (var field in message.Fields)
                    yield return field;

        foreach (var type in project.Types.All)
            if (type is StructType s)
                foreach (var field in s.Fields)
                    yield return field;
    }
}
