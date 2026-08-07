using System.Globalization;
using System.Text;
using ProtoDesigner.Core.Ir;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.Proto;

/// <summary>
/// Emits a <c>.proto</c> schema for a protocol, optionally carrying the declared ranges and capacities
/// as <a href="https://buf.build/docs/protovalidate">protovalidate</a> constraints.
/// </summary>
/// <remarks>
/// <para>
/// <b>This produces a different wire format, not a second encoding of ours.</b> Protobuf is
/// tag-length-value with varints; a message exported here and the same message through the C target do
/// not interoperate and are not the same bytes. The target exists so one schema definition can serve two
/// consumers — the embedded link on the C codec, a backend or dashboard on protobuf — without anyone
/// maintaining a second hand-written definition that drifts.
/// </para>
/// <para>
/// Because of that, it deliberately emits no wire sizes and no <c>OnWireLength</c>. Publishing our byte
/// counts beside protobuf's would be precisely the lie the compatibility gate exists to prevent.
/// </para>
/// <para>
/// Only messages that pass that gate should ever reach this generator; the caller narrows the scope with
/// <c>ProtobufCompatibility</c>. What survives the narrowing is representable, so this class does not
/// re-check — one gate in one place.
/// </para>
/// </remarks>
public sealed class ProtoGenerator : IProtocolGenerator
{
    public string Id => "proto";

    public string DisplayName => "Protobuf schema (.proto — a different wire format)";

    /// <summary>Option key: emit protovalidate constraints alongside the fields.</summary>
    public const string ProtovalidateOption = "protovalidate";

    public IReadOnlyList<GeneratorOption> Options { get; } = new[]
    {
        new GeneratorOption(
            ProtovalidateOption,
            "protovalidate constraints",
            "Carry declared ranges and array capacities as buf.validate options. Without them a .proto "
            + "keeps only the field types, and every limit you designed is lost. Requires the consumer "
            + "to resolve the buf/validate/validate.proto import.",
            GeneratorOptionKind.Flag,
            Default: "true"),
    };

    /// <summary>Protobuf cannot express a sub-byte field or a quantized one, so some messages have no export.</summary>
    public bool CoversEveryMessage => false;

    public GeneratedFileSet Generate(ProtocolIr ir, GeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(ir);
        return Generate(new[] { ir }, options);
    }

    public GeneratedFileSet Generate(IReadOnlyList<ProtocolIr> buses, GeneratorOptions options)
    {
        ArgumentNullException.ThrowIfNull(buses);
        if (buses.Count == 0) return GeneratedFileSet.Empty;
        options ??= new GeneratorOptions();

        var files = new List<GeneratedFile>
        {
            new(TypesFileName(options), EmitTypesFile(buses, options)),
        };

        foreach (var ir in buses)
            files.Add(new($"{ProtoNaming.FileStem(ir.BusName)}.proto", EmitBusFile(ir, options)));

        if (options.IncludeReadme)
            files.Add(new("README.md", EmitReadme(buses, options)));

        return new GeneratedFileSet(files);
    }

    private static bool UseValidate(GeneratorOptions options) =>
        options.Flag(ProtovalidateOption, fallback: true);

    private static string Package(GeneratorOptions options) => ProtoNaming.Package(options.Namespace);

    /// <summary>
    /// The shared declarations file. Types are project-wide, so a struct used by two buses is one message
    /// declared once and imported — two definitions of one name in a package would not compile.
    /// </summary>
    private static string TypesFileName(GeneratorOptions options) =>
        $"{ProtoNaming.FileStem(options.Namespace)}_types.proto";

    // ---- shared types --------------------------------------------------------------------------

    private static string EmitTypesFile(IReadOnlyList<ProtocolIr> buses, GeneratorOptions options)
    {
        var body = new StringBuilder();

        // Deduplicate by name across buses: the same project type reaches several of them and would
        // otherwise be declared once per bus.
        var seenEnums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in buses.SelectMany(b => b.Enums))
            if (seenEnums.Add(e.Name)) EmitEnum(body, e);

