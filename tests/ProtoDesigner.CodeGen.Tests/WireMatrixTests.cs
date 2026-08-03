using ProtoDesigner.CodeGen.Runtime;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Drives every (host kind, wire width, endianness, bit offset) combination through the reference codec.
/// This is the bug hunt the hand-picked corpus cannot do: it is the widths either side of every byte
/// boundary, at every sub-byte offset, at their extreme values, that break masking and sign extension.
/// </summary>
public class WireMatrixTests
{
    private static readonly ReferenceCodec Codec = new();

    // Built once: the matrix is a few hundred messages and rebuilding it per case dominates the runtime.
    private static readonly Lazy<(ProtocolIr Ir, IReadOnlyList<WireMatrix.Case> Cases)> Built = new(() =>
    {
        var cases = WireMatrix.Cases();
        var (project, bus) = WireMatrix.Build(cases);
        return (new IrBuilder().Build(project, bus), cases);
    });

    public static TheoryData<string> AllCases()
    {
        var data = new TheoryData<string>();
        foreach (var c in WireMatrix.Cases()) data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllCases))]
    public void Every_value_round_trips_at_every_width_and_offset(string caseName)
    {
        var (ir, cases) = Built.Value;
        var c = cases.Single(x => x.Name == caseName);
        var msg = ir.Messages.Single(m => m.Name == caseName);

        foreach (var value in c.Values)
        {
            var bytes = Codec.Encode(msg, WireMatrix.ValuesFor(c, value));
            var decoded = Codec.Decode(msg, bytes);

            Assert.Equal((decimal)value, decoded["value"]);

            // The lead-in must survive too: a writer that overshoots its width would corrupt the bits
            // in front of it, and a value-only assertion would never notice.
            for (var i = 0; i < c.LeadInBits; i++)
                Assert.Equal(i % 2 == 0 ? 1m : 0m, decoded[$"lead{i}"]);
        }
    }

    /// <summary>
    /// A field must occupy exactly the width it declares. Encoding the all-ones value and the all-zeros
    /// value should differ in precisely that many bits — no more (the writer spilled) and no fewer (the
    /// writer truncated).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllCases))]
    public void A_field_occupies_exactly_its_declared_width(string caseName)
    {
        var (ir, cases) = Built.Value;
        var c = cases.Single(x => x.Name == caseName);
        var msg = ir.Messages.Single(m => m.Name == caseName);

        var low = Codec.Encode(msg, WireMatrix.ValuesFor(c, c.IsSigned ? -1L : 0L));
        var high = Codec.Encode(msg, WireMatrix.ValuesFor(c, c.IsSigned ? 0L : AllOnes(c.Bits)));

        Assert.Equal(low.Length, high.Length);

        var differing = 0;
        for (var i = 0; i < low.Length; i++)
            differing += System.Numerics.BitOperations.PopCount((uint)(byte)(low[i] ^ high[i]));

        // A bool only ever toggles its lowest bit, however wide the slot: the remaining bits are reserved
        // padding the value cannot reach. Every other kind must use all of what it declared.
        var expected = c.Kind == PrimitiveKind.Bool ? 1 : c.Bits;
        Assert.Equal(expected, differing);
    }

    private static long AllOnes(int bits) => bits >= 64 ? unchecked((long)ulong.MaxValue) : (1L << bits) - 1;

    /// <summary>
    /// The whole matrix must lay out to the size the widths imply. A message here is the lead-in bits plus
    /// the field, rounded up to a whole byte.
    /// </summary>
    [Fact]
    public void Every_message_in_the_matrix_lays_out_to_its_expected_size()
    {
        var (ir, cases) = Built.Value;

        foreach (var c in cases)
        {
            var msg = ir.Messages.Single(m => m.Name == c.Name);
            var expected = ((c.LeadInBits + c.Bits) + 7) / 8 * 8;
            Assert.Equal(expected, msg.MaxBits);
        }
    }

    [Fact]
    public void The_matrix_covers_what_it_claims_to()
    {
        var cases = WireMatrix.Cases();

        Assert.Contains(cases, c => c.Bits == 1);
        Assert.Contains(cases, c => c.Bits == 64);
        Assert.Contains(cases, c => c.Bits == 64 && c.LeadInBits > 0);          // 64 bits straddling bytes
        Assert.Contains(cases, c => c.Endianness == Endianness.Big);
        Assert.Contains(cases, c => c.IsSigned && c.Bits == 1);                 // a 1-bit signed field
        Assert.Contains(cases, c => c.Kind == PrimitiveKind.Bool);
        Assert.Contains(cases, c => c.Kind == PrimitiveKind.Char);

        // Enough breadth to be worth calling a matrix.
        Assert.True(cases.Count > 200, $"Only {cases.Count} cases — the matrix has shrunk.");
    }
}
