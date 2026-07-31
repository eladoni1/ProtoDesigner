namespace ProtoDesigner.Core.Tests.Validation;

public class EncodingFeasibilityTests
{
    [Fact]
    public void Width_too_small_for_declared_range_reports_PD0020()
    {
        var b = new ValidationBuilder();
        var temp = b.Prim("Temp", PrimitiveKind.U16, new NumericRange(0, 1000));
        b.NewMessage("M", ValidationBuilder.F("t", temp, FieldEncoding.Packed(4)));

        b.Run().Has(DiagnosticCodes.WidthTooSmallForRange);
    }

    [Fact]
    public void Width_that_fits_the_range_produces_no_PD0020()
    {
        var b = new ValidationBuilder();
        var temp = b.Prim("Temp", PrimitiveKind.U16, new NumericRange(0, 10));
        b.NewMessage("M", ValidationBuilder.F("t", temp, FieldEncoding.Packed(4)));

        b.Run().HasNo(DiagnosticCodes.WidthTooSmallForRange);
    }

    [Fact]
    public void Transform_offset_lets_a_narrower_width_fit()
    {
        var b = new ValidationBuilder();
        var temp = b.Prim("Temp", PrimitiveKind.U16, new NumericRange(1000, 1015));
        var enc = new FieldEncoding
        {
            BitWidth = 4,
            AllowBitPacking = true,
            Transform = new ScalarTransform(1000, 1),
        };
        b.NewMessage("M", ValidationBuilder.F("t", temp, enc));

        b.Run().HasNo(DiagnosticCodes.WidthTooSmallForRange);
    }

    [Fact]
    public void Enum_that_does_not_fit_in_declared_width_reports_PD0021()
    {
        var b = new ValidationBuilder();
        var mode = b.Enum("Mode", PrimitiveKind.U32, ("A", 0), ("B", 1), ("C", 2), ("D", 8));
        b.NewMessage("M", ValidationBuilder.F("m", mode, FieldEncoding.Packed(3)));

        b.Run().Has(DiagnosticCodes.EnumMemberDoesNotFit);
    }

    [Fact]
    public void Default_out_of_range_reports_PD0022()
    {
        var b = new ValidationBuilder();
        var count = b.Prim("Count", PrimitiveKind.U8, new NumericRange(0, 10));
        var field = ValidationBuilder.F("c", count);
        field.DefaultValue = 99;
        b.NewMessage("M", field);

        b.Run().Has(DiagnosticCodes.DefaultValueOutOfRange);
    }

    [Fact]
    public void Default_inside_range_produces_no_PD0022()
    {
        var b = new ValidationBuilder();
        var count = b.Prim("Count", PrimitiveKind.U8, new NumericRange(0, 10));
        var field = ValidationBuilder.F("c", count);
        field.DefaultValue = 7;
        b.NewMessage("M", field);

        b.Run().HasNo(DiagnosticCodes.DefaultValueOutOfRange);
    }

    [Fact]
    public void Invalid_bit_width_reports_PD0023()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.NewMessage("M", ValidationBuilder.F("x", u8, new FieldEncoding { BitWidth = 128 }));

        b.Run().Has(DiagnosticCodes.InvalidBitWidth);
    }

    [Fact]
    public void Non_positive_alignment_reports_PD0024()
    {
        var b = new ValidationBuilder();
        var u8 = b.Prim("u8", PrimitiveKind.U8);
        b.NewMessage("M", ValidationBuilder.F("x", u8, new FieldEncoding { AlignmentBits = 0 }));

        b.Run().Has(DiagnosticCodes.InvalidAlignment);
    }
}
