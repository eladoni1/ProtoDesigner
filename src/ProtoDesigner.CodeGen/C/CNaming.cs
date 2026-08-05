using System.Text;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.C;

/// <summary>
/// Maps IR names and primitive kinds onto legal, idiomatic C identifiers. Kept separate from the emitter
/// so naming rules are testable on their own.
/// </summary>
/// <remarks>
/// C has no namespaces, so the project's namespace becomes a <em>prefix</em> on every emitted symbol.
/// That is not a workaround: a header that declares an unprefixed <c>Header</c> struct is a landmine in
/// any translation unit that includes two protocols, and C gives no other way to keep them apart.
/// </remarks>
internal static class CNaming
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C
        "auto","break","case","char","const","continue","default","do","double","else","enum","extern",
        "float","for","goto","if","inline","int","long","register","restrict","return","short","signed",
        "sizeof","static","struct","switch","typedef","union","unsigned","void","volatile","while",
        "_Bool","_Complex","_Imaginary","bool","true","false",
        // C++ as well, because the header has to compile as C++ too. A field called 'class' is legal C
        // and would break every C++ consumer of the same header.
        "asm","catch","class","delete","dynamic_cast","explicit","export","friend","mutable","namespace",
        "new","operator","private","protected","public","reinterpret_cast","static_cast","template",
        "this","throw","try","typeid","typename","using","virtual","wchar_t","nullptr","constexpr",
        "decltype","noexcept","alignas","alignof","thread_local","and","or","not","xor","bitand","bitor",
        "compl","and_eq","or_eq","xor_eq","not_eq",
    };

    /// <summary>The C storage type that holds a field's decoded value.</summary>
    /// <remarks>
    /// <see cref="PrimitiveKind.Char"/> deliberately maps to <c>uint8_t</c> rather than <c>char</c>.
    /// Plain <c>char</c> has implementation-defined signedness — signed on MSVC and x86 GCC, unsigned on
    /// ARM — so the identical generated header would decode wire byte 254 as -2 on one target and 254 on
    /// another. A protocol type cannot mean two different things depending on who compiled it.
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

    /// <summary>The symbol prefix derived from the generator's namespace option.</summary>
    public static string Prefix(string ns) => Sanitize(ns, pascal: false).ToLowerInvariant();

    /// <summary>A struct or enum type name: <c>proto_Telemetry</c>.</summary>
    public static string TypeName(string prefix, string raw) => Join(prefix, Sanitize(raw, pascal: true));

    /// <summary>
    /// An enum member: <c>proto_Mode_Idle</c>. C enum members share the enclosing scope, so the type name
    /// is part of the member's name or two enums with an <c>Ok</c> member cannot coexist.
    /// </summary>
    public static string EnumMemberName(string prefix, string enumName, string memberName) =>
        Join(prefix, Sanitize(enumName, pascal: true) + "_" + Sanitize(memberName, pascal: true));

    /// <summary>A function name: <c>proto_Telemetry_ConvertToWire</c>.</summary>
    public static string FunctionName(string prefix, string typeName, string verb) =>
        Join(prefix, Sanitize(typeName, pascal: true) + "_" + verb);

    /// <summary>
    /// A compile-time constant, shouted as C convention wants: <c>PROTO_TELEMETRY_MAX_BYTES</c>.
    /// </summary>
    public static string MacroName(string prefix, string typeName, string suffix)
    {
        var body = Sanitize(typeName, pascal: true) + "_" + suffix;
        return Join(prefix, body).ToUpperInvariant();
    }

    /// <summary>A single struct member name: <c>messageId</c>. Not prefixed — it is already scoped by its struct.</summary>
    public static string MemberName(string path)
    {
        var flattened = path.Replace(".", "_").Replace("[]", "");
        return Sanitize(flattened, pascal: false);
    }

    /// <summary>
    /// An access path into a host struct, keeping the dots: <c>header.messageId</c> stays
    /// <c>header.messageId</c> so it addresses the nested member the generator declared.
    /// </summary>
    public static string MemberPath(string path)
    {
        var segments = path.Replace("[]", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0
            ? "_"
            : string.Join(".", segments.Select(s => Sanitize(s, pascal: false)));
    }

    private static string Join(string prefix, string body) =>
        string.IsNullOrEmpty(prefix) ? body : $"{prefix}_{body}";

    /// <summary>
    /// Strips characters that are illegal in a C identifier. Any run of illegal characters acts as a word
    /// separator and capitalises the next letter, so "sensor-array 2" becomes "sensorArray2" (or
    /// "SensorArray2" when <paramref name="pascal"/> is set). Keywords get a trailing underscore — C has
    /// no escape syntax, so the name has to change.
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
