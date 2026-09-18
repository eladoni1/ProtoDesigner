using System.Globalization;
using System.Text;
using ProtoDesigner.Core.Ir;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.CSharp;

/// <summary>
/// Emits <c>ConvertToWire</c> and <c>ConvertToHost</c> for one message.
/// </summary>
/// <remarks>
/// <para>
/// Same algorithm as the C target, because the two speak one wire format: walk the regions, position each
/// field from its region's origin, and let each array kind work out its own count. Where the C generator
/// writes a cursor into a local, this writes the same cursor into a C# local — the shapes differ, the
/// bytes must not.
/// </para>
/// <para>
/// A generator reads only the IR. This one walks <c>Fields</c> rather than <c>Members</c>, because offsets
/// are defined over leaves in wire order: <c>Members</c> is the host shape and would put a struct where
/// the wire has its flattened contents.
/// </para>
/// </remarks>
internal static class CSharpCodec
{
    public static void EmitEncode(StringBuilder sb, ProtocolIr ir, IrMessage m, string type)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Writes this message into <paramref name=\"wire\"/> and returns the number of bytes used.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine($"    /// <remarks>A buffer of <see cref=\"MaxBytes\"/> always fits.</remarks>");
        sb.AppendLine("    public int ConvertToWire(byte[] wire)");
        sb.AppendLine("    {");
        sb.AppendLine("        var w = new PdBitWriter(wire);");

        foreach (var region in m.Regions)
        {
            var fields = m.Fields.Where(f => f.RegionIndex == region.Index).ToList();
            if (fields.Count == 0 && region.Kind == IrRegionKind.Fixed) continue;

            sb.AppendLine();
            sb.AppendLine($"        // --- region {region.Index} ({region.Kind}) ---");
            sb.AppendLine($"        int r{region.Index} = w.BitLength;");

            foreach (var f in fields)
            {
                sb.AppendLine($"        // {f.Path}");
                sb.AppendLine($"        if (w.BitLength < r{region.Index} + {f.BitOffset}) "
                    + $"w.Skip(r{region.Index} + {f.BitOffset} - w.BitLength);");
                EncodeField(sb, ir, m, f, type);
            }

            if (region.Kind == IrRegionKind.Fixed) sb.AppendLine("        w.PadTo(8);");
        }

        sb.AppendLine();
        sb.AppendLine("        return w.ByteLength;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    public static void EmitDecode(StringBuilder sb, ProtocolIr ir, IrMessage m, string type)
    {
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Reads <paramref name=\"length\"/> bytes of <paramref name=\"wire\"/> into <paramref name=\"msg\"/>.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static PdDecodeResult ConvertToHost(byte[] wire, int length, "
            + $"{type} msg)");
        sb.AppendLine("    {");
        sb.AppendLine("        var r = new PdBitReader(wire, length);");

        foreach (var region in m.Regions)
        {
            var fields = m.Fields.Where(f => f.RegionIndex == region.Index).ToList();
            if (fields.Count == 0 && region.Kind == IrRegionKind.Fixed) continue;

            sb.AppendLine();
            sb.AppendLine($"        // --- region {region.Index} ({region.Kind}) ---");
            sb.AppendLine($"        int r{region.Index} = r.BitOffset;");

            foreach (var f in fields)
            {
                sb.AppendLine($"        // {f.Path}");
                sb.AppendLine($"        if (r.BitOffset < r{region.Index} + {f.BitOffset}) "
                    + $"r.Skip(r{region.Index} + {f.BitOffset} - r.BitOffset);");
                DecodeField(sb, ir, m, f, type);
            }

            if (region.Kind == IrRegionKind.Fixed) sb.AppendLine("        r.AlignTo(8);");
        }

        sb.AppendLine();
        sb.AppendLine("        if (r.Underflowed) return PdDecodeResult.Fail(r.BitOffset);");
        sb.AppendLine("        return PdDecodeResult.Success();");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    /// <summary>
    /// The C# expression that reaches one field, as a dotted walk into the declared members.
    /// </summary>
    /// <remarks>
    /// A field path is flat because offsets are defined over leaves in wire order, but the host shape is
    /// nested: <c>header.messageId</c> is declared as <c>Header.MessageId</c>, a member of a member.
    /// Flattening it to one identifier produces code that names something no class has. The walk also
    /// picks up the collision rename <see cref="CSharpNaming.MemberNameIn"/> applies, which depends on the
    /// enclosing type and so cannot be worked out from the path alone.
    /// </remarks>
    private static string AccessPath(ProtocolIr ir, IrMessage m, string path, string rootType)
    {
        var segments = path.Replace("[]", string.Empty)
                           .Split('.', StringSplitOptions.RemoveEmptyEntries);

        var members = m.Members;
        var enclosing = rootType;
        var parts = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            var member = members.FirstOrDefault(x => string.Equals(x.Name, segment, StringComparison.Ordinal));
            if (member is null)
            {
                // A path that does not resolve is a leaf the member tree does not model; name it directly
                // rather than dropping it, so the compiler reports it instead of the codec silently
                // writing to the wrong place.
                parts.Add(CSharpNaming.MemberNameIn(enclosing, segment));
                break;
            }

            parts.Add(CSharpNaming.MemberNameIn(enclosing, member.Name));

            if (member.Kind == IrMemberKind.StructRef && member.StructIndex is { } si)
            {
                members = ir.Structs[si].Members;
                enclosing = CSharpNaming.TypeName(ir.Structs[si].Name);
            }
        }

        return string.Join(".", parts);
    }

