using ProtoDesigner.CodeGen.Runtime;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Pins <see cref="WireLength"/> against what the reference codec actually writes.
/// </summary>
/// <remarks>
/// This is the test that makes the generated <c>OnWireLength</c> trustworthy. Both generators emit a
/// region walk mirroring <see cref="WireLength"/>; if that walk ever disagreed with the encoder, every
/// caller computing "CRC sits at length minus CRC size" would read the wrong bytes — and nothing else in
/// the suite would notice, because encode and decode would still agree with each other.
/// </remarks>
public class WireLengthTests
{
    private readonly ReferenceCodec _codec = new();

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Corpus.All()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void The_computed_length_matches_what_the_codec_encodes(string corpusName)
    {
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();   // one call — project and bus must come from the same instance
        var ir = new IrBuilder().Build(project, bus);

        foreach (var message in ir.Messages)
        {
            // Only fixed-size messages can be checked without inventing array contents; the variable
            // case gets its own test below, where the counts are chosen deliberately.
            if (!WireLength.IsFixedSize(message)) continue;

            var encoded = _codec.Encode(message, DefaultValues(message));

            Assert.Equal(encoded.Length, WireLength.FixedBytes(message));
            Assert.Equal(encoded.Length, WireLength.Bytes(message, _ => 0));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(32)]
    public void A_variable_message_measures_what_it_encodes_at_every_count(int count)
    {
        // Batch is count(u8) + payload[u8, up to 32] + crc(u16): a fixed prefix, a variable region and a
        // fixed suffix, which is the case where a naive "MaxBits / 8" is wrong for all but a full array.
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var message = ir.Messages.Single();

        var values = DefaultValues(message);
        values["count"] = count;
        values["payload"] = Enumerable.Range(0, count).Select(i => (object)(i & 0xFF)).ToList();

        var encoded = _codec.Encode(message, values);

        Assert.Equal(encoded.Length, WireLength.Bytes(message, _ => count));
    }

    [Fact]
    public void A_count_above_capacity_measures_the_truncated_length()
    {
        // The generated encoder stops at the declared capacity, so the length has to report what was
        // written rather than what was asked for — otherwise a trailing field's offset would be wrong
        // exactly when the caller over-filled the array.
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var message = ir.Messages.Single();
        var region = message.Regions.Single(r => r.Kind == IrRegionKind.Variable);

        Assert.Equal(
            WireLength.Bytes(message, _ => region.MaxElements),
            WireLength.Bytes(message, _ => region.MaxElements + 50));
    }

    [Fact]
    public void A_trailing_field_can_be_located_by_subtraction()
    {
        // The whole point of the length API: find a trailing field — a checksum, typically — from the
        // message length and the field's own wire size, with no offset stored anywhere.
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var message = ir.Messages.Single();
        var crc = message.Fields.Single(f => f.Path == "crc");

        const int count = 5;
        var values = DefaultValues(message);
        values["count"] = count;
        values["payload"] = Enumerable.Range(0, count).Select(i => (object)(i & 0xFF)).ToList();
        values["crc"] = 0xBEEF;

        var encoded = _codec.Encode(message, values);
        var crcOffset = WireLength.Bytes(message, _ => count) - (crc.BitWidth / 8);

        // Little-endian u16, so 0xBEEF is EF BE.
        Assert.Equal(0xEF, encoded[crcOffset]);
        Assert.Equal(0xBE, encoded[crcOffset + 1]);
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void Every_named_type_reports_a_wire_size(string corpusName)
    {
        // Every type the protocol names — primitive, enum or struct — has to state its wire size, since
        // that is now the whole of what the tool contributes towards locating a field by hand. Primitives
        // were the gap: they declare no C++ type, so they were being skipped entirely.
        var factory = Corpus.All().Single(c => c.Name == corpusName).Factory;
        var (project, bus) = factory();   // one call — project and bus must come from the same instance
        var ir = new IrBuilder().Build(project, bus);

        Assert.All(ir.Primitives, p => Assert.True(p.WireBits > 0, $"primitive '{p.Name}' has no wire size"));
        Assert.All(ir.Enums, e => Assert.True(e.WireBits > 0, $"enum '{e.Name}' has no wire size"));
        Assert.All(ir.Structs, s => Assert.True(s.WireBits > 0, $"struct '{s.Name}' has no wire size"));

        // Every primitive a field actually uses must be present, including ones reached only through a
        // struct member or an array element.
        Assert.NotEmpty(ir.Primitives);
    }

    [Fact]
    public void A_primitive_reached_only_through_a_struct_still_reports_its_size()
    {
        var ir = Corpus.BuildIr(Corpus.StructAndArray());

        // Header is built from u8 and u32; neither is a direct field of the message.
        var names = ir.Primitives.Select(p => p.Name).ToList();
        Assert.Contains("u8", names, StringComparer.Ordinal);
        Assert.Contains("u32", names, StringComparer.Ordinal);
    }

    [Fact]
    public void Asking_a_variable_message_for_a_fixed_length_is_refused()
    {
        var ir = Corpus.BuildIr(Corpus.DynamicArray());
        var message = ir.Messages.Single();

        Assert.Throws<InvalidOperationException>(() => WireLength.FixedBytes(message));
    }

    /// <summary>Zero for every field, which is enough to measure a length.</summary>
    private static Dictionary<string, object?> DefaultValues(IrMessage message)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var field in message.Fields)
        {
            values[field.Path] = field.Array is { } arr
                ? Enumerable.Range(0, arr.ElementCount ?? 0).Select(_ => (object)0).ToList()
                : 0;
        }

        return values;
    }
}
