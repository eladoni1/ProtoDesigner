using System.Text;
using ProtoDesigner.CodeGen.C;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Compiles and runs the generated codec for the two array kinds that decode without being told a count.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IrArrayKind.Terminated"/> and <see cref="IrArrayKind.FillRemaining"/> used to decode by
/// asking the caller how many elements to read, which made them unusable for the one job they exist to
/// do: receive a frame whose length you do not know in advance. Encode was always correct, so a golden
/// file could not tell the difference — the emitted decode was well-formed C that simply read whatever
/// count happened to be in the struct.
/// </para>
/// <para>
/// So the assertion that matters below is the one where the driver <b>zeroes the destination struct and
/// never sets a count</b>. If the decoder does not work the count out for itself — by scanning for the
/// sentinel, or by measuring what is left after the trailing fields — it reads zero elements and the test
/// fails. The expected bytes are derived from the declaration by hand, so neither implementation is being
/// checked against itself.
/// </para>
/// <para>
/// Needs MSVC, like the other cross-checks, and fails rather than skips when it is absent.
/// </para>
/// </remarks>
public class CTerminatedFillCrossCheck
{
    private const string Prefix = "proto";

    /// <summary>
    /// <c>lead</c>, then up to eight bytes, then the terminator, then <c>tail</c>.
    /// </summary>
    /// <remarks>
    /// A sentinel-terminated array cannot carry its own sentinel value as data — that is inherent to the
    /// format, not a limitation of this decoder — so the payload below avoids <c>0x00</c>.
    /// </remarks>
    private static (Project Project, Bus Bus) Terminated() =>
        Build("Trail", u8 => new ArrayLength.Terminated(new byte[] { 0x00 }, MaxCount: 8));

    /// <summary><c>lead</c>, then whatever is left once <c>tail</c> is accounted for.</summary>
    private static (Project Project, Bus Bus) Filling() =>
        Build("Fill", u8 => new ArrayLength.FillRemaining(MaxCount: 8));