    // ---- one field -------------------------------------------------------------------------------

    private static void EncodeField(StringBuilder sb, ProtocolIr ir, IrMessage m, IrField f, string type)
    {
        var self = "this." + AccessPath(ir, m, f.Path, type);
        var endian = EndianArgs(f.Endianness, f.BitOrder);

        if (f.Kind == IrFieldKind.Array && f.Array is { } arr)
        {
            EncodeArray(sb, ir, m, f, arr, self, type, endian);
            return;
        }

        WriteValue(sb, "        ", self, f.Primitive, f.Kind == IrFieldKind.EnumRef,
                   f.Transform, f.BitWidth, endian, f.WireIsSigned);
    }

    private static void DecodeField(StringBuilder sb, ProtocolIr ir, IrMessage m, IrField f, string type)
    {
        var member = "msg." + AccessPath(ir, m, f.Path, type);
        var endian = EndianArgs(f.Endianness, f.BitOrder);

        if (f.Kind == IrFieldKind.Array && f.Array is { } arr)
        {
            DecodeArray(sb, ir, m, f, arr, member, type, endian);
            return;
        }

        var target = f.Kind == IrFieldKind.EnumRef && f.EnumIndex is { } ei
            ? CSharpNaming.TypeName(ir.Enums[ei].Name)
            : CSharpNaming.StorageType(f.Primitive);

        ReadValue(sb, "        ", member, target, f.Primitive, f.Transform, f.BitWidth, endian,
                  f.WireIsSigned);
    }

    // ---- arrays ----------------------------------------------------------------------------------

