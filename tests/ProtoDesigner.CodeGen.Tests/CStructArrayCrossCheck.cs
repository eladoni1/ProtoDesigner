using System.Text;
using ProtoDesigner.CodeGen.C;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Compiles the generated codec for an array of structs and checks the bytes it produces against a
/// layout worked out by hand.
/// </summary>
/// <remarks>
/// <para>
/// The golden file pins this output against <em>itself</em> — it proves the emission is stable, not
/// that it is right. The two cross-checks compare the generated C against the C# reference codec,
/// which is a real independent check but does not reach composite elements: its own array path
/// describes an element by one <c>ElementPrimitive</c>, so both implementations would have to grow
/// the feature before they could disagree about it.
/// </para>
/// <para>
/// So the ground truth here is neither implementation. The expected bytes below are derived from the
/// declared layout by hand and written out literally. If the element stride, a member offset, the
/// float bit pattern or the inner loop over a fixed array member is wrong, this fails — and it
/// cannot be made to pass by changing the generator and the reference codec in the same wrong way.
/// </para>
/// <para>
/// Compiled as C and as C++ from one source, like the other cross-checks.
/// </para>
/// </remarks>
public class CStructArrayCrossCheck
{
    private const string Prefix = "proto";

    /// <summary>
    /// The message built below, byte by byte, little-endian throughout.
    ///
    ///   readingCount        u8            02
    ///   readings[0]         {u8,u8}       11 22
    ///   readings[1]         {u8,u8}       33 44
    ///   blockCount          u8            01
    ///   blocks[0].channelId u32   0xAABBCCDD -> DD CC BB AA
    ///   blocks[0].scale     f32   1.0f = 0x3F800000 -> 00 00 80 3F
    ///   blocks[0].samples   u8[4]         01 02 03 04
    ///   crc                 u32   0xDEADBEEF -> EF BE AD DE
    ///
    /// 22 bytes. Note what each part proves: the two readings prove the 16-bit element stride, the
    /// float proves a raw IEEE member inside an element is not cast through an integer, and the
    /// samples prove the inner loop over a fixed-size array member.
    /// </summary>
    private const string ExpectedHex = "021122334401DDCCBBAA0000803F01020304EFBEADDE";

    /// <summary>
    /// The same message with both arrays empty: <c>00</c>, nothing, <c>00</c>, nothing, then the crc.
    /// </summary>
    /// <remarks>
    /// The boundary the element loop is most likely to get wrong. A stride added once too often, or a
    /// region cursor advanced before the loop rather than inside it, still produces the right bytes at
    /// two elements and the wrong ones at none — and the trailing crc is what moves, so a decoder reads
    /// garbage for a field nobody touched.
    /// </remarks>
    private const string ExpectedEmptyHex = "0000EFBEADDE";

    /// <summary>
    /// Both arrays full: 8 readings and 2 blocks, the widest frame the declaration allows.
    /// </summary>
    /// <remarks>
    /// The other end of the same risk, and the one that proves MAX_BYTES is not an underestimate — a
    /// buffer sized by the generated macro has to survive the encoder writing its largest output.
    /// </remarks>
    private const string ExpectedFullHex =
        "08" + "0100" + "0101" + "0102" + "0103" + "0104" + "0105" + "0106" + "0107" +
        "02" +
        "DDCCBBAA" + "0000803F" + "01020304" +
        "DDCCBBAA" + "0000803F" + "01020304" +
        "EFBEADDE";

    // Two facts rather than a theory: MsvcLanguage is internal, so it cannot appear in a public
    // signature, and making it public to satisfy a test parameter would widen the surface for no gain.
    [Fact]
    public void A_struct_array_encodes_the_bytes_the_layout_declares_as_c() =>
        Run(MsvcLanguage.C);

    [Fact]
    public void A_struct_array_encodes_the_bytes_the_layout_declares_as_cpp() =>
        Run(MsvcLanguage.Cpp);

