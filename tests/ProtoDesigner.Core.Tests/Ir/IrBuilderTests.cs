using ProtoDesigner.Core.Ir;

namespace ProtoDesigner.Core.Tests.Ir;

public class IrBuilderTests
{
    [Fact]
    public void A_scalar_message_produces_one_fixed_region_and_one_field()
    {
        var project = new Project("P");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var bus = new Bus("B", Transport.Ethernet);
        var msg = new Message("M");
        msg.Fields.Add(new FieldBinding("value", u8.Id));
        bus.Messages.Add(msg);
        project.Buses.Add(bus);

        var ir = new IrBuilder().Build(project, bus);

        var m = Assert.Single(ir.Messages);
        Assert.Equal("M", m.Name);
        Assert.Single(m.Regions);
        Assert.Equal(IrRegionKind.Fixed, m.Regions[0].Kind);
        var f = Assert.Single(m.Fields);
        Assert.Equal(IrFieldKind.Scalar, f.Kind);
        Assert.Equal(PrimitiveKind.U8, f.Primitive);
        Assert.Equal(0, f.BitOffset);
        Assert.Equal(8, f.BitWidth);
    }

    [Fact]
    public void A_struct_is_flattened_and_members_use_dotted_paths()
    {
        var project = new Project("P");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u32 = project.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding("id", u8.Id), new FieldBinding("ts", u32.Id)));

        var bus = new Bus("B", Transport.Ethernet);
        var msg = new Message("M");
        msg.Fields.Add(new FieldBinding("header", header.Id));
        bus.Messages.Add(msg);
        project.Buses.Add(bus);

        var m = new IrBuilder().Build(project, bus).Messages.Single();

        Assert.Equal(new[] { "header.id", "header.ts" }, m.Fields.Select(f => f.Path));
        Assert.All(m.Fields, f => Assert.Equal(IrFieldKind.Scalar, f.Kind));
    }

    [Fact]
    public void Enums_are_hoisted_into_a_single_top_level_list_and_referenced_by_index()
    {
        var project = new Project("P");
        var mode = project.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
            .With("Idle", 0).With("Run", 5));

        var bus = new Bus("B", Transport.Ethernet);
        var m1 = new Message("A");
        m1.Fields.Add(new FieldBinding("mode", mode.Id, FieldEncoding.Packed(4)));
        var m2 = new Message("B");
        m2.Fields.Add(new FieldBinding("mode", mode.Id, FieldEncoding.Packed(4)));
        bus.Messages.Add(m1); bus.Messages.Add(m2);
        project.Buses.Add(bus);

        var ir = new IrBuilder().Build(project, bus);

        var e = Assert.Single(ir.Enums);
        Assert.Equal("Mode", e.Name);
        Assert.Equal(2, e.Members.Count);

        foreach (var msg in ir.Messages)
        {
            var f = msg.Fields.Single();
            Assert.Equal(IrFieldKind.EnumRef, f.Kind);
            Assert.Equal(0, f.EnumIndex);
            Assert.Equal(4, f.BitWidth);
        }
    }

    [Fact]
    public void A_dynamic_array_becomes_a_variable_region_and_the_array_field_points_at_its_count_field()
    {
        var project = new Project("P");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));

        var count = new FieldBinding("count", u8.Id);
        var samples = project.Types.Add(new ArrayType(TypeId.New(), "Samples", u8.Id,
            new ArrayLength.CountFromField(count.Id, 10)));

        var bus = new Bus("B", Transport.Ethernet);
        var msg = new Message("M");
        msg.Fields.Add(count);
        msg.Fields.Add(new FieldBinding("samples", samples.Id));
        bus.Messages.Add(msg);
        project.Buses.Add(bus);

        var m = new IrBuilder().Build(project, bus).Messages.Single();

        Assert.Equal(2, m.Regions.Count);
        Assert.Equal(IrRegionKind.Fixed, m.Regions[0].Kind);
        Assert.Equal(IrRegionKind.Variable, m.Regions[1].Kind);
        Assert.Equal(0, m.Regions[1].CountFieldIndex);   // "count" is the first flattened field

        var arr = m.Fields.Single(f => f.Kind == IrFieldKind.Array);
        Assert.Equal(IrArrayKind.CountFromField, arr.Array!.Kind);
        Assert.Equal(0, arr.Array.CountFieldIndex);
        Assert.Equal(8, arr.Array.ElementBits);
        Assert.Equal(10, arr.Array.MaxElements);
    }

    [Fact]
    public void A_static_array_reports_the_count_and_stride()
    {
        var project = new Project("P");
        var u16 = project.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));
        var samples = project.Types.Add(new ArrayType(TypeId.New(), "Samples", u16.Id, new ArrayLength.Fixed(4)));

        var bus = new Bus("B", Transport.Ethernet);
        var msg = new Message("M");
        msg.Fields.Add(new FieldBinding("samples", samples.Id));
        bus.Messages.Add(msg);
        project.Buses.Add(bus);

        var m = new IrBuilder().Build(project, bus).Messages.Single();

        var arr = m.Fields.Single(f => f.Kind == IrFieldKind.Array);
        Assert.Equal(IrArrayKind.Fixed, arr.Array!.Kind);
        Assert.Equal(4, arr.Array.ElementCount);
        Assert.Equal(4, arr.Array.MaxElements);
        Assert.Equal(16, arr.Array.ElementBits);
    }

    [Fact]
    public void Wire_id_and_transport_flow_through()
    {
        var project = new Project("P");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var bus = new Bus("Main", Transport.Uart);
        var msg = new Message("Ping") { WireId = 7 };
        msg.Fields.Add(new FieldBinding("id", u8.Id));
        bus.Messages.Add(msg);
        project.Buses.Add(bus);

        var ir = new IrBuilder().Build(project, bus);

        Assert.Equal(Transport.Uart, ir.Transport);
        Assert.Equal(7, ir.Messages.Single().WireId);
    }

    [Fact]
    public void A_count_field_inside_a_struct_still_resolves_to_a_flattened_index()
    {
        var project = new Project("P");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var count = new FieldBinding("count", u8.Id);
        var header = project.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding("tag", u8.Id), count));
        var samples = project.Types.Add(new ArrayType(TypeId.New(), "Samples", u8.Id,
            new ArrayLength.CountFromField(count.Id, 4)));

        var bus = new Bus("B", Transport.Ethernet);
        var msg = new Message("M");
        msg.Fields.Add(new FieldBinding("header", header.Id));
        msg.Fields.Add(new FieldBinding("samples", samples.Id));
        bus.Messages.Add(msg);
        project.Buses.Add(bus);

        var m = new IrBuilder().Build(project, bus).Messages.Single();
        // Flattened order: header.tag (0), header.count (1), samples (2)
        Assert.Equal(new[] { "header.tag", "header.count", "samples" }, m.Fields.Select(f => f.Path));
        var samplesField = m.Fields[2];
        Assert.Equal(1, samplesField.Array!.CountFieldIndex);
        Assert.Equal(1, m.Regions[1].CountFieldIndex);
    }
}