    private static void EncodeArray(StringBuilder sb, ProtocolIr ir, IrMessage m, IrField f,
        IrArrayInfo arr, string member, string type, string endian)
    {
        var count = EncodeCount(ir, m, arr, member, type);

        if (arr.Kind == IrArrayKind.LengthPrefixed)
        {
            // Clamped like the element loop below, because the generator owns this prefix: announcing
            // more elements than it then writes would send any reader off the end of the frame.
            sb.AppendLine($"        {{ ulong n = (ulong)({count}); "
                + $"if (n > {arr.MaxElements}UL) n = {arr.MaxElements}UL;");
            sb.AppendLine($"          w.WriteUnsigned(n, {arr.PrefixBits}, PdEndian.Little, PdBitOrder.MsbFirst); }}");
        }

        sb.AppendLine($"        for (int i = 0; i < (int)({count}) && i < {arr.MaxElements}; i++)");
        sb.AppendLine("        {");

        if (arr.HasCompositeElement)
        {
            // The stride is re-established at the end of every iteration, so padding inside an element
            // cannot accumulate into a drift across the array.
            sb.AppendLine("            int e = w.BitLength;");
            foreach (var em in arr.ElementFields!)
            {
                var emName = CSharpNaming.MemberName(em.Name);
                sb.AppendLine($"            if (w.BitLength < e + {em.BitOffset}) "
                    + $"w.Skip(e + {em.BitOffset} - w.BitLength);");

                var access = $"{member}[i].{emName}";
                var indent = "            ";
                if (em.FixedArrayCount is { } n)
                {
                    sb.AppendLine($"            for (int j = 0; j < {n}; j++)");
                    sb.AppendLine("            {");
                    indent = "                ";
                    access += "[j]";
                }

                WriteValue(sb, indent, access, em.Primitive, em.EnumIndex is not null,
                           em.Transform, em.BitWidth, EndianArgs(em.Endianness, em.BitOrder),
                           em.WireIsSigned);

                if (em.FixedArrayCount is not null) sb.AppendLine("            }");
            }
            sb.AppendLine($"            if (w.BitLength < e + {arr.ElementBits}) "
                + $"w.Skip(e + {arr.ElementBits} - w.BitLength);");
        }
        else
        {
            WriteValue(sb, "            ", $"{member}[i]", arr.ElementPrimitive,
                       arr.ElementEnumIndex is not null, f.Transform, arr.ElementBits, endian,
                       f.WireIsSigned);
        }

        sb.AppendLine("        }");

        if (arr.Kind == IrArrayKind.Terminated && arr.Sentinel.Count > 0)
        {
            var bytes = string.Join(", ", arr.Sentinel.Select(b => $"0x{b:X2}"));
            sb.AppendLine($"        w.WriteBytes(new byte[] {{ {bytes} }}, {arr.Sentinel.Count});");
        }
    }

    private static void DecodeArray(StringBuilder sb, ProtocolIr ir, IrMessage m, IrField f,
        IrArrayInfo arr, string member, string type, string endian)
    {
        var count = DecodeCount(sb, ir, m, f, arr, member, type);

        sb.AppendLine($"        for (int i = 0; i < (int)({count}) && i < {arr.MaxElements}; i++)");
        sb.AppendLine("        {");

        if (arr.HasCompositeElement)
        {
            // Elements are constructed by the declaration (PdInit.Array), so there is nothing to
            // null-check here.
            sb.AppendLine("            int e = r.BitOffset;");
            foreach (var em in arr.ElementFields!)
            {
                var emName = CSharpNaming.MemberName(em.Name);
                sb.AppendLine($"            if (r.BitOffset < e + {em.BitOffset}) "
                    + $"r.Skip(e + {em.BitOffset} - r.BitOffset);");

                var emType = em.EnumIndex is { } eei
                    ? CSharpNaming.TypeName(ir.Enums[eei].Name)
                    : CSharpNaming.StorageType(em.Primitive);

                var target = $"{member}[i].{emName}";
                var indent = "            ";
                if (em.FixedArrayCount is { } n)
                {
                    sb.AppendLine($"            for (int j = 0; j < {n}; j++)");
                    sb.AppendLine("            {");
                    indent = "                ";
                    target += "[j]";
                }

                ReadValue(sb, indent, target, emType, em.Primitive, em.Transform, em.BitWidth,
                          EndianArgs(em.Endianness, em.BitOrder), em.WireIsSigned);

                if (em.FixedArrayCount is not null) sb.AppendLine("            }");
            }
            sb.AppendLine($"            if (r.BitOffset < e + {arr.ElementBits}) "
                + $"r.Skip(e + {arr.ElementBits} - r.BitOffset);");
        }
        else
        {
            var elemType = arr.ElementEnumIndex is { } ei
                ? CSharpNaming.TypeName(ir.Enums[ei].Name)
                : CSharpNaming.StorageType(arr.ElementPrimitive);

            ReadValue(sb, "            ", $"{member}[i]", elemType, arr.ElementPrimitive,
                      f.Transform, arr.ElementBits, endian, f.WireIsSigned);
        }

        sb.AppendLine("        }");

        if (arr.Kind == IrArrayKind.Terminated && arr.Sentinel.Count > 0)
            sb.AppendLine($"        r.Skip({arr.Sentinel.Count * 8});   // sentinel");
    }