    private static void Run(MsvcLanguage language)
    {
        var (project, bus) = Corpus.StructArray();
        var ir = new IrBuilder().Build(project, bus);
        var set = new CGenerator().Generate(ir, new GeneratorOptions(Namespace: Prefix));

        var dir = Path.Combine(Path.GetTempPath(), $"pd-structarray-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var file in set.Files)
            {
                if (!file.RelativePath.EndsWith(".h", StringComparison.Ordinal)) continue;
                File.WriteAllText(Path.Combine(dir, file.RelativePath), file.Contents);
            }

            var headerName = set.Files.Single(f =>
                f.RelativePath.EndsWith(".h", StringComparison.Ordinal) &&
                f.RelativePath != "protodesigner_runtime.h" &&
                !f.RelativePath.EndsWith("_types.h", StringComparison.Ordinal)).RelativePath;

            var source = language == MsvcLanguage.C ? "driver.c" : "driver.cpp";
            File.WriteAllText(Path.Combine(dir, source), Driver(headerName));

            var compiler = MsvcLocator.Find();
            Assert.True(compiler is not null || MsvcLocator.SkipRequested,
                "No MSVC toolchain found, so the generated C was never compiled — the half of this test "
                + "that actually proves anything did not run. Install the VC++ build tools, or set "
                + $"{MsvcLocator.SkipVariable}=1 to accept generation-only coverage.");
            if (compiler is null) return;

            var (exit, output) = MsvcLocator.CompileAndRun(compiler, dir, source, language);
            Assert.True(exit == 0, $"Driver failed (exit {exit}):\n{output}");

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                              .Select(l => l.Trim()).ToList();

            Assert.Contains($"LEN=22", lines);
            Assert.Contains($"HEX={ExpectedHex}", lines);
            Assert.Contains("ROUNDTRIP=OK", lines);

            // 1 + 0 + 1 + 0 + 4: the crc has to land immediately after two empty variable regions.
            Assert.Contains("EMPTYLEN=6", lines);
            Assert.Contains($"EMPTYHEX={ExpectedEmptyHex}", lines);

            // 1 + 8*2 + 1 + 2*12 + 4, which must equal the declared MAX_BYTES.
            Assert.Contains("FULLLEN=46", lines);
            Assert.Contains($"FULLHEX={ExpectedFullHex}", lines);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>
    /// Populates the message, encodes it, prints the bytes, decodes them back and compares.
    /// </summary>
    /// <remarks>
    /// Plain assignment rather than designated initializers: this same source is compiled as C++14,
    /// which has none.
    /// </remarks>
    private static string Driver(string headerName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#include <string.h>");
        sb.AppendLine($"#include \"{headerName}\"");
        sb.AppendLine();
        sb.AppendLine("int main(void) {");
        sb.AppendLine($"    {Prefix}_Bundle msg;");
        sb.AppendLine("    memset(&msg, 0, sizeof(msg));");
        sb.AppendLine();
        sb.AppendLine("    msg.readingCount = 2;");
        sb.AppendLine("    msg.readings[0].channel = 0x11; msg.readings[0].value = 0x22;");
        sb.AppendLine("    msg.readings[1].channel = 0x33; msg.readings[1].value = 0x44;");
        sb.AppendLine();
        sb.AppendLine("    msg.blockCount = 1;");
        sb.AppendLine("    msg.blocks[0].channelId = 0xAABBCCDDu;");
        sb.AppendLine("    msg.blocks[0].scale = 1.0f;");
        sb.AppendLine("    msg.blocks[0].samples[0] = 1; msg.blocks[0].samples[1] = 2;");
        sb.AppendLine("    msg.blocks[0].samples[2] = 3; msg.blocks[0].samples[3] = 4;");
        sb.AppendLine();
        sb.AppendLine("    msg.crc = 0xDEADBEEFu;");
        sb.AppendLine();
        sb.AppendLine($"    {{ uint8_t wire[{Prefix.ToUpperInvariant()}_BUNDLE_MAX_BYTES];");
        sb.AppendLine("      size_t i;");
        sb.AppendLine($"      size_t n = {Prefix}_Bundle_ConvertToWire(&msg, wire, sizeof(wire));");
        sb.AppendLine("      printf(\"LEN=%u\\n\", (unsigned)n);");
        sb.AppendLine("      printf(\"HEX=\");");
        sb.AppendLine("      for (i = 0; i < n; ++i) printf(\"%02X\", wire[i]);");
        sb.AppendLine("      printf(\"\\n\");");
        sb.AppendLine();
        sb.AppendLine("      /* The declared length must agree with what the encoder actually wrote. */");
        sb.AppendLine($"      if (n != {Prefix}_Bundle_OnWireLength(&msg)) {{");
        sb.AppendLine("          printf(\"ROUNDTRIP=LENGTH-MISMATCH\\n\"); return 1; }");
        sb.AppendLine();
        sb.AppendLine($"      {{ {Prefix}_Bundle back;");
        sb.AppendLine("        pd_decode_result_t rc;");
        sb.AppendLine("        memset(&back, 0, sizeof(back));");
        sb.AppendLine($"        rc = {Prefix}_Bundle_ConvertToHost(wire, n, &back);");
        sb.AppendLine("        if (!rc.ok) { printf(\"ROUNDTRIP=DECODE-FAILED\\n\"); return 1; }");
        sb.AppendLine("        if (back.readingCount != msg.readingCount ||");
        sb.AppendLine("            back.readings[0].channel != 0x11 || back.readings[0].value != 0x22 ||");
        sb.AppendLine("            back.readings[1].channel != 0x33 || back.readings[1].value != 0x44 ||");
        sb.AppendLine("            back.blockCount != msg.blockCount ||");
        sb.AppendLine("            back.blocks[0].channelId != 0xAABBCCDDu ||");
        sb.AppendLine("            back.blocks[0].scale != 1.0f ||");
        sb.AppendLine("            back.blocks[0].samples[0] != 1 || back.blocks[0].samples[3] != 4 ||");
        sb.AppendLine("            back.crc != 0xDEADBEEFu) {");
        sb.AppendLine("            printf(\"ROUNDTRIP=FIELD-MISMATCH\\n\"); return 1; }");
        sb.AppendLine("        printf(\"ROUNDTRIP=OK\\n\");");
        sb.AppendLine("      }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /* Both arrays empty: the trailing crc must move up against the counts. */");
        sb.AppendLine($"    {{ uint8_t wire[{Prefix.ToUpperInvariant()}_BUNDLE_MAX_BYTES];");
        sb.AppendLine("      size_t i, n;");
        sb.AppendLine("      msg.readingCount = 0; msg.blockCount = 0;");
        sb.AppendLine($"      n = {Prefix}_Bundle_ConvertToWire(&msg, wire, sizeof(wire));");
        sb.AppendLine("      printf(\"EMPTYLEN=%u\\n\", (unsigned)n);");
        sb.AppendLine("      printf(\"EMPTYHEX=\");");
        sb.AppendLine("      for (i = 0; i < n; ++i) printf(\"%02X\", wire[i]);");
        sb.AppendLine("      printf(\"\\n\");");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /* Both arrays full: the widest frame the declaration allows. */");
        sb.AppendLine($"    {{ uint8_t wire[{Prefix.ToUpperInvariant()}_BUNDLE_MAX_BYTES];");
        sb.AppendLine("      size_t i, n;");
        sb.AppendLine("      msg.readingCount = 8;");
        sb.AppendLine("      for (i = 0; i < 8; ++i) {");
        sb.AppendLine("          msg.readings[i].channel = 1; msg.readings[i].value = (uint8_t)i; }");
        sb.AppendLine("      msg.blockCount = 2;");
        sb.AppendLine("      msg.blocks[1] = msg.blocks[0];");
        sb.AppendLine($"      n = {Prefix}_Bundle_ConvertToWire(&msg, wire, sizeof(wire));");
        sb.AppendLine("      printf(\"FULLLEN=%u\\n\", (unsigned)n);");
        sb.AppendLine("      printf(\"FULLHEX=\");");
        sb.AppendLine("      for (i = 0; i < n; ++i) printf(\"%02X\", wire[i]);");
        sb.AppendLine("      printf(\"\\n\");");
        sb.AppendLine("    }");
        sb.AppendLine("    return 0;");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
