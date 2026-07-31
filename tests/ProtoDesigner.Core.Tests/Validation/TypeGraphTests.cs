namespace ProtoDesigner.Core.Tests.Validation;

public class TypeGraphTests
{
    [Fact]
    public void Recursive_struct_reports_PD0010()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var node = b.Struct("Node", ValidationBuilder.F("v", u8));
        node.With(ValidationBuilder.F("next", node));

        b.Run().Has(DiagnosticCodes.RecursiveType);
    }

    [Fact]
    public void Indirect_recursion_reports_PD0010()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        var a = b.Struct("A", ValidationBuilder.F("t", u8));
        var s = b.Struct("B", ValidationBuilder.F("t", u8));
        a.With(ValidationBuilder.F("b", s));
        s.With(ValidationBuilder.F("a", a));

        b.Run().Has(DiagnosticCodes.RecursiveType);
    }

    [Fact]
    public void Non_recursive_type_graph_produces_no_recursion_diagnostic()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.Struct("Header", ValidationBuilder.F("tag", u8));

        b.Run().HasNo(DiagnosticCodes.RecursiveType);
    }

    [Fact]
    public void Unknown_type_reference_from_a_field_reports_PD0011()
    {
        var b = new ValidationBuilder();
        b.NewMessage("M", new FieldBinding("mystery", TypeId.New()));

        b.Run().Has(DiagnosticCodes.UnknownTypeReference);
    }

    [Fact]
    public void Enum_with_no_members_reports_PD0012()
    {
        var b = new ValidationBuilder();
        b.AddType(new EnumType(TypeId.New(), "Empty", PrimitiveKind.U8));

        b.Run().Has(DiagnosticCodes.EnumWithoutMembers);
    }

    // Aliases are a deliberate idiom — C's `enum { SUCCESS = 0, NO_ERROR = 0 }` — so this states what
    // decoding will yield rather than implying the model is wrong.
    [Fact]
    public void Enum_with_aliased_member_value_reports_PD0013_as_info()
    {
        var b = new ValidationBuilder();
        b.Enum("Mode", PrimitiveKind.U8, ("Idle", 0), ("Alias", 0));

        var d = b.Run().Has(DiagnosticCodes.DuplicateEnumMember);
        Assert.Equal(Severity.Info, d.Severity);
        Assert.Contains("'Idle'", d.Message);
    }
}