    /// <summary>Where the encoder gets the element count from.</summary>
    private static string EncodeCount(ProtocolIr ir, IrMessage m, IrArrayInfo arr, string member, string type) =>
        arr.Kind switch
        {
            IrArrayKind.Fixed => arr.ElementCount!.Value.ToString(CultureInfo.InvariantCulture),
            // The count field is the single source of truth — read it, don't shadow it.
            IrArrayKind.CountFromField when arr.CountFieldIndex is { } ci =>
                "this." + AccessPath(ir, m, m.Fields[ci].Path, type),
            _ => $"{member}Count",
        };

    /// <summary>
    /// Where the decoder gets the element count from, emitting whatever work that takes first.
    /// </summary>
    /// <remarks>
    /// The two self-describing kinds work it out rather than asking: a terminated array scans ahead for
    /// its sentinel without moving the cursor, and a fill-remaining one measures what is left after the
    /// regions that still have to fit. Reading a caller-supplied count instead is what made both unusable
    /// for a frame whose length is not known in advance.
    /// </remarks>
    private static string DecodeCount(StringBuilder sb, ProtocolIr ir, IrMessage m, IrField f,
        IrArrayInfo arr, string member, string type)
    {
        switch (arr.Kind)
        {
            case IrArrayKind.Fixed:
                return arr.ElementCount!.Value.ToString(CultureInfo.InvariantCulture);

            case IrArrayKind.CountFromField when arr.CountFieldIndex is { } ci:
                return "msg." + AccessPath(ir, m, m.Fields[ci].Path, type);

            case IrArrayKind.LengthPrefixed:
                sb.AppendLine($"        {member}Count = (uint)r.ReadUnsigned({arr.PrefixBits}, "
                    + "PdEndian.Little, PdBitOrder.MsbFirst);");
                return $"{member}Count";

            case IrArrayKind.Terminated:
                var bytes = string.Join(", ", arr.Sentinel.Select(b => $"0x{b:X2}"));
                sb.AppendLine($"        {{ var end = new byte[] {{ {bytes} }};");
                sb.AppendLine("          int scan = r.BitOffset;");
                sb.AppendLine("          uint n = 0;");
                sb.AppendLine($"          while (n < {arr.MaxElements}u)");
                sb.AppendLine("          {");
                sb.AppendLine("              if (r.MatchBytesAt(scan, end)) break;");
                sb.AppendLine($"              if (scan + {arr.ElementBits} > r.BitOffset + r.BitsRemaining) break;");
                sb.AppendLine($"              scan += {arr.ElementBits};");
                sb.AppendLine("              n++;");
                sb.AppendLine("          }");
                sb.AppendLine($"          {member}Count = n; }}");
                return $"{member}Count";

            default:
                var trailing = m.Regions.Where(reg => reg.Index > f.RegionIndex).Sum(reg => reg.MinBits);
                sb.AppendLine("        { int avail = r.BitsRemaining;");
                if (trailing > 0)
                    sb.AppendLine($"          avail = avail > {trailing} ? avail - {trailing} : 0;");
                sb.AppendLine($"          avail /= {arr.ElementBits};");
                sb.AppendLine($"          if (avail > {arr.MaxElements}) avail = {arr.MaxElements};");
                sb.AppendLine($"          {member}Count = (uint)avail; }}");
                return $"{member}Count";
        }
    }

    // ---- one value -------------------------------------------------------------------------------

    private static void WriteValue(StringBuilder sb, string indent, string access,
        PrimitiveKind primitive, bool isEnum, ScalarTransform transform, int bits, string endian,
        bool wireIsSigned)
    {
        // A float host with no transform puts its IEEE bit pattern on the wire verbatim; casting to an
        // integer would keep 3 of 3.14159 and discard the rest.
        if (IsRawFloat(primitive, transform))
        {
            sb.AppendLine($"{indent}w.WriteUnsigned((ulong)PdFloat.ToBits({access}), {bits}, {endian});");
            return;
        }

        var expr = ToWire(isEnum ? $"(long)({access})" : access, transform, primitive);
        sb.AppendLine(wireIsSigned
            ? $"{indent}w.WriteSigned((long)({expr}), {bits}, {endian});"
            : $"{indent}w.WriteUnsigned((ulong)({expr}), {bits}, {endian});");
    }

