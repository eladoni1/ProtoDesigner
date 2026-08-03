using System.Globalization;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// The other half of the bug hunt: enums, arrays and quantized floats. Where <see cref="WireMatrix"/>
/// varies width and offset over plain scalars, this varies the things that sit between the host value and
/// the wire code — an enum's cast through its underlying type, an array's per-element stride, a float's
/// scale and offset.
/// </summary>
internal static class CompositeMatrix
{
    internal abstract record Case(string Name, int LeadInBits)
    {
        public override string ToString() => Name;
    }

    /// <summary>An enum at a given width. Members deliberately include aliases and non-contiguous values.</summary>
    internal sealed record EnumCase(
        string Name,
        int LeadInBits,
        PrimitiveKind Underlying,
        int Bits,
        IReadOnlyList<(string Name, long Value)> Members) : Case(Name, LeadInBits)
    {
        /// <summary>Every distinct member value must survive; an alias decodes to the first name for it.</summary>
        public IReadOnlyList<long> Values => Members.Select(m => m.Value).Distinct().ToList();
    }

    /// <summary>A fixed array whose elements are packed at a chosen width.</summary>
    internal sealed record ArrayCase(
        string Name,
        int LeadInBits,
        PrimitiveKind Element,
        int ElementBits,
        int Count) : Case(Name, LeadInBits);

    /// <summary>A floating-point host quantized onto an integer wire of the given width.</summary>
    internal sealed record QuantizedCase(
        string Name,
        int LeadInBits,
        PrimitiveKind Host,
        decimal Min,
        decimal Max,
        int Bits) : Case(Name, LeadInBits);

    /// <summary>A float sent as its raw IEEE-754 bit pattern.</summary>
    internal sealed record RawFloatCase(string Name, int LeadInBits, PrimitiveKind Host, int Bits)
        : Case(Name, LeadInBits);

    public static IReadOnlyList<Case> Cases()
    {
        var cases = new List<Case>();

        // ---- enums ------------------------------------------------------------------------------
        // Contiguous from zero, sparse, aliased, and starting well above zero — the four shapes that
        // exercise the width calculation differently.
        var memberSets = new (string Label, (string, long)[] Members)[]
        {
            ("dense", new[] { ("A", 0L), ("B", 1L), ("C", 2L), ("D", 3L) }),
            ("sparse", new[] { ("Idle", 0L), ("Arming", 1L), ("Running", 5L), ("Fault", 10L) }),
            ("aliased", new[] { ("Success", 0L), ("NoError", 0L), ("Warn", 1L), ("Fail", 2L) }),
            ("offset", new[] { ("First", 100L), ("Second", 101L), ("Third", 127L) }),
        };

        foreach (var (label, members) in memberSets)
        {
            var span = members.Max(m => m.Item2);
            var minBits = BitMath.BitsForUnsignedMax((ulong)span);

            foreach (var bits in new[] { minBits, minBits + 1, 8, 16, 32 }.Distinct().Where(b => b <= 32))
            {
                foreach (var lead in new[] { 0, 3 })
                    cases.Add(new EnumCase($"Enum_{label}_{bits}b_at{lead}", lead, PrimitiveKind.U32, bits, members));
            }
        }

        // ---- arrays -----------------------------------------------------------------------------
        foreach (var element in new[] { PrimitiveKind.U8, PrimitiveKind.U16, PrimitiveKind.I16, PrimitiveKind.I32 })
        {
            var natural = element.NaturalBits();
            foreach (var bits in new[] { 4, 8, natural }.Distinct().Where(b => b <= natural))
            {
                foreach (var lead in new[] { 0, 3 })
                    cases.Add(new ArrayCase($"Arr_{element}_{bits}b_x4_at{lead}", lead, element, bits, 4));
            }
        }

        // ---- signed hosts biased onto unsigned codes ---------------------------------------------
        // The wire's signedness follows the transform, not the host: these all bias non-negative, so the
        // field must be written and read unsigned despite the host type being signed.
        foreach (var host in new[] { PrimitiveKind.I8, PrimitiveKind.I16, PrimitiveKind.I32 })
        {
            var natural = host.NaturalBits();
            foreach (var bits in new[] { 4, 8, 12 }.Where(b => b <= natural))
            {
                foreach (var lead in new[] { 0, 3 })
                    cases.Add(new QuantizedCase($"Bias_{host}_{bits}b_at{lead}", lead, host, -100m, 100m, bits));
            }
        }

        // ---- quantized floats -------------------------------------------------------------------
        foreach (var host in new[] { PrimitiveKind.F32, PrimitiveKind.F64 })
        {
            foreach (var (min, max) in new[] { (-40m, 70m), (0m, 100m), (-1m, 1m), (1000m, 1015m) })
            {
                foreach (var bits in new[] { 4, 8, 12, 16 })
                {
                    var tag = $"{host}_{min}_{max}_{bits}b".Replace("-", "n").Replace(".", "_");
                    cases.Add(new QuantizedCase($"Q_{tag}", 0, host, min, max, bits));
                }
            }
        }

        // ---- raw floats -------------------------------------------------------------------------
        foreach (var lead in new[] { 0, 3 })
        {
            cases.Add(new RawFloatCase($"Raw_F32_at{lead}", lead, PrimitiveKind.F32, 32));
            cases.Add(new RawFloatCase($"Raw_F64_at{lead}", lead, PrimitiveKind.F64, 64));
        }

        return cases;
    }

