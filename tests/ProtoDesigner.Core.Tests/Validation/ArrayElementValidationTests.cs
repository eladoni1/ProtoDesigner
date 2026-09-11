using ProtoDesigner.Core.Ir;
using ProtoDesigner.Core.Layout;

namespace ProtoDesigner.Core.Tests.Validation;

/// <summary>
/// What may and may not be an array element (PD0036).
/// </summary>
/// <remarks>
/// This rule had no tests while it refused every composite element, which is how its behaviour could be
/// changed without anything going red. It matters more now that it refuses a narrower set: each shape it
/// still refuses is one the layout engine lays out happily and the IR builder then throws on, so a gap
/// here is an <see cref="InvalidOperationException"/> in front of a user instead of a diagnostic.
/// </remarks>
public class ArrayElementValidationTests
{
    /// <summary>
    /// Every refusal is paired with this: the engine lays the model out, so the shape really is
    /// wrong-but-computable and really does belong to the validator rather than to the engine.
    /// </summary>
    private static void LaysOutAnyway(ValidationBuilder b)
    {
        var bus = b.Project.Buses[0];
        var exception = Record.Exception(() => new LayoutEngine().Compute(b.Project, bus, bus.Messages[0]));
        Assert.True(exception is null,
            "The engine refused this too, so the validator is not the right place to report it: " +
            exception?.Message);
    }

    // ---- supported ------------------------------------------------------------------------------

    [Fact]
    public void A_struct_element_is_allowed()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var reading = b.Struct("Reading",
            ValidationBuilder.F("channel", u8), ValidationBuilder.F("value", u8));
        var readings = b.Array("Readings", reading, new ArrayLength.Fixed(4));
        b.NewMessage("M", ValidationBuilder.F("readings", readings));

        b.Run().HasNo(DiagnosticCodes.ArrayOfCompositeElement);
    }

    [Fact]
    public void A_fixed_primitive_array_inside_a_struct_element_is_allowed()
    {
        // The common shape this whole feature exists for: a payload buffer inside a channel record.
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var u32 = b.Prim("u32", PrimitiveKind.U32);
        var samples = b.Array("Samples", u8, new ArrayLength.Fixed(4));
        var block = b.Struct("Block",
            ValidationBuilder.F("channelId", u32), ValidationBuilder.F("samples", samples));
        var blocks = b.Array("Blocks", block, new ArrayLength.Fixed(2));
        b.NewMessage("M", ValidationBuilder.F("blocks", blocks));

        b.Run().NoErrors();
    }

    [Fact]
    public void A_nested_struct_inside_a_struct_element_is_allowed()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var inner = b.Struct("Inner", ValidationBuilder.F("x", u8));
        var outer = b.Struct("Outer",
            ValidationBuilder.F("i", inner), ValidationBuilder.F("y", u8));
        var items = b.Array("Items", outer, new ArrayLength.Fixed(2));
        b.NewMessage("M", ValidationBuilder.F("items", items));

        b.Run().NoErrors();
    }

    // ---- refused --------------------------------------------------------------------------------

    [Fact]
    public void An_array_of_arrays_reports_PD0036()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var row = b.Array("Row", u8, new ArrayLength.Fixed(4));
        var grid = b.Array("Grid", row, new ArrayLength.Fixed(2));
        b.NewMessage("M", ValidationBuilder.F("grid", grid));

        b.Run().Has(DiagnosticCodes.ArrayOfCompositeElement);
    }

    [Fact]
    public void A_variable_array_inside_a_struct_element_reports_PD0036()
    {
        // Every element has to be the same size; a per-element length makes the stride depend on data.
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var count = ValidationBuilder.F("n", u8);
        var payload = b.Array("Payload", u8, new ArrayLength.CountFromField(count.Id, 4));
        var element = b.Struct("El", count, ValidationBuilder.F("payload", payload));
        var items = b.Array("Items", element, new ArrayLength.Fixed(2));
        b.NewMessage("M", ValidationBuilder.F("items", items));

        var found = b.Run().Has(DiagnosticCodes.ArrayOfCompositeElement);
        Assert.Contains("payload", found.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_composite_array_inside_a_struct_element_reports_PD0036()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var inner = b.Struct("Inner", ValidationBuilder.F("x", u8));
        var list = b.Array("List", inner, new ArrayLength.Fixed(2));
        var element = b.Struct("El", ValidationBuilder.F("list", list));
        var items = b.Array("Items", element, new ArrayLength.Fixed(2));
        b.NewMessage("M", ValidationBuilder.F("items", items));

        var found = b.Run().Has(DiagnosticCodes.ArrayOfCompositeElement);
        Assert.Contains("list", found.Message, StringComparison.Ordinal);

        // The point of reporting it here rather than leaving it to the builder.
        LaysOutAnyway(b);
    }

    [Fact]
    public void A_composite_array_nested_two_structs_deep_still_reports_PD0036()
    {
        // The walk has to recurse, or a nested struct hides the offender from the rule.
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var leaf = b.Struct("Leaf", ValidationBuilder.F("x", u8));
        var list = b.Array("List", leaf, new ArrayLength.Fixed(2));
        var middle = b.Struct("Middle", ValidationBuilder.F("list", list));
        var element = b.Struct("El", ValidationBuilder.F("mid", middle));
        var items = b.Array("Items", element, new ArrayLength.Fixed(2));
        b.NewMessage("M", ValidationBuilder.F("items", items));

        var found = b.Run().Has(DiagnosticCodes.ArrayOfCompositeElement);
        Assert.Contains("mid.list", found.Message, StringComparison.Ordinal);
    }

    // ---- the promise the rule makes -------------------------------------------------------------

    [Fact]
    public void Anything_the_rule_allows_reaches_the_ir()
    {
        // The contract: a clean model generates. Without this the rule is only asserted against itself,
        // and the builder's own refusals could drift out from under it.
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var u32 = b.Prim("u32", PrimitiveKind.U32);
        var samples = b.Array("Samples", u8, new ArrayLength.Fixed(4));
        var inner = b.Struct("Inner", ValidationBuilder.F("x", u8));
        var element = b.Struct("El",
            ValidationBuilder.F("id", u32),
            ValidationBuilder.F("inner", inner),
            ValidationBuilder.F("samples", samples));
        var items = b.Array("Items", element, new ArrayLength.Fixed(3));
        b.NewMessage("M", ValidationBuilder.F("items", items));

        b.Run().NoErrors();

        var ir = new IrBuilder().Build(b.Project, b.Project.Buses[0]);
        var array = ir.Messages[0].Fields.Single(f => f.Path == "items").Array!;

        Assert.True(array.HasCompositeElement);
        Assert.Equal(
            new[] { "id", "inner.x", "samples" },
            array.ElementFields!.Select(m => m.Name).ToArray());
        Assert.Equal(4, array.ElementFields!.Single(m => m.Name == "samples").FixedArrayCount);
    }
}