    private static void ReadValue(StringBuilder sb, string indent, string target, string targetType,
        PrimitiveKind primitive, ScalarTransform transform, int bits, string endian, bool wireIsSigned)
    {
        if (IsRawFloat(primitive, transform))
        {
            var call = primitive == PrimitiveKind.F32
                ? $"PdFloat.FromBits((uint)r.ReadUnsigned({bits}, {endian}))"
                : $"PdFloat.FromBits((ulong)r.ReadUnsigned({bits}, {endian}))";
            sb.AppendLine($"{indent}{target} = {call};");
            return;
        }

        var read = wireIsSigned
            ? $"r.ReadSigned({bits}, {endian})"
            : $"r.ReadUnsigned({bits}, {endian})";
        sb.AppendLine($"{indent}{target} = ({targetType})({FromWire(read, transform, primitive)});");
    }

    // ---- transforms ------------------------------------------------------------------------------

    private static string EndianArgs(Endianness e, BitOrder order) =>
        (e == Endianness.Big ? "PdEndian.Big" : "PdEndian.Little")
        + ", "
        + (order == BitOrder.LsbFirst ? "PdBitOrder.LsbFirst" : "PdBitOrder.MsbFirst");

    /// <summary><c>wire = (value - offset) / scale</c>, rounded when the maths is floating-point.</summary>
    /// <remarks>
    /// The rounding matters for the same reason it does in C: a cast truncates toward zero, so a scaled
    /// field would never reach the top code its width was chosen for and every reading would be biased
    /// low. Integer pipelines are left alone rather than forced through a double.
    /// </remarks>
    private static string ToWire(string valueExpr, ScalarTransform t, PrimitiveKind host)
    {
        if (t.IsIdentity) return valueExpr;

        var expr = valueExpr;
        if (t.Offset != 0m) expr = $"(({expr}) {Signed(-t.Offset)})";
        if (t.Scale != 1m) expr = $"(({expr}) / {Dec(t.Scale)})";

        return IsFloatingMath(t, host) ? $"PdMath.Quantize({expr})" : expr;
    }

    /// <summary><c>value = (wire * scale) + offset</c>.</summary>
    private static string FromWire(string wireExpr, ScalarTransform t, PrimitiveKind host)
    {
        if (t.IsIdentity) return wireExpr;

        var expr = wireExpr;
        if (t.Scale != 1m) expr = $"(({expr}) * {Dec(t.Scale)})";
        if (t.Offset != 0m) expr = $"(({expr}) {Signed(t.Offset)})";

        // A float host keeps the fractional part coming back — that IS the decoded value — so only an
        // integer host needs the result rounded rather than truncated.
        return IsFloatingMath(t, host) && !host.IsFloat() ? $"PdMath.Quantize({expr})" : expr;
    }

    private static bool IsFloatingMath(ScalarTransform t, PrimitiveKind host) =>
        host.IsFloat() || decimal.Truncate(t.Scale) != t.Scale;

    private static bool IsRawFloat(PrimitiveKind host, ScalarTransform t) =>
        host.IsFloat() && t.IsIdentity;

    private static string Signed(decimal value) =>
        value < 0m ? $"- {Dec(-value)}" : $"+ {Dec(value)}";

    /// <summary>
    /// A transform constant as a C# literal.
    /// </summary>
    /// <remarks>
    /// Integral values stay integral so an otherwise-integer pipeline is not dragged into floating-point
    /// maths. Everything else becomes a <c>double</c> literal, printed with "R" so it names exactly the
    /// double the C target's constant also names — the two have to agree bit for bit or the same field
    /// decodes differently on each side.
    /// </remarks>
    private static string Dec(decimal value)
    {
        if (decimal.Truncate(value) == value && Math.Abs(value) < 1e18m)
            return decimal.ToInt64(value).ToString(CultureInfo.InvariantCulture);

        var text = ((double)value).ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') || text.Contains('e') ? text : text + ".0";
    }
}
