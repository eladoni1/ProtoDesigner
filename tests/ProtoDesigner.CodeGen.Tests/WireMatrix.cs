using System.Globalization;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// The full cross-product of host kind, wire width, endianness and bit offset, as one project. Each case
/// becomes its own single-purpose message so a failure names the exact combination that broke.
/// </summary>
/// <remarks>
/// The point is to find bugs the hand-written corpus cannot: a width that only misbehaves when it
/// straddles a byte, a 64-bit field at a non-zero offset, a signed field one code away from its minimum.
/// The same cases drive the pure-C# round trip and the MSVC cross-check, so the two can never test
/// different things.
/// </remarks>
internal static class WireMatrix
{
    /// <summary>One (kind, width, endianness, lead-in) combination and the values it must carry exactly.</summary>
    internal sealed record Case(
        string Name,
        PrimitiveKind Kind,
        int Bits,
        Endianness Endianness,
        int LeadInBits,
        IReadOnlyList<long> Values)
    {
        public bool IsSigned => Kind is PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64;

        public override string ToString() => Name;
    }

    /// <summary>Widths worth trying: every sub-byte width, both sides of each byte boundary, and the extremes.</summary>
    private static readonly int[] CandidateWidths =
        { 1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 15, 16, 17, 23, 24, 25, 31, 32, 33, 47, 48, 49, 63, 64 };

    /// <summary>
    /// Bits of padding placed before the field under test. Zero keeps it byte-aligned (the writer's fast
    /// path); the others force it onto the bit-by-bit slow path and across byte boundaries.
    /// </summary>
    private static readonly int[] LeadIns = { 0, 1, 3, 7 };

    private static readonly PrimitiveKind[] Kinds =
    {
        PrimitiveKind.U8, PrimitiveKind.U16, PrimitiveKind.U32, PrimitiveKind.U64,
        PrimitiveKind.I8, PrimitiveKind.I16, PrimitiveKind.I32, PrimitiveKind.I64,
        PrimitiveKind.Bool, PrimitiveKind.Char,
    };

    public static IReadOnlyList<Case> Cases()
    {
        var cases = new List<Case>();

        foreach (var kind in Kinds)
        {
            var natural = kind.NaturalBits();

            foreach (var bits in CandidateWidths)
            {
                // A wire wider than the host cannot round-trip: the extra codes have nowhere to live.
                if (bits > natural) continue;

                foreach (var endian in new[] { Endianness.Little, Endianness.Big })
                {
                    foreach (var lead in LeadIns)
                    {
                        // Endianness only means anything for a whole-byte field on a byte boundary;
                        // everywhere else the writer packs MSB-first and the flag is inert. Skipping the
                        // duplicates keeps the matrix honest about what it is actually varying.
                        if (endian == Endianness.Big && (bits % 8 != 0 || lead % 8 != 0)) continue;

                        var name = $"{kind}_{bits}b_{endian}_at{lead}";
                        cases.Add(new Case(name, kind, bits, endian, lead, ValuesFor(kind, bits)));
                    }
                }
            }
        }

        return cases;
    }

    /// <summary>
    /// Boundary values for a width: both extremes, the values either side of zero, and one code in from
    /// each end. Off-by-one bugs in masking and sign extension live exactly here.
    /// </summary>
    private static IReadOnlyList<long> ValuesFor(PrimitiveKind kind, int bits)
    {
        // A bool carries one bit of information however wide its wire slot is — every generated struct
        // holds it in a native bool, which folds any non-zero to 1. Feeding it 14 would be testing a
        // round trip no generator can deliver.
        if (kind == PrimitiveKind.Bool) return new[] { 0L, 1L };

        var signed = kind is PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64;

        if (signed)
        {
            var max = bits >= 64 ? long.MaxValue : (1L << (bits - 1)) - 1;
            var min = bits >= 64 ? long.MinValue : -(1L << (bits - 1));
            // Clamped, because at 1 bit the representable set is just {-1, 0}: an unclamped +1 would be
            // testing that the codec stores a value the width cannot hold.
            return Distinct(new[] { min, min + 1, -1L, 0L, 1L, max - 1, max }
                .Where(v => v >= min && v <= max));
        }

        var top = bits >= 64 ? unchecked((long)ulong.MaxValue) : (1L << bits) - 1;
        return Distinct(new[] { 0L, 1L, top / 2, top - 1, top }
            .Where(v => bits >= 64 || (v >= 0 && v <= top)));
    }

    /// <summary>De-duplicates while keeping order — at 1 and 2 bits the boundaries collapse onto each other.</summary>
    private static IReadOnlyList<long> Distinct(IEnumerable<long> values)
    {
        var seen = new HashSet<long>();
        var result = new List<long>();
        foreach (var v in values)
            if (seen.Add(v)) result.Add(v);
        return result;
    }

    /// <summary>
    /// Builds one message per case. The lead-in is a separate packed field, so the field under test lands
    /// at exactly the intended bit offset rather than relying on alignment settings to place it.
    /// </summary>
    public static (Project Project, Bus Bus) Build(IReadOnlyList<Case> cases)
    {
        var p = new Project("WireMatrix");
        var bus = new Bus(BusId.New(), "Matrix", Transport.Ethernet);

        // One shared 1-bit filler type; the lead-in is expressed as N of them so any offset is reachable.
        var filler = p.Types.Add(new ParameterType(TypeId.New(), "Filler", PrimitiveKind.U8, new NumericRange(0, 1)));

        var typeCache = new Dictionary<PrimitiveKind, ParameterType>();

        foreach (var c in cases)
        {
            if (!typeCache.TryGetValue(c.Kind, out var type))
            {
                // No range: an unconstrained host means no transform is derived, so the wire code is the
                // value and the round trip is exact by construction.
                type = p.Types.Add(new ParameterType(TypeId.New(), $"T_{c.Kind}", c.Kind));
                typeCache[c.Kind] = type;
            }

            var m = new Message(MessageId.New(), c.Name);

            for (var i = 0; i < c.LeadInBits; i++)
                m.Fields.Add(new FieldBinding(FieldId.New(), $"lead{i}", filler.Id, FieldEncoding.Packed(1)));

            m.Fields.Add(new FieldBinding(FieldId.New(), "value", type.Id, new FieldEncoding
            {
                BitWidth = c.Bits,
                AllowBitPacking = true,
                Endianness = c.Endianness,
            }));

            bus.Messages.Add(m);
        }

        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>The value dictionary for one case, with the lead-in bits set so padding cannot hide a bug.</summary>
    public static Dictionary<string, object?> ValuesFor(Case c, long value)
    {
        var values = new Dictionary<string, object?>();
        // Alternating lead-in bits: an all-zero prefix would mask a writer that overshoots into it.
        for (var i = 0; i < c.LeadInBits; i++) values[$"lead{i}"] = (i % 2 == 0) ? 1 : 0;
        values["value"] = value;
        return values;
    }

    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