    /// <summary>Builds one message per case, all on a single bus so one generated header covers them.</summary>
    public static (Project Project, Bus Bus) Build(IReadOnlyList<Case> cases)
    {
        var p = new Project("CompositeMatrix");
        var bus = new Bus(BusId.New(), "Composite", Transport.Ethernet);
        var filler = p.Types.Add(new ParameterType(TypeId.New(), "Filler", PrimitiveKind.U8, new NumericRange(0, 1)));

        foreach (var c in cases)
        {
            var m = new Message(MessageId.New(), c.Name);
            for (var i = 0; i < c.LeadInBits; i++)
                m.Fields.Add(new FieldBinding(FieldId.New(), $"lead{i}", filler.Id, FieldEncoding.Packed(1)));

            switch (c)
            {
                case EnumCase e:
                {
                    var type = p.Types.Add(new EnumType(TypeId.New(), $"E_{e.Name}", e.Underlying) { WireBits = e.Bits });
                    foreach (var (name, value) in e.Members) type.With(name, value);
                    m.Fields.Add(new FieldBinding(FieldId.New(), "value", type.Id,
                        new FieldEncoding { BitWidth = e.Bits, AllowBitPacking = true }));
                    break;
                }

                case ArrayCase a:
                {
                    var element = p.Types.Add(new ParameterType(TypeId.New(), $"El_{a.Name}", a.Element));
                    var array = p.Types.Add(new ArrayType(TypeId.New(), $"A_{a.Name}", element.Id,
                        new ArrayLength.Fixed(a.Count)));
                    m.Fields.Add(new FieldBinding(FieldId.New(), "value", array.Id,
                        new FieldEncoding { BitWidth = a.ElementBits, AllowBitPacking = true }));
                    break;
                }

                case QuantizedCase q:
                {
                    var range = new NumericRange(q.Min, q.Max);
                    var type = p.Types.Add(new ParameterType(TypeId.New(), $"Q_{q.Name}", q.Host, range)
                    {
                        WireForm = WireForm.Unsigned,
                        WireBits = q.Bits,
                    });
                    m.Fields.Add(new FieldBinding(FieldId.New(), "value", type.Id, new FieldEncoding
                    {
                        BitWidth = q.Bits,
                        AllowBitPacking = true,
                        Transform = new ScalarTransform(range.Min, BitMath.MinimumScale(range, q.Bits)),
                    }));
                    break;
                }

                case RawFloatCase r:
                {
                    var type = p.Types.Add(new ParameterType(TypeId.New(), $"R_{r.Name}", r.Host)
                    {
                        WireForm = WireForm.Float,
                        WireBits = r.Bits,
                    });
                    m.Fields.Add(new FieldBinding(FieldId.New(), "value", type.Id,
                        new FieldEncoding { BitWidth = r.Bits, AllowBitPacking = true }));
                    break;
                }
            }

            bus.Messages.Add(m);
        }

        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>Lead-in bits, alternating so a writer that overshoots into them is caught.</summary>
    public static Dictionary<string, object?> BaseValues(Case c)
    {
        var values = new Dictionary<string, object?>();
        for (var i = 0; i < c.LeadInBits; i++) values[$"lead{i}"] = (i % 2 == 0) ? 1 : 0;
        return values;
    }

    /// <summary>Boundary element values for an array of the given element kind and width.</summary>
    public static IReadOnlyList<long> ArrayElementValues(ArrayCase a)
    {
        var signed = a.Element is PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64;
        if (signed)
        {
            var max = (1L << (a.ElementBits - 1)) - 1;
            var min = -(1L << (a.ElementBits - 1));
            return new[] { min, -1L, 0L, max };
        }
        var top = a.ElementBits >= 64 ? long.MaxValue : (1L << a.ElementBits) - 1;
        return new[] { 0L, 1L, top / 2, top };
    }

    /// <summary>Sample points across a quantized range, including both endpoints.</summary>
    public static IReadOnlyList<decimal> QuantizedValues(QuantizedCase q)
    {
        var span = q.Max - q.Min;
        return new[]
        {
            q.Min,
            q.Min + span / 4m,
            q.Min + span / 2m,
            q.Max - span / 4m,
            q.Max,
        };
    }

    /// <summary>Values a raw float must carry bit-exactly, including ones with no short decimal form.</summary>
    public static IReadOnlyList<double> RawFloatValues(RawFloatCase r) => r.Host == PrimitiveKind.F32
        ? new[] { 0.0, 1.0, -1.0, 0.5, 3.5f, (double)float.MaxValue, (double)float.Epsilon }
        : new[] { 0.0, 1.0, -1.0, 0.5, 3.14159265358979, 1e-9, 1e9 };

    public static string Dec(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
