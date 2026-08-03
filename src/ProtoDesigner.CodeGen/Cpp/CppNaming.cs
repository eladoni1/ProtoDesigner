using System.Text;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.Cpp;

/// <summary>
/// Maps IR names and primitive kinds onto legal, idiomatic C++ identifiers and types. Kept separate
/// from the emitter so naming rules are testable on their own.
/// </summary>
internal static class CppNaming
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "alignas","alignof","and","and_eq","asm","auto","bitand","bitor","bool","break","case","catch",
        "char","char16_t","char32_t","class","compl","const","constexpr","const_cast","continue",
        "decltype","default","delete","do","double","dynamic_cast","else","enum","explicit","export",
        "extern","false","float","for","friend","goto","if","inline","int","long","mutable","namespace",
        "new","noexcept","not","not_eq","nullptr","operator","or","or_eq","private","protected","public",
        "register","reinterpret_cast","return","short","signed","sizeof","static","static_assert",
        "static_cast","struct","switch","template","this","thread_local","throw","true","try","typedef",
        "typeid","typename","union","unsigned","using","virtual","void","volatile","wchar_t","while",
        "xor","xor_eq",
    };

    /// <summary>The C++ storage type that holds a field's decoded value.</summary>
    /// <remarks>
    /// <see cref="PrimitiveKind.Char"/> deliberately maps to <c>uint8_t</c> rather than <c>char</c>.
    /// Plain <c>char</c> has implementation-defined signedness — signed on MSVC and x86 GCC, unsigned on
    /// ARM — so the identical generated header would decode wire byte 254 as -2 on one target and 254 on
    /// another. A protocol type cannot mean two different things depending on who compiled it. Printing a
    /// generated string therefore needs a cast, which is a small price for a value that is the same
    /// everywhere; a field that genuinely wants a signed byte declares <see cref="PrimitiveKind.I8"/>.
    /// </remarks>
    public static string StorageType(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => "bool",
        PrimitiveKind.Char => "uint8_t",
        PrimitiveKind.I8 => "int8_t",
        PrimitiveKind.U8 => "uint8_t",
        PrimitiveKind.I16 => "int16_t",
        PrimitiveKind.U16 => "uint16_t",
        PrimitiveKind.I32 => "int32_t",
        PrimitiveKind.U32 => "uint32_t",
        PrimitiveKind.I64 => "int64_t",
        PrimitiveKind.U64 => "uint64_t",
        PrimitiveKind.F32 => "float",
        PrimitiveKind.F64 => "double",
        _ => "uint32_t",
    };

    /// <summary>A struct/enum type name: PascalCase, keyword-safe.</summary>
    public static string TypeName(string raw) => Sanitize(raw, pascal: true);

    /// <summary>A single member name: <c>messageId</c> → <c>messageId</c>.</summary>
    public static string MemberName(string path)
    {
        var flattened = path.Replace(".", "_").Replace("[]", "");
        return Sanitize(flattened, pascal: false);
    }

    /// <summary>
    /// An access path into a host struct, keeping the dots: <c>header.messageId</c> stays
    /// <c>header.messageId</c> so it addresses the nested member the generator declared.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="MemberName"/>, which flattens. Each segment is sanitised on its own
    /// so a name that needs escaping still gets it, but the structure survives.
    /// </remarks>
    public static string MemberPath(string path)
    {
        var segments = path.Replace("[]", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            ? "_"
            : string.Join(".", segments.Select(s => Sanitize(s, pascal: false)));
    }

    /// <summary>An enum member name, prefixed to avoid clashing with the enclosing scope.</summary>
    public static string EnumMemberName(string raw) => Sanitize(raw, pascal: true);

    /// <summary>
    /// Strips characters that are illegal in a C++ identifier. Any run of illegal characters acts as a
    /// word separator and capitalises the next letter, so "sensor-array 2" becomes "sensorArray2" (or
    /// "SensorArray2" when <paramref name="pascal"/> is set). Underscores are legal and pass through,
    /// which is what keeps dotted IR paths readable after the caller rewrites '.' to '_'.
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
        if (Keywords.Contains(result)) result += "_";
        return result;
    }
}
