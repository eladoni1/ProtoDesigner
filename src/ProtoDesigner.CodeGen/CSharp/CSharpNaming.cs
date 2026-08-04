using System.Text;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.CSharp;

/// <summary>
/// Maps IR names and primitive kinds onto legal, idiomatic C# identifiers and types. Kept separate from
/// the emitter so naming rules are testable on their own.
/// </summary>
internal static class CSharpNaming
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
        "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
        "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
        "internal","is","lock","long","namespace","new","null","object","operator","out","override",
        "params","private","protected","public","readonly","ref","return","sbyte","sealed","short",
        "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof",
        "uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
    };

    /// <summary>The C# storage type that holds a field's decoded value.</summary>
    /// <remarks>
    /// <see cref="PrimitiveKind.Char"/> maps to <c>byte</c> rather than <c>char</c>, matching the C++
    /// generator. A protocol <c>char</c> is one wire byte; C#'s <c>char</c> is a 16-bit UTF-16 code unit,
    /// so binding it here would silently change the width and make the two targets disagree.
    /// </remarks>
    public static string StorageType(PrimitiveKind kind) => kind switch
    {
        PrimitiveKind.Bool => "bool",
        PrimitiveKind.Char => "byte",
        PrimitiveKind.I8 => "sbyte",
        PrimitiveKind.U8 => "byte",
        PrimitiveKind.I16 => "short",
        PrimitiveKind.U16 => "ushort",
        PrimitiveKind.I32 => "int",
        PrimitiveKind.U32 => "uint",
        PrimitiveKind.I64 => "long",
        PrimitiveKind.U64 => "ulong",
        PrimitiveKind.F32 => "float",
        PrimitiveKind.F64 => "double",
        _ => "uint",
    };

    /// <summary>A class/enum type name: PascalCase, keyword-safe.</summary>
    public static string TypeName(string raw) => Sanitize(raw, pascal: true);

    /// <summary>
    /// A field name. C# convention is PascalCase for public members, which is why this differs from the
    /// C++ generator's camelCase — the two targets are idiomatic in their own language rather than
    /// identical to each other.
    /// </summary>
    public static string MemberName(string path)
    {
        var flattened = path.Replace(".", "_").Replace("[]", "");
        return Sanitize(flattened, pascal: true);
    }

    /// <summary>An enum member name: PascalCase, keyword-safe.</summary>
    public static string EnumMemberName(string raw) => Sanitize(raw, pascal: true);

    /// <summary>
    /// A member name that is legal inside <paramref name="enclosingType"/>. C# forbids a member sharing
    /// its enclosing type's name, so a <c>Status</c> field inside class <c>Status</c> is renamed rather
    /// than emitted as code that will not compile.
    /// </summary>
    public static string MemberNameIn(string enclosingType, string path)
    {
        var name = MemberName(path);
        return string.Equals(name, enclosingType, StringComparison.Ordinal) ? name + "_" : name;
    }

    /// <summary>
    /// Strips characters that are illegal in a C# identifier. Any run of illegal characters acts as a word
    /// separator and capitalises the next letter, so "sensor-array 2" becomes "SensorArray2". Underscores
    /// are legal and pass through, which is what keeps dotted IR paths readable after the caller rewrites
    /// '.' to '_'. Keywords are escaped with '@' rather than mangled, so the name the user chose survives.
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
        if (Keywords.Contains(result)) result = "@" + result;
        return result;
    }
}
