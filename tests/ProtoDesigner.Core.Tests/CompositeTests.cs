using static ProtoDesigner.Core.Tests.ModelBuilder;

namespace ProtoDesigner.Core.Tests;

public class CompositeTests
{
    private readonly ModelBuilder _b = new();

    [Fact]
    public void A_struct_expands_inline_with_dotted_paths()
    {
        var header = _b.Struct("Header",
            F("messageId", _b.U8()),
            F("flags", _b.U8()));

        var layout = _b.Layout(
            F("header", header),
            F("payload", _b.U16()));

        layout.Order("header", "header.messageId", "header.flags", "payload");
        layout.At("header", 0, 16);
        layout.At("header.messageId", 0, 8);
        layout.At("header.flags", 8, 8);
        layout.At("payload", 16, 16);
        layout.Sized(32);
    }

    [Fact]
    public void Struct_members_carry_region_relative_offsets_not_struct_relative_ones()
    {
        var pair = _b.Struct("Pair", F("x", _b.U16()), F("y", _b.U16()));

        var layout = _b.Layout(
            F("lead", _b.U32()),
            F("point", pair));

        // 32 bits of lead, so the struct's members sit at 32 and 48 — absolute, not 0 and 16.
        layout.At("point", 32, 32);
        layout.At("point.x", 32, 16);
        layout.At("point.y", 48, 16);
    }

    [Fact]
    public void Structs_nest()
    {
        var inner = _b.Struct("Inner", F("a", _b.U8()), F("b", _b.U8()));
        var outer = _b.Struct("Outer", F("tag", _b.U8()), F("inner", inner));

        var layout = _b.Layout(F("outer", outer));

        layout.Order("outer", "outer.tag", "outer.inner", "outer.inner.a", "outer.inner.b");
        layout.At("outer", 0, 24);
        layout.At("outer.inner", 8, 16);
        layout.At("outer.inner.b", 16, 8);
        layout.Sized(24);
    }

    [Fact]
    public void The_same_struct_can_appear_twice_in_one_message()
    {
        var point = _b.Struct("Point", F("x", _b.U16()), F("y", _b.U16()));

        var layout = _b.Layout(F("from", point), F("to", point));

        layout.At("from.x", 0, 16);
        layout.At("to.x", 32, 16);
        layout.Sized(64);
    }

    [Fact]
    public void A_struct_can_be_aligned_as_a_unit()
    {
        var pair = _b.Struct("Pair", F("x", _b.U8()), F("y", _b.U8()));

        var layout = _b.Layout(
            F("lead", _b.U8()),
            F("pair", pair, FieldEncoding.AlignedTo(32)));

        layout.At("pair", 32, 16);
        layout.At("pair.x", 32, 8);
        layout.Sized(48);
    }

    [Fact]
    public void A_static_array_reports_a_stride_and_a_count()
    {
        var samples = _b.Array("Samples", _b.U16(), count: 4);

        var layout = _b.Layout(F("samples", samples));

        var node = layout["samples"];
        Assert.Equal(LayoutNodeKind.Array, node.Kind);
        Assert.Equal(16, node.ElementBits);
        Assert.Equal(4, node.ElementCount);
        Assert.Equal(64, node.BitWidth);
        layout.Sized(64);
    }

    [Fact]
    public void Array_children_describe_one_element_with_element_relative_offsets()
    {
        var point = _b.Struct("Point", F("x", _b.I32()), F("y", _b.I32()));
        var path = _b.Array("Path", point, count: 3);

        var layout = _b.Layout(F("lead", _b.U16()), F("path", path));

        layout.At("path", 16, 192);

        // The element tree is described once, relative to the start of an element.
        layout.At("path[]", 0, 64);
        layout.At("path[].x", 0, 32);
        layout.At("path[].y", 32, 32);
    }

    [Fact]
    public void A_string_is_an_array_of_chars()
    {
        var name = _b.Array("Name", _b.Char(), count: 8);

        var layout = _b.Layout(F("name", name));

        Assert.Equal(8, layout["name"].ElementBits);
        layout.Sized(64);
    }

    [Fact]
    public void An_enum_array_packs_per_element()
    {
        var mode = _b.Enum("Mode", PrimitiveKind.U32, ("Idle", 0), ("Fault", 10));
        var modes = _b.Array("Modes", mode, count: 6);

        // On an array binding, BitWidth is the width of one element.
        var layout = _b.Layout(F("modes", modes, FieldEncoding.Packed(4)));

        var node = layout["modes"];
        Assert.Equal(4, node.ElementBits);
        Assert.Equal(24, node.BitWidth);
        layout.Sized(24);
    }

    [Fact]
    public void Arrays_nest()
    {
        var row = _b.Array("Row", _b.U8(), count: 4);
        var grid = _b.Array("Grid", row, count: 3);

        var layout = _b.Layout(F("grid", grid));

        Assert.Equal(32, layout["grid"].ElementBits);
        Assert.Equal(96, layout["grid"].BitWidth);
        layout.At("grid[]", 0, 32);
        layout.At("grid[][]", 0, 8);
    }

    [Fact]
    public void A_struct_can_contain_an_array()
    {
        var samples = _b.Array("Samples", _b.U16(), count: 2);
        var block = _b.Struct("Block", F("id", _b.U8()), F("samples", samples));

        var layout = _b.Layout(F("block", block));

        layout.At("block", 0, 40);
        layout.At("block.samples", 8, 32);
        layout.Sized(40);
    }

    [Fact]
    public void A_directly_recursive_struct_is_rejected()
    {
        var node = _b.Struct("Node", F("value", _b.U8()));
        node.With(F("next", node));

        var error = _b.LayoutThrows(F("root", node));
        Assert.Contains("contains itself", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_indirectly_recursive_struct_is_rejected()
    {
        var a = _b.Struct("A", F("tag", _b.U8()));
        var b = _b.Struct("B", F("tag", _b.U8()));
        a.With(F("b", b));
        b.With(F("a", a));

        _b.LayoutThrows(F("root", a));
    }

    [Fact]
    public void An_array_of_itself_is_rejected()
    {
        var array = _b.Array("SelfArray", _b.U8(), count: 2);
        array.ElementTypeId = array.Id;

        _b.LayoutThrows(F("root", array));
    }
}
