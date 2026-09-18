using System.Globalization;
using System.Text;
using ProtoDesigner.Core.Ir;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.CSharp;

/// <summary>
/// Emits the C# <em>host representation</em> of a protocol: an enum per IR enum, a class per shared
/// struct type, a class per message carrying its size constants and fields, and a message-id enum per
/// bus. Each message also carries its wire layout as a comment — offset, width and endianness per field.
/// </summary>
/// <remarks>
/// <para>
/// <b>This target does not emit encode/decode yet.</b> It is the message structure, which is the half of
/// the job that is useful on its own: it is what you read to understand the protocol, and what you bind a
/// C# tool or test harness against. Conversion code is Phase 5, and it will land here behind the same
/// <see cref="IProtocolGenerator"/> interface without the declarations changing shape.
/// </para>
/// <para>
/// Saying so in the file header rather than emitting a stub that silently does nothing is deliberate. A
/// missing method is a compile error at the call site; a stub that returns 0 is a protocol bug found much
/// later, on the wire.
/// </para>
/// </remarks>
public sealed class CSharpGenerator : IProtocolGenerator
{
    public string Id => "csharp";

    public string DisplayName => "C# (encode / decode)";

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
            new(CSharpRuntime.FileName, CSharpRuntime.Emit(Namespace(options))),
            new(TypesFileName(options), EmitTypesFile(buses, options)),
        };

        foreach (var ir in buses)
            files.Add(new($"{CSharpNaming.TypeName(ir.BusName)}.cs", EmitBusFile(ir, options)));

        if (options.IncludeReadme)
            files.Add(new("README.md", EmitReadme(buses, options)));

        return new GeneratedFileSet(files);
    }

    private static string Namespace(GeneratorOptions options) => CSharpNaming.TypeName(options.Namespace);

    /// <summary>
    /// The shared declarations file. Types are project-wide, so a struct used by two buses is one type and
    /// is declared once here rather than repeated per bus — where two copies in one namespace would not
    /// compile at all.
    /// </summary>
    private static string TypesFileName(GeneratorOptions options) => $"{Namespace(options)}Types.cs";

    // ---- shared types --------------------------------------------------------------------------

    private static string EmitTypesFile(IReadOnlyList<ProtocolIr> buses, GeneratorOptions options)
    {
        var sb = new StringBuilder();
        EmitFileHeader(sb, $"{buses[0].ProjectName} — shared type declarations");
        sb.AppendLine($"namespace {Namespace(options)};");
        sb.AppendLine();

        // Named primitives declare no type — the host kind is already a built-in — but their wire size is
        // the thing a caller cannot derive, so it gets a holder class of its own.
        var seenPrimitives = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in buses.SelectMany(b => b.Primitives).Where(p => seenPrimitives.Add(p.Name)))
            EmitTypeSizeClass(sb, CSharpNaming.TypeName(p.Name), p.WireBits);

        // Deduplicate by name across buses: the same project type reaches several of them and would
        // otherwise be declared once per bus. Two genuinely different types sharing a name is already a
        // validation warning (PD0005), so first-wins is safe here.
        var seenEnums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in buses.SelectMany(b => b.Enums))
            if (seenEnums.Add(e.Name)) EmitEnum(sb, e);

        var seenStructs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ir in buses)
            foreach (var s in ir.Structs)
                if (seenStructs.Add(s.Name)) EmitStruct(sb, ir, s);

        return sb.ToString();
    }

    private static void EmitEnum(StringBuilder sb, IrEnum e)
    {
        var name = CSharpNaming.TypeName(e.Name);

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// {(e.IsFlags ? "Flag set" : "Enumeration")} '{Escape(e.Name)}'.");
        sb.AppendLine("/// </summary>");
        if (e.IsFlags) sb.AppendLine("[System.Flags]");
        sb.AppendLine($"public enum {name} : {CSharpNaming.StorageType(e.Underlying)}");
        sb.AppendLine("{");
        foreach (var m in e.Members)
            sb.AppendLine($"    {CSharpNaming.EnumMemberName(m.Name)} = {m.Value.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine("}");
        sb.AppendLine();

        // An enum cannot hold constants, so its wire size goes on a companion class. Same names as the
        // C++ target, so the two are readable side by side.
        EmitTypeSizeClass(sb, name, e.WireBits);
    }

    private static void EmitStruct(StringBuilder sb, ProtocolIr ir, IrStruct s)
    {
        var name = CSharpNaming.TypeName(s.Name);

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Struct '{Escape(s.Name)}'.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public sealed class {name}");
        sb.AppendLine("{");
        EmitTypeSizeMembers(sb, s.WireBits, "    ");
        foreach (var member in s.Members) EmitMember(sb, ir, name, member);
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// The wire size of a type, as constants. Bytes only for a whole number of them — a 4-bit enum has no
    /// byte size, and a rounded one would be wrong exactly where it would be trusted.
    /// </summary>
    private static void EmitTypeSizeMembers(StringBuilder sb, int wireBits, string indent)
    {
        if (wireBits <= 0) return;

        sb.AppendLine($"{indent}/// <summary>Wire size of the type itself. An individual field may narrow it —");
        sb.AppendLine($"{indent}/// check that field's own width before assuming this applies to it.</summary>");
        sb.AppendLine($"{indent}public const int OnWireBits = {wireBits};");
        if (wireBits % 8 == 0)
            sb.AppendLine($"{indent}public const int OnWireBytes = {wireBits / 8};");
        sb.AppendLine();
    }

    private static void EmitTypeSizeClass(StringBuilder sb, string typeName, int wireBits)
    {
        if (wireBits <= 0) return;

        sb.AppendLine($"/// <summary>Wire size of <see cref=\"{typeName}\"/>.</summary>");
        sb.AppendLine($"public static class {typeName}Wire");
        sb.AppendLine("{");
        EmitTypeSizeMembers(sb, wireBits, "    ");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // ---- one bus -------------------------------------------------------------------------------

    private static string EmitBusFile(ProtocolIr ir, GeneratorOptions options)
    {
        var sb = new StringBuilder();
        EmitFileHeader(sb, $"{ir.ProjectName} — bus '{ir.BusName}' ({ir.Transport})");
        sb.AppendLine($"namespace {Namespace(options)};");
        sb.AppendLine();

        if (!ir.Enums.Any(e => e.Synthetic == SyntheticEnum.MessageId)) EmitMessageIdEnum(sb, ir);
        if (!ir.Enums.Any(e => e.Synthetic == SyntheticEnum.ModuleId)) EmitModuleIdEnum(sb, ir);
        foreach (var m in ir.Messages) EmitMessage(sb, ir, m);

        return sb.ToString();
    }

    /// <summary>
    /// The bus's message-id enum. Ids are unique within a bus and start at 1, so <c>NotAssigned = 0</c> is
    /// a sentinel no real message can collide with.
    /// </summary>
    private static void EmitMessageIdEnum(StringBuilder sb, ProtocolIr ir)
    {
        var bus = CSharpNaming.TypeName(ir.BusName);
        var identified = ir.Messages.Where(m => m.WireId is > 0).OrderBy(m => m.WireId!.Value).ToList();

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Every message on bus '{Escape(ir.BusName)}' that carries an id.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public enum {bus}MessageId : uint");
        sb.AppendLine("{");
        sb.AppendLine("    NotAssigned = 0,");
        foreach (var m in identified)
            sb.AppendLine($"    {CSharpNaming.EnumMemberName(m.Name)} = {m.WireId!.Value},");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// The bus's module-id enum: design-time identities for the participants, not a wire value.
    /// </summary>
    /// <remarks>
    /// C# has namespaces, so this needs no bus prefix on the members the way the C target does — but the
    /// type is still named per bus, since two buses can both have a <c>Sensor</c>.
    /// </remarks>
    private static void EmitModuleIdEnum(StringBuilder sb, ProtocolIr ir)
    {
        if (ir.Modules.Count == 0) return;

        var bus = CSharpNaming.TypeName(ir.BusName);

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Every module on bus '{Escape(ir.BusName)}'. Nothing puts a module id in a frame;");
        sb.AppendLine("/// these exist so code can name an end of the link without a magic number.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public enum {bus}ModuleId : uint");
        sb.AppendLine("{");
        sb.AppendLine("    NotAssigned = 0,");
        foreach (var m in ir.Modules)
            sb.AppendLine($"    {CSharpNaming.EnumMemberName(m.Name)} = {m.Value},");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitMessage(StringBuilder sb, ProtocolIr ir, IrMessage m)
    {
        var name = CSharpNaming.TypeName(m.Name);

        sb.AppendLine("/// <summary>");
        sb.Append($"/// Message '{Escape(m.Name)}'");
        if (m.WireId is { } w) sb.Append($" (wire id {w})");
        sb.AppendLine(m.MinBits == m.MaxBits
            ? $" — fixed {m.MinBits} bits / {Bytes(m.MinBits)} bytes."
            : $" — {m.MinBits}..{m.MaxBits} bits / {Bytes(m.MinBits)}..{Bytes(m.MaxBits)} bytes.");
        sb.AppendLine("/// </summary>");
        EmitLayoutComment(sb, m);

        sb.AppendLine($"public sealed class {name}");
        sb.AppendLine("{");
        if (m.WireId is { } wireId)
            sb.AppendLine($"    public const uint WireId = {wireId};");

        // Only the byte capacity: it sizes a buffer, and OnWireLength answers the rest.
        sb.AppendLine($"    public const int MaxBytes = {Bytes(m.MaxBits)};");
        sb.AppendLine();

        // Members, not Fields: the host shape the user declared, with structs kept whole.
        foreach (var member in m.Members) EmitMember(sb, ir, name, member);

        sb.AppendLine();
        EmitOnWireLength(sb, m, name);
        CSharpCodec.EmitEncode(sb, ir, m, name);
        CSharpCodec.EmitDecode(sb, ir, m, name);

        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// <summary>
    /// Emits the message's on-wire length in bytes, mirroring <c>WireLength</c> in the Core and the C++
    /// target region by region — including the pad-to-byte the encoder applies at the end of each fixed
    /// region.
    /// </summary>
    /// <remarks>
    /// This one method is real behaviour rather than a declaration, and it is here deliberately: a
    /// trailing field sits at <c>OnWireLength() - &lt;Type&gt;Wire.OnWireBytes</c>, so a C# tool can find
    /// it without the encode/decode this target does not yet emit.
    /// </remarks>
    private static void EmitOnWireLength(StringBuilder sb, IrMessage m, string enclosingType)
    {
        sb.AppendLine("    /// <summary>The number of bytes this message occupies on the wire.</summary>");

        if (WireLength.IsFixedSize(m))
        {
            sb.AppendLine("    public int OnWireLength() => MaxBytes;   // fixed size: contents do not matter");
            return;
        }

        sb.AppendLine("    public int OnWireLength()");
        sb.AppendLine("    {");
        sb.AppendLine("        int bits = 0;");

        foreach (var region in m.Regions)
        {
            if (region.Kind == IrRegionKind.Fixed)
            {
                if (region.MaxBits > 0)
                {
                    sb.AppendLine($"        bits += {region.MaxBits};                 // region {region.Index} (Fixed)");
                    sb.AppendLine("        bits = ((bits + 7) / 8) * 8;   // each fixed region is padded to a byte");
                }
                continue;
            }

            sb.AppendLine($"        {{   // region {region.Index} (Variable), up to {region.MaxElements} x {region.ElementBits} bits");
            sb.AppendLine($"            int n = (int){CountExpression(m, region, enclosingType)};");
            sb.AppendLine($"            if (n > {region.MaxElements}) n = {region.MaxElements};   // truncated at capacity");
            sb.AppendLine("            if (n < 0) n = 0;");
            if (region.PrefixBits > 0)
                sb.AppendLine($"            bits += {region.PrefixBits};                // inline length prefix");
            sb.AppendLine($"            bits += n * {region.ElementBits};");
            sb.AppendLine("        }");
        }

        sb.AppendLine("        return (bits + 7) / 8;");
        sb.AppendLine("    }");
    }

    /// <summary>
    /// How many elements a variable region carries, as a C# expression over this class. Names go through
    /// the same collision handling the member declarations use, so a field whose name matches its message
    /// is referred to by the name that was actually emitted.
    /// </summary>
    private static string CountExpression(IrMessage m, IrRegion region, string enclosingType)
    {
        if (region.CountFieldIndex is { } ci)
            return CSharpNaming.MemberNameIn(enclosingType, m.Fields[ci].Path);

        var arrayField = m.Fields.FirstOrDefault(f => f.RegionIndex == region.Index && f.Array is not null);
        return arrayField is null
            ? "0"
            : $"{CSharpNaming.MemberNameIn(enclosingType, arrayField.Path)}Count";
    }

    /// <summary>
    /// Writes the message's wire layout as a remarks block: one line per field with its region, bit offset
    /// and width. This is the structure the target exists to show, so it is emitted per field rather than
    /// left to the reader to derive from the .pdproj.
    /// </summary>
    private static void EmitLayoutComment(StringBuilder sb, IrMessage m)
    {
        sb.AppendLine("/// <remarks>");
        sb.AppendLine("/// Wire layout:");
        sb.AppendLine("/// <code>");

        foreach (var region in m.Regions)
        {
            var fields = m.Fields.Where(f => f.RegionIndex == region.Index).ToList();
            if (fields.Count == 0 && region.Kind == IrRegionKind.Fixed) continue;

            sb.Append($"/// region {region.Index} ({region.Kind}");
            if (region.Kind == IrRegionKind.Variable)
                sb.Append($", up to {region.MaxElements} x {region.ElementBits} bits");
            sb.AppendLine(")");

            foreach (var f in fields)
            {
                // Offsets in a variable region are per element, not per message, so labelling them the
                // same way would misreport every field after the first dynamic array.
                var offsetLabel = region.Kind == IrRegionKind.Variable
                    ? $"+{f.BitOffset,-4}"
                    : $"@{f.BitOffset,-4}";

                sb.Append($"///   {Escape(f.Path),-32} {offsetLabel} {f.BitWidth,3} bit  {f.Endianness}");
                if (!f.Transform.IsIdentity) sb.Append($"  transform {f.Transform}");
                if (f.Array is { } arr) sb.Append($"  array {arr.Kind}, max {arr.MaxElements}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("/// </code>");
        sb.AppendLine("/// </remarks>");
    }

    private static void EmitMember(StringBuilder sb, ProtocolIr ir, string enclosingType, IrMember member)
    {
        var name = CSharpNaming.MemberNameIn(enclosingType, member.Name);
        var type = member.Kind switch
        {
            IrMemberKind.EnumRef when member.EnumIndex is { } ei => CSharpNaming.TypeName(ir.Enums[ei].Name),
            IrMemberKind.StructRef when member.StructIndex is { } si => CSharpNaming.TypeName(ir.Structs[si].Name),
            _ => CSharpNaming.StorageType(member.Primitive),
        };

        if (member.Note is not null)
            sb.AppendLine($"    /// <summary>{Escape(member.Note)}</summary>");

        if (member.ArrayCapacity is { } capacity)
        {
            // Allocated at the declared capacity so the field is never null: the array's length is the
            // protocol's capacity, and how many entries are actually live is the count's job, not the
            // array's. A null here would be a NullReferenceException in every consumer instead.
            sb.AppendLine(member.Kind == IrMemberKind.StructRef
                ? $"    public {type}[] {name} = PdInit.Array<{type}>({capacity});"
                : $"    public {type}[] {name} = new {type}[{capacity}];");

            // Only a self-describing array carries its own count; see IrMember.NeedsCountMember. The two
            // are one logical field, so a blank line keeps the pair visually together — and is not worth
            // emitting when there is no second half.
            if (member.NeedsCountMember)
            {
                sb.AppendLine($"    public uint {name}Count;");
                sb.AppendLine();
            }
            return;
        }

        // A struct member is a reference type here, so it is initialised for the same reason as an array.
        var initialiser = member.Kind == IrMemberKind.StructRef ? $" = new {type}();" : ";";
        sb.AppendLine($"    public {type} {name}{initialiser}");
    }

    // ---- boilerplate ---------------------------------------------------------------------------

    private static void EmitFileHeader(StringBuilder sb, string title)
    {
        sb.AppendLine("// -----------------------------------------------------------------------------");
        sb.AppendLine($"// {title}");
        sb.AppendLine("// Generated by ProtoDesigner. Do not edit; regenerate from the .pdproj instead.");
        sb.AppendLine("//");
        sb.AppendLine("// The host-side declarations, the wire layout that goes with them, and the");
        sb.AppendLine("// conversion functions. Same wire format as the C target: a frame written by one");
        sb.AppendLine("// is read by the other.");
        sb.AppendLine("// -----------------------------------------------------------------------------");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
    }

    private static string EmitReadme(IReadOnlyList<ProtocolIr> buses, GeneratorOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {buses[0].ProjectName} — generated C# declarations");
        sb.AppendLine();
        sb.AppendLine("Generated by ProtoDesigner. Regenerate rather than editing by hand.");
        sb.AppendLine();
        sb.AppendLine("## What this is");
        sb.AppendLine();
        sb.AppendLine("The host representation of the protocol: a class per message and per shared struct, an");
        sb.AppendLine("enum per enum type, and each message's wire layout recorded as a comment.");
        sb.AppendLine();
        sb.AppendLine("## Converting");
        sb.AppendLine();
        sb.AppendLine("Each message carries `ConvertToWire` and `ConvertToHost`, over the same wire format as");
        sb.AppendLine("the C target — a frame written by one is read by the other. Size a buffer with the");
        sb.AppendLine("message's `MaxBytes`; `ConvertToWire` returns how many of those bytes it used, which");
        sb.AppendLine("is what you send.");
        sb.AppendLine();
        sb.AppendLine("```csharp");
        sb.AppendLine("var wire = new byte[Telemetry.MaxBytes];");
        sb.AppendLine("int n = msg.ConvertToWire(wire);");
        sb.AppendLine();
        sb.AppendLine("var back = new Telemetry();");
        sb.AppendLine("if (!Telemetry.ConvertToHost(wire, n, back).Ok) { /* frame ran out early */ }");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"Namespace: `{Namespace(options)}`");
        sb.AppendLine();
        sb.AppendLine("| File | Contents |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| `{TypesFileName(options)}` | Shared enums and structs, used across every bus |");
        foreach (var ir in buses)
            sb.AppendLine($"| `{CSharpNaming.TypeName(ir.BusName)}.cs` | Bus '{ir.BusName}' — {ir.Messages.Count} message(s) |");

        return sb.ToString();
    }

    private static int Bytes(int bits) => (bits + 7) / 8;

    /// <summary>Keeps a user-chosen name from closing the XML doc comment it appears inside.</summary>
    private static string Escape(string raw) =>
        raw.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