    private static (Project Project, Bus Bus) Build(string messageName, Func<TypeId, ArrayLength> length)
    {
        var project = new Project($"{messageName}Sample");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var samples = project.Types.Add(
            new ArrayType(TypeId.New(), "Samples", u8.Id, length(u8.Id)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), messageName) { WireId = 1 };
        message.Fields.Add(new FieldBinding(FieldId.New(), "lead", u8.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "samples", samples.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "tail", u8.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);
        return (project, bus);
    }

    [Fact]
    public void A_terminated_array_decodes_its_own_count_as_c() =>
        RunTerminated(MsvcLanguage.C);

    [Fact]
    public void A_terminated_array_decodes_its_own_count_as_cpp() =>
        RunTerminated(MsvcLanguage.Cpp);

    [Fact]
    public void A_fill_remaining_array_decodes_its_own_count_as_c() =>
        RunFilling(MsvcLanguage.C);

    [Fact]
    public void A_fill_remaining_array_decodes_its_own_count_as_cpp() =>
        RunFilling(MsvcLanguage.Cpp);

    private static void RunTerminated(MsvcLanguage language)
    {
        // lead AA, three samples, terminator 00, tail BB.
        var lines = Compile(Terminated(), "Trail", language, TerminatedDriver);
        if (lines is null) return;

        Assert.Contains("LEN=6", lines);
        Assert.Contains("HEX=AA11223300BB", lines);
        Assert.Contains("DECODED=3:11,22,33:BB", lines);

        // No elements at all: the terminator lands immediately and tail follows it.
        Assert.Contains("EMPTYLEN=3", lines);
        Assert.Contains("EMPTYHEX=AA00BB", lines);
        Assert.Contains("EMPTYDECODED=0:BB", lines);

        // Full: the scan stops at the declared maximum rather than running into the terminator.
        Assert.Contains("FULLLEN=11", lines);
        Assert.Contains("FULLDECODED=8:BB", lines);
    }

    private static void RunFilling(MsvcLanguage language)
    {
        // lead AA, three samples, tail BB — no framing of its own at all.
        var lines = Compile(Filling(), "Fill", language, FillingDriver);
        if (lines is null) return;

        Assert.Contains("LEN=5", lines);
        Assert.Contains("HEX=AA112233BB", lines);
        Assert.Contains("DECODED=3:11,22,33:BB", lines);

        // The boundary that proves the trailing field is reserved rather than swallowed: with nothing
        // between them, the count has to come out zero and tail has to be BB, not consumed as an element.
        Assert.Contains("EMPTYLEN=2", lines);
        Assert.Contains("EMPTYHEX=AABB", lines);
        Assert.Contains("EMPTYDECODED=0:BB", lines);

        Assert.Contains("FULLLEN=10", lines);
        Assert.Contains("FULLDECODED=8:BB", lines);
    }

    private static List<string>? Compile(
        (Project Project, Bus Bus) model, string messageName, MsvcLanguage language,
        Func<string, string, string> driver)
    {
        var ir = new IrBuilder().Build(model.Project, model.Bus);
        var set = new CGenerator().Generate(ir, new GeneratorOptions(Namespace: Prefix));

        var dir = Path.Combine(Path.GetTempPath(), $"pd-termfill-{Guid.NewGuid():N}");
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
            File.WriteAllText(Path.Combine(dir, source), driver(headerName, messageName));

            var compiler = MsvcLocator.Find();
            Assert.True(compiler is not null || MsvcLocator.SkipRequested,
                "No MSVC toolchain found, so the generated decode was never run — the half of this test "
                + "that proves the count is worked out rather than supplied did not happen. Install the "
                + $"VC++ build tools, or set {MsvcLocator.SkipVariable}=1 to accept generation-only "
                + "coverage.");
            if (compiler is null) return null;

            var (exit, output) = MsvcLocator.CompileAndRun(compiler, dir, source, language);
            Assert.True(exit == 0, $"Driver failed (exit {exit}):\n{output}");

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                         .Select(l => l.Trim()).ToList();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>Shared preamble: includes, the struct, and a hex dump helper.</summary>
    private static StringBuilder Preamble(string headerName, string messageName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#include <stdio.h>");
        sb.AppendLine("#include <string.h>");
        sb.AppendLine($"#include \"{headerName}\"");
        sb.AppendLine();
        sb.AppendLine($"typedef {Prefix}_{messageName} msg_t;");
        sb.AppendLine($"#define MAXB {Prefix.ToUpperInvariant()}_{messageName.ToUpperInvariant()}_MAX_BYTES");
        sb.AppendLine($"#define ENCODE {Prefix}_{messageName}_ConvertToWire");
        sb.AppendLine($"#define DECODE {Prefix}_{messageName}_ConvertToHost");
        sb.AppendLine();
        sb.AppendLine("static void dump(const char* tag, const uint8_t* w, size_t n) {");
        sb.AppendLine("    size_t i;");
        sb.AppendLine("    printf(\"%s\", tag);");
        sb.AppendLine("    for (i = 0; i < n; ++i) printf(\"%02X\", w[i]);");
        sb.AppendLine("    printf(\"\\n\");");
        sb.AppendLine("}");
        sb.AppendLine();
        return sb;
    }

    /// <summary>
    /// Encodes, then decodes into a zeroed struct that is never told a count.
    /// </summary>
    /// <remarks>
    /// <c>memset</c> before <c>DECODE</c> is load-bearing. It is what makes the count the decoder's job:
    /// leave the previous value in place and a decoder that reads the caller's count passes without ever
    /// looking at the frame.
    /// </remarks>
    private static string RoundTrip(string label, string setup, bool listValues)
    {
        var sb = new StringBuilder();
        sb.AppendLine("    { uint8_t wire[MAXB]; size_t n; msg_t back; pd_decode_result_t rc;");
        sb.AppendLine(setup);
        sb.AppendLine("      n = ENCODE(&msg, wire, sizeof(wire));");
        sb.AppendLine($"      printf(\"{label}LEN=%u\\n\", (unsigned)n);");
        sb.AppendLine($"      dump(\"{label}HEX=\", wire, n);");
        sb.AppendLine();
        sb.AppendLine("      memset(&back, 0, sizeof(back));   /* the count is the decoder's to find */");
        sb.AppendLine("      rc = DECODE(wire, n, &back);");
        sb.AppendLine($"      if (!rc.ok) {{ printf(\"{label}DECODED=FAILED\\n\"); return 1; }}");

        if (listValues)
        {
            sb.AppendLine($"      printf(\"{label}DECODED=%u:%02X,%02X,%02X:%02X\\n\", "
                + "(unsigned)back.samples_count, back.samples[0], back.samples[1], back.samples[2], back.tail);");
        }
        else
        {
            sb.AppendLine($"      printf(\"{label}DECODED=%u:%02X\\n\", "
                + "(unsigned)back.samples_count, back.tail);");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
        return sb.ToString();
    }

    private static string TerminatedDriver(string headerName, string messageName)
    {
        var sb = Preamble(headerName, messageName);
        sb.AppendLine("int main(void) {");
        sb.AppendLine("    msg_t msg;");
        sb.AppendLine("    memset(&msg, 0, sizeof(msg));");
        sb.AppendLine("    msg.lead = 0xAA; msg.tail = 0xBB;");
        sb.AppendLine();
        sb.Append(RoundTrip("", "      msg.samples_count = 3;\n"
            + "      msg.samples[0] = 0x11; msg.samples[1] = 0x22; msg.samples[2] = 0x33;", listValues: true));
        sb.Append(RoundTrip("EMPTY", "      msg.samples_count = 0;", listValues: false));
        sb.Append(RoundTrip("FULL", "      { int k; msg.samples_count = 8;\n"
            + "        for (k = 0; k < 8; ++k) msg.samples[k] = (uint8_t)(k + 1); }", listValues: false));
        sb.AppendLine("    return 0;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string FillingDriver(string headerName, string messageName) =>
        TerminatedDriver(headerName, messageName);
}
