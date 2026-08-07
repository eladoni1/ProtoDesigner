using System.Text;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.Proto;

/// <summary>
/// Maps IR names and primitive kinds onto protobuf identifiers and scalar types. Kept separate from the
/// emitter so naming rules are testable on their own.
/// </summary>
internal static class ProtoNaming
{
    /// <summary>
    /// The protobuf scalar that carries a host kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrow integers widen: protobuf has no 8- or 16-bit scalar, so a <c>u8</c> becomes
    /// <c>uint32</c>. That is not a loss of information — the declared range travels alongside as a
    /// protovalidate constraint, which states the real limits more precisely than a width ever did.
    /// </para>
    /// <para>
    /// <see cref="PrimitiveKind.Char"/> becomes <c>uint32</c> rather than <c>string</c>: a protocol
    /// <c>char</c> is one byte of data, and calling it a string would promise UTF-8 validity that the
    /// wire never guaranteed.
    /// </para>
    /// </remarks>
    public static string ScalarType(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => "bool",
        PrimitiveKind.Char => "uint32",
        PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 => "int32",
        PrimitiveKind.U8 or PrimitiveKind.U16 or PrimitiveKind.U32 => "uint32",
        PrimitiveKind.I64 => "int64",
        PrimitiveKind.U64 => "uint64",
        PrimitiveKind.F32 => "float",
        PrimitiveKind.F64 => "double",
        _ => "uint32",
    };

    /// <summary>
    /// The protovalidate rule group for a scalar type — <c>uint32</c> constraints live under
    /// <c>uint32</c>, and so on. Null when the type has no numeric constraints worth stating.
    /// </summary>
    public static string? RuleGroup(PrimitiveKind kind) => ScalarType(kind) switch
    {
        "bool" => null,
        var scalar => scalar,
    };

    /// <summary>A message or enum name: PascalCase, which is protobuf's convention.</summary>
    public static string TypeName(string raw) => Sanitize(raw, pascal: true);

    /// <summary>A field name: lower_snake_case, which is protobuf's convention.</summary>
    public static string FieldName(string raw) => ToSnake(Sanitize(raw, pascal: false));

    /// <summary>
    /// An enum member: UPPER_SNAKE_CASE prefixed with the enum's name.
    /// </summary>
    /// <remarks>
    /// protobuf enum members share the enclosing scope exactly as C's do, so two enums with an
    /// <c>OK</c> member would collide inside one package. Prefixing is the convention protobuf's own
    /// style guide prescribes for that reason.
    /// </remarks>
    public static string EnumMemberName(string enumName, string memberName) =>
        ToSnake(Sanitize(enumName, pascal: true)).ToUpperInvariant()
        + "_" + ToSnake(Sanitize(memberName, pascal: true)).ToUpperInvariant();

    /// <summary>A package name: lowercase, dot-separated segments preserved.</summary>
    public static string Package(string raw)
    {
        var segments = raw.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Sanitize(s, pascal: false).ToLowerInvariant())
            .Where(s => s.Length > 0)
            .ToArray();

        return segments.Length == 0 ? "proto" : string.Join('.', segments);
    }

    /// <summary>A file name stem: lower_snake_case, protobuf's file convention.</summary>
    public static string FileStem(string raw) => ToSnake(Sanitize(raw, pascal: false)).ToLowerInvariant();

    /// <summary>camelCase or PascalCase to snake_case, leaving existing underscores alone.</summary>
    private static string ToSnake(string raw)
    {
        var sb = new StringBuilder(raw.Length + 4);

        for (var i = 0; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (char.IsUpper(ch) && i > 0 && raw[i - 1] != '_')
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Strips characters that are illegal in a protobuf identifier. protobuf has no keyword escape, but
    /// it also reserves very little, so a name only needs its illegal characters removed.
    /// </summary>
    private static string Sanitize(string raw, bool pascal)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "_";

        var sb = new StringBuilder(raw.Length);
        var upperNext = pascal;

        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sb.Append(upperNext ? char.ToUpperInvariant(ch) : ch);
                upperNext = false;
            }
            else if (sb.Length > 0)
            {
                upperNext = true;
            }
        }

        var result = sb.ToString();
        if (result.Length == 0) return "_";
        if (char.IsDigit(result[0])) result = "_" + result;
        return result;
    }
}