        var seenStructs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ir in buses)
            foreach (var s in ir.Structs)
                if (seenStructs.Add(s.Name)) EmitStruct(body, ir, s, options);

        return Compose($"{buses[0].ProjectName} — shared type declarations", body, options);
    }

    /// <summary>
    /// Puts the header on a finished body, importing protovalidate only if the body actually uses it.
    /// </summary>
    /// <remarks>
    /// The import cannot be decided up front: a file of plain structs with no declared ranges needs no
    /// constraints, and protoc reports an unused import as a warning. Composing after the fact is the
    /// simplest way to be sure the two agree.
    /// </remarks>
    private static string Compose(string title, StringBuilder body, GeneratorOptions options,
        string? extraImport = null)
    {
        var text = body.ToString();
        var sb = new StringBuilder();
        EmitHeader(sb, title, options, extraImport, needsValidate: text.Contains("buf.validate.", StringComparison.Ordinal));
        sb.Append(text);
        return sb.ToString();
    }

    private static void EmitEnum(StringBuilder sb, IrEnum e)
    {
        var name = ProtoNaming.TypeName(e.Name);

        sb.AppendLine($"// {(e.IsFlags ? "Flag set" : "Enumeration")} '{e.Name}'.");
        sb.AppendLine($"enum {name} {{");

        // proto3 requires the zero value to be first and to exist. A declared member with value 0 serves;
        // otherwise an UNSPECIFIED entry is added, which is protobuf's own convention for "not set".
        if (e.Members.All(m => m.Value != 0))
            sb.AppendLine($"  {ProtoNaming.EnumMemberName(e.Name, "Unspecified")} = 0;");

        foreach (var m in e.Members.OrderBy(m => m.Value == 0 ? 0 : 1))
            sb.AppendLine($"  {ProtoNaming.EnumMemberName(e.Name, m.Name)} = "
                + $"{m.Value.ToString(CultureInfo.InvariantCulture)};");

        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitStruct(StringBuilder sb, ProtocolIr ir, IrStruct s, GeneratorOptions options)
    {
        sb.AppendLine($"// Struct '{s.Name}'.");
        sb.AppendLine($"message {ProtoNaming.TypeName(s.Name)} {{");
        EmitMembers(sb, ir, s.Members, options);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // ---- one bus -------------------------------------------------------------------------------

    private static string EmitBusFile(ProtocolIr ir, GeneratorOptions options)
    {
        var body = new StringBuilder();
        foreach (var m in ir.Messages) EmitMessage(body, ir, m, options);

        // The shared types file is imported only when this bus actually references something in it;
        // protoc warns about an import nothing uses.
        var needsTypes = ir.Enums.Count > 0 || ir.Structs.Count > 0;

        return Compose($"{ir.ProjectName} — bus '{ir.BusName}' ({ir.Transport})", body, options,
            needsTypes ? TypesFileName(options) : null);
    }

    private static void EmitMessage(StringBuilder sb, ProtocolIr ir, IrMessage m, GeneratorOptions options)
    {
        sb.Append($"// Message '{m.Name}'");
        if (m.WireId is { } w) sb.Append($" (wire id {w} on the native transport)");
        sb.AppendLine(".");

        sb.AppendLine($"message {ProtoNaming.TypeName(m.Name)} {{");
        EmitMembers(sb, ir, m.Members, options);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the host-shape members. Structs stay nested messages rather than being flattened, because
    /// protobuf has no concept of wire order to flatten into — the shape the user declared is the shape.
    /// </summary>
    private static void EmitMembers(StringBuilder sb, ProtocolIr ir,
        IReadOnlyList<IrMember> members, GeneratorOptions options)
    {
        // An assigned number always wins, because it is the one already-deployed peers agreed to. The
        // ordinal is only a fallback for a project that has never been through the assign command, and it
        // steps past any number already claimed so the two schemes cannot collide mid-message.
        var claimed = members.Select(m => m.ProtoFieldNumber).OfType<int>().ToHashSet();
        var ordinal = 1;

        foreach (var member in members)
        {
            int number;
            if (member.ProtoFieldNumber is { } assigned)
            {
                number = assigned;
            }
            else
            {
                while (claimed.Contains(ordinal)) ordinal++;
                number = ordinal++;
            }

            var name = ProtoNaming.FieldName(member.Name);
            var type = MemberType(ir, member);

            if (member.Note is not null) sb.AppendLine($"  // {member.Note}");

            var repeated = member.ArrayCapacity is not null ? "repeated " : "";
            var constraints = UseValidate(options) ? Constraints(ir, member) : null;
            var suffix = constraints is null ? ";" : $" [\n{constraints}\n  ];";

            sb.AppendLine($"  {repeated}{type} {name} = {number}{suffix}");

            // A self-describing array's count is redundant in protobuf: `repeated` carries its own
            // length, and a second copy could disagree with it.
            if (member.NeedsCountMember)
                sb.AppendLine($"  // '{name}' carries its own count on the native wire; "
                    + "protobuf's repeated length replaces it.");
        }
    }

    private static string MemberType(ProtocolIr ir, IrMember member) => member.Kind switch
    {
        IrMemberKind.EnumRef when member.EnumIndex is { } ei => ProtoNaming.TypeName(ir.Enums[ei].Name),
        IrMemberKind.StructRef when member.StructIndex is { } si => ProtoNaming.TypeName(ir.Structs[si].Name),
        _ => ProtoNaming.ScalarType(member.Primitive),
    };

    /// <summary>
    /// The protovalidate options for a member, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// This is the reason the target is worth having. Without it a 1000..1015 field is bare
    /// <c>uint32</c> and a 32-element array is bare <c>repeated</c> — every limit the user designed is
    /// gone. The constraint states the real limits, and states them more precisely than a bit width ever
    /// did, since it survives protobuf's varint widening.
    /// </remarks>
    private static string? Constraints(ProtocolIr ir, IrMember member)
    {
        var rules = new List<string>();

        if (member.ArrayCapacity is { } capacity)
        {
            // protobuf's `repeated` carries no length of its own, so without these the capacity — and any
            // floor the user declared — is simply gone from the schema.
            var min = member.ArrayMinCount ?? 0;

            // A fixed-count array is the case where the two ends coincide; it needs no separate branch.
            // With no declared floor an empty array is legal, and `min_items: 0` would be noise.
            rules.Add(min > 0
                ? $"(buf.validate.field).repeated = {{ min_items: {min}, max_items: {capacity} }}"
                : $"(buf.validate.field).repeated.max_items = {capacity}");
        }

        // A rule on a repeated field describes each element, not the list, so it belongs under `items`.
        // protovalidate enforces this and refuses to compile the *whole message* when a scalar rule sits
        // directly on a repeated field — protoc accepts it happily, since the option is well-formed and
        // resolves, so nothing short of a runtime notices.
        var scope = member.ArrayCapacity is not null ? "repeated.items." : "";

        if (member.Kind == IrMemberKind.EnumRef)
        {
            // Rejects a value outside the declared set, which is what an enum meant on the native wire
            // and what protobuf otherwise lets through as an unknown number.
            rules.Add($"(buf.validate.field).{scope}enum.defined_only = true");
        }
        else if (member.Range is { } range && !range.IsConstant
                 && ProtoNaming.RuleGroup(member.Primitive) is { } group
                 && Narrows(range, group))
        {
            rules.Add($"(buf.validate.field).{scope}{group}.gte = {Number(range.Min, member.Primitive)}");
            rules.Add($"(buf.validate.field).{scope}{group}.lte = {Number(range.Max, member.Primitive)}");
        }

        return rules.Count == 0 ? null : string.Join(",\n", rules.Select(r => "    " + r));
    }

    /// <summary>
    /// Whether a range says anything the protobuf scalar does not already say.
    /// </summary>
    /// <remarks>
    /// Compared against the <em>protobuf</em> type's span, not the host's, and the difference matters.
    /// Protobuf has no 8- or 16-bit scalar, so a <c>u16</c> arrives as <c>uint32</c> and its 0..65535
    /// limit becomes real information that the type no longer carries — worth stating. A <c>u32</c>'s
    /// full span, by contrast, is exactly <c>uint32</c>'s own, and emitting it would be noise dressed up
    /// as a designed constraint.
    /// </remarks>
    private static bool Narrows(NumericRange range, string protoScalar)
    {
        var (min, max) = protoScalar switch
        {
            "uint32" => (0m, (decimal)uint.MaxValue),
            "int32" => (int.MinValue, (decimal)int.MaxValue),
            "uint64" => (0m, (decimal)ulong.MaxValue),
            "int64" => (long.MinValue, (decimal)long.MaxValue),
            // A float has no useful span to compare against; a declared range is always worth stating.
            _ => (decimal.MinValue, decimal.MaxValue),
        };

        return range.Min > min || range.Max < max;
    }

    /// <summary>
    /// A range bound as a protobuf literal. Integer targets get an integer, since <c>uint32.gte = 0.0</c>
    /// is a type error rather than a rounding question.
    /// </summary>
    private static string Number(decimal value, PrimitiveKind kind)
    {
        var isFloat = ProtoNaming.ScalarType(kind) is "float" or "double";
        if (!isFloat) return decimal.Truncate(value).ToString(CultureInfo.InvariantCulture);

        var text = ((double)value).ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
    }

    // ---- boilerplate ---------------------------------------------------------------------------

    private static void EmitHeader(StringBuilder sb, string title, GeneratorOptions options,
        string? extraImport, bool needsValidate)
    {
        sb.AppendLine("// -----------------------------------------------------------------------------");
        sb.AppendLine($"// {title}");
        sb.AppendLine("// Generated by ProtoDesigner. Do not edit; regenerate from the .pdproj instead.");
        sb.AppendLine("//");
        sb.AppendLine("// THIS IS A DIFFERENT WIRE FORMAT from the C target. Protobuf is tag-length-value");
        sb.AppendLine("// with varints, so these bytes are not the bytes the generated C codec produces and");
        sb.AppendLine("// the two do not interoperate. Only messages whose encodings protobuf can express");
        sb.AppendLine("// are exported here; anything bit-packed or quantized stays on the C target.");
        sb.AppendLine("// -----------------------------------------------------------------------------");
        sb.AppendLine();
        sb.AppendLine("syntax = \"proto3\";");
        sb.AppendLine();
        sb.AppendLine($"package {Package(options)};");
        sb.AppendLine();

        if (needsValidate)
            sb.AppendLine("import \"buf/validate/validate.proto\";");
        if (extraImport is not null)
            sb.AppendLine($"import \"{extraImport}\";");
        if (needsValidate || extraImport is not null)
            sb.AppendLine();
    }

    private static string EmitReadme(IReadOnlyList<ProtocolIr> buses, GeneratorOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {buses[0].ProjectName} — generated protobuf schema");
        sb.AppendLine();
        sb.AppendLine("Generated by ProtoDesigner. Regenerate rather than editing by hand.");
        sb.AppendLine();
        sb.AppendLine("## This is not the same wire format as the C target");
        sb.AppendLine();
        sb.AppendLine("Protobuf is tag-length-value with varints. A message encoded from this schema is a");
        sb.AppendLine("different sequence of bytes from the same message through the generated C codec, and the");
        sb.AppendLine("two cannot decode each other. Both describe the same *data*; only one can be on any given");
        sb.AppendLine("link.");
        sb.AppendLine();
        sb.AppendLine("The point of having both is one definition with two consumers: the embedded link keeps the");
        sb.AppendLine("C codec, while a backend or dashboard speaks protobuf, and nobody maintains a second");
        sb.AppendLine("hand-written schema that drifts from the first.");
        sb.AppendLine();
        sb.AppendLine("Messages using sub-byte fields or quantized values are **not** here — protobuf cannot");
        sb.AppendLine("represent them, so they were withheld rather than exported as something subtly different.");
        sb.AppendLine();

        if (UseValidate(options))
        {
            sb.AppendLine("## protovalidate");
            sb.AppendLine();
            sb.AppendLine("Ranges and array capacities are carried as `buf.validate` constraints, which is what");
            sb.AppendLine("keeps the limits you designed from being lost — protobuf's own types say only");
            sb.AppendLine("\"uint32\", not \"1000 to 1015\". Your build needs to resolve");
            sb.AppendLine("`buf/validate/validate.proto`, and the consumer needs a protovalidate runtime to");
            sb.AppendLine("enforce them; they are checked where the message is handled, not on the wire.");
            sb.AppendLine();
        }

        sb.AppendLine("| File | Contents |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| `{TypesFileName(options)}` | Shared enums and structs — project-wide, so declared once |");
        foreach (var ir in buses)
            sb.AppendLine($"| `{ProtoNaming.FileStem(ir.BusName)}.proto` | Bus '{ir.BusName}' — {ir.Messages.Count} message(s) |");

        return sb.ToString();
    }
}
