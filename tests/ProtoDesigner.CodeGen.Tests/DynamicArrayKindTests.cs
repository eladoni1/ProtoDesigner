using ProtoDesigner.Core.Ir;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// That every <see cref="ArrayLength"/> variant survives the trip from model to IR to generated C.
/// </summary>
/// <remarks>
/// <para>
/// <c>LengthPrefixed</c> did not, for a long time, and nothing noticed: the shape had layout tests and no
/// corpus entry, so the IR builder was never asked to handle the synthetic <c>__length</c> node the engine
/// inserts, and it threw on the missing <c>TypeId</c>. The array editor could produce the shape; generation
/// then failed with an internal error naming a path no user had ever typed.
/// </para>
/// <para>
/// The suspicion at the time was that <c>Terminated</c> and <c>FillRemaining</c> were broken the same way.
/// They are not — only <c>LengthPrefixed</c> emits a synthetic node — and this class exists to say so with a
/// test rather than a guess. Their real gap is a different one: <see cref="IrArrayKind.Terminated"/> and
/// <see cref="IrArrayKind.FillRemaining"/> <em>decode</em> by asking the caller for a count instead of
/// scanning for the sentinel or consuming the remainder.
/// </para>
/// </remarks>
public class DynamicArrayKindTests
{
    private static (Project, Bus) Build(Func<FieldId, ArrayLength> length)
    {
        var p = new Project("Kinds");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = p.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));

        var count = new FieldBinding(FieldId.New(), "count", u8.Id);
        var payload = p.Types.Add(new ArrayType(TypeId.New(), "Payload", u8.Id, length(count.Id)));

        var bus = new Bus(BusId.New(), "Link", Transport.Ethernet);
        var m = new Message(MessageId.New(), "M") { WireId = 1 };
        m.Fields.Add(count);
        m.Fields.Add(new FieldBinding(FieldId.New(), "payload", payload.Id));
        m.Fields.Add(new FieldBinding(FieldId.New(), "trailer", u16.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>Every length rule, with the capacity it declares and the IR kind it should arrive as.</summary>
    public static TheoryData<IrArrayKind, int, Func<FieldId, ArrayLength>> Lengths() => new()
    {
        { IrArrayKind.Fixed, 8, _ => new ArrayLength.Fixed(8) },
        { IrArrayKind.CountFromField, 16, id => new ArrayLength.CountFromField(id, 16) },
        { IrArrayKind.LengthPrefixed, 16, _ => new ArrayLength.LengthPrefixed(8, 16) },
        { IrArrayKind.Terminated, 16, _ => new ArrayLength.Terminated(new byte[] { 0x00 }, 16) },
        { IrArrayKind.FillRemaining, 16, _ => new ArrayLength.FillRemaining(16) },
    };

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Every_array_length_rule_reaches_the_ir(
        IrArrayKind expected, int capacity, Func<FieldId, ArrayLength> length)
    {
        var (project, bus) = Build(length);

        var ir = new IrBuilder().Build(project, bus);

        var field = Assert.Single(ir.Messages[0].Fields, f => f.Path == "payload");
        Assert.NotNull(field.Array);
        Assert.Equal(expected, field.Array!.Kind);
        Assert.Equal(capacity, field.Array.MaxElements);
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Every_array_length_rule_reaches_the_c_generator(
        IrArrayKind expected, int capacity, Func<FieldId, ArrayLength> length)
    {
        _ = expected;
        var (project, bus) = Build(length);
        var ir = new IrBuilder().Build(project, bus);

        var files = new C.CGenerator().Generate(ir, new GeneratorOptions());

        var header = files.Files.Single(
            f => f.RelativePath.EndsWith("link.h", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("proto_M_ConvertToWire", header.Contents);
        Assert.Contains("proto_M_ConvertToHost", header.Contents);
        Assert.Contains($"i < {capacity}", header.Contents);
    }

    [Fact]
    public void A_length_prefix_is_framing_rather_than_a_field()
    {
        // The prefix occupies wire space and appears in the layout, but it is not a value the user
        // declared — so it must not become an IrField, or it would be counted twice and arrive untyped.
        var (project, bus) = Build(_ => new ArrayLength.LengthPrefixed(8, 16));
        var layout = new Core.Layout.LayoutEngine().Compute(project, bus, bus.Messages[0]);

        Assert.Single(layout.LengthPrefixes());
        Assert.DoesNotContain(layout.Values(), n => n.Path.EndsWith("__length", StringComparison.Ordinal));

        var ir = new IrBuilder().Build(project, bus);
        Assert.DoesNotContain(ir.Messages[0].Fields, f => f.Path.Contains("__length", StringComparison.Ordinal));

        // The information is not lost, only moved to where it belongs: on the array itself.
        Assert.Equal(8, ir.Messages[0].Fields.Single(f => f.Path == "payload").Array!.PrefixBits);
    }

    [Fact]
    public void A_count_field_still_indexes_correctly_past_a_length_prefix()
    {
        // The regression the skip protects: CountFieldIndex is a position in the flattened field list, so
        // emitting the prefix as a field would have shifted every index after it by one.
        var (project, bus) = Build(id => new ArrayLength.CountFromField(id, 16));
        var ir = new IrBuilder().Build(project, bus);

        var array = ir.Messages[0].Fields.Single(f => f.Path == "payload");
        var index = array.Array!.CountFieldIndex;

        Assert.NotNull(index);
        Assert.Equal("count", ir.Messages[0].Fields[index!.Value].Path);
    }
}
