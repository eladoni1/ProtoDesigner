using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ProtoDesigner.CodeGen.CSharp;
using ProtoDesigner.CodeGen.Runtime;

namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// Compiles the generated C# codec, runs it, and checks its bytes against the reference codec.
/// </summary>
/// <remarks>
/// <para>
/// The C target's equivalent needs MSVC and only runs on Windows. This one needs nothing: Roslyn compiles
/// the emitted source in memory and reflection calls into it, so the C# codec is checked on every machine
/// that can run the suite at all.
/// </para>
/// <para>
/// The reference codec is the independent implementation the whole cross-check suite measures against —
/// it walks the IR generically rather than emitting text — so agreeing with it is a real check. Comparing
/// against the C generator instead would only prove the two agree with each other, and the thing actually
/// under test is whether both write the frame the layout declares.
/// </para>
/// <para>
/// Every case round-trips as well as encodes. Encoding alone would miss a decoder that reads the right
/// bytes back into the wrong field, which is exactly the class of defect a cursor bug produces.
/// </para>
/// </remarks>
public class CSharpWireCrossCheck
{
    private const string Ns = "Proto";

    public static TheoryData<string> Corpora()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[]
        {
            "scalars", "packed-bits", "struct-and-array", "dynamic-array",
            "length-prefixed-array", "quantized", "raw-floats", "biased-signed", "shared-struct",
        })
        {
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Corpora))]
    public void The_generated_csharp_agrees_with_the_reference_codec(string corpusName)
    {
        var entry = Corpus.All().Single(c => c.Name == corpusName);
        var (project, bus) = entry.Factory();
        var ir = new IrBuilder().Build(project, bus);

        var assembly = Compile(new CSharpGenerator().Generate(ir, new GeneratorOptions(Namespace: Ns)));
        var codec = new ReferenceCodec();

        foreach (var message in ir.Messages)
        {
            var values = Sample(message);
            var expected = codec.Encode(message, values);

            var type = assembly.GetType($"{Ns}.{CSharpNaming.TypeName(message.Name)}")
                ?? throw new InvalidOperationException($"no generated type for '{message.Name}'");

            var instance = Activator.CreateInstance(type)!;
            foreach (var (path, value) in values) Assign(instance, type, message, path, value);

            var maxBytes = (int)type.GetField("MaxBytes")!.GetRawConstantValue()!;
            var wire = new byte[maxBytes];
            var written = (int)type.GetMethod("ConvertToWire")!.Invoke(instance, new object[] { wire })!;

            Assert.Equal(expected, wire.Take(written).ToArray());

            // Round trip: the same bytes have to come back as the same values, into a fresh instance that
            // was never told anything.
            var back = Activator.CreateInstance(type)!;
            var result = type.GetMethod("ConvertToHost")!
                .Invoke(null, new object[] { wire, written, back })!;
            Assert.True((bool)result.GetType().GetProperty("Ok")!.GetValue(result)!,
                $"'{message.Name}' did not decode the frame it had just written");

            var reread = new byte[maxBytes];
            var rewritten = (int)type.GetMethod("ConvertToWire")!.Invoke(back, new object[] { reread })!;
            Assert.Equal(wire.Take(written).ToArray(), reread.Take(rewritten).ToArray());
        }
    }

    /// <summary>
    /// A fixed region that ends mid-byte, followed by another region.
    /// </summary>
    /// <remarks>
    /// The corpus does not reach this: every entry's first region happens to land on a byte boundary, so
    /// the pad-to-byte at the end of a fixed region is a no-op throughout and deleting it changes nothing.
    /// Here four bits of flags sit in front of the count, so region 1 starts at bit 16 only because the
    /// pad moved it there — drop the pad and every byte after it shifts by four bits.
    /// </remarks>
    [Fact]
    public void A_region_ending_mid_byte_is_padded_before_the_next_one()
    {
        var project = new Project("PadSample");
        var u8 = project.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = project.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));

        var count = new FieldBinding(FieldId.New(), "count", u8.Id);
        var payload = project.Types.Add(new ArrayType(TypeId.New(), "Payload", u8.Id,
            new ArrayLength.CountFromField(count.Id, 8)));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var message = new Message(MessageId.New(), "Padded") { WireId = 1 };
        message.Fields.Add(count);
        message.Fields.Add(new FieldBinding(FieldId.New(), "flags", u8.Id, FieldEncoding.Packed(4)));
        message.Fields.Add(new FieldBinding(FieldId.New(), "payload", payload.Id));
        message.Fields.Add(new FieldBinding(FieldId.New(), "crc", u16.Id));
        bus.Messages.Add(message);
        project.Buses.Add(bus);

        var ir = new IrBuilder().Build(project, bus);
        var m = ir.Messages.Single();

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["flags"] = 5L,
            ["count"] = 2L,
            ["payload"] = new object[] { 0x11L, 0x22L },
            ["crc"] = 0xBEEFL,
        };

        var expected = new ReferenceCodec().Encode(m, values);

        var assembly = Compile(new CSharpGenerator().Generate(ir, new GeneratorOptions(Namespace: Ns)));
        var type = assembly.GetType($"{Ns}.Padded")!;
        var instance = Activator.CreateInstance(type)!;
        foreach (var (path, value) in values) Assign(instance, type, m, path, value);

        var wire = new byte[(int)type.GetField("MaxBytes")!.GetRawConstantValue()!];
        var written = (int)type.GetMethod("ConvertToWire")!.Invoke(instance, new object[] { wire })!;

        Assert.Equal(expected, wire.Take(written).ToArray());

        // The decoder has to skip the same pad the encoder wrote, or every field after it is read four
        // bits early. Round-tripping is what makes the alignment on that side observable at all.
        var back = Activator.CreateInstance(type)!;
        var result = type.GetMethod("ConvertToHost")!.Invoke(null, new object[] { wire, written, back })!;
        Assert.True((bool)result.GetType().GetProperty("Ok")!.GetValue(result)!);

        var reread = new byte[wire.Length];
        var rewritten = (int)type.GetMethod("ConvertToWire")!.Invoke(back, new object[] { reread })!;
        Assert.Equal(expected, reread.Take(rewritten).ToArray());
    }

    /// <summary>
    /// An array of structs, checked against bytes worked out by hand rather than by either codec.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference codec deliberately refuses composite elements — its array path describes an element
    /// by one primitive kind — so it cannot be the oracle here, which is why this case is not in the
    /// corpus theory above. The expected bytes are the ones <see cref="CStructArrayCrossCheck"/> derives
    /// by hand from the declared layout, and they are ground truth for the C target already.
    /// </para>
    /// <para>
    /// That makes this the strongest check in the file: two independently written generators, in two
    /// languages, agreeing with a third answer that neither produced. It also runs anywhere, where the C
    /// side of the same claim needs MSVC.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_array_of_structs_writes_the_bytes_the_layout_declares()
    {
        const string expectedHex = "021122334401DDCCBBAA0000803F01020304EFBEADDE";

        var (project, bus) = Corpus.StructArray();
        var ir = new IrBuilder().Build(project, bus);
        var assembly = Compile(new CSharpGenerator().Generate(ir, new GeneratorOptions(Namespace: Ns)));

        var type = assembly.GetType($"{Ns}.Bundle")!;
        var msg = Activator.CreateInstance(type)!;

        Set(type, msg, "ReadingCount", (byte)2);
        var readings = (Array)type.GetField("Readings")!.GetValue(msg)!;
        SetElement(readings, 0, ("Channel", (byte)0x11), ("Value", (byte)0x22));
        SetElement(readings, 1, ("Channel", (byte)0x33), ("Value", (byte)0x44));

        Set(type, msg, "BlockCount", (byte)1);
        var blocks = (Array)type.GetField("Blocks")!.GetValue(msg)!;
        SetElement(blocks, 0, ("ChannelId", 0xAABBCCDDu), ("Scale", 1.0f));
        var block = blocks.GetValue(0)!;
        var samples = (byte[])block.GetType().GetField("Samples")!.GetValue(block)!;
        for (var i = 0; i < 4; i++) samples[i] = (byte)(i + 1);

        Set(type, msg, "Crc", 0xDEADBEEFu);

        var wire = new byte[(int)type.GetField("MaxBytes")!.GetRawConstantValue()!];
        var written = (int)type.GetMethod("ConvertToWire")!.Invoke(msg, new object[] { wire })!;

        Assert.Equal(expectedHex, Convert.ToHexString(wire.Take(written).ToArray()));

        // And back: the array elements have to arrive constructed, or this throws before it compares.
        var back = Activator.CreateInstance(type)!;
        var result = type.GetMethod("ConvertToHost")!.Invoke(null, new object[] { wire, written, back })!;
        Assert.True((bool)result.GetType().GetProperty("Ok")!.GetValue(result)!);

        var reread = new byte[wire.Length];
        var rewritten = (int)type.GetMethod("ConvertToWire")!.Invoke(back, new object[] { reread })!;
        Assert.Equal(expectedHex, Convert.ToHexString(reread.Take(rewritten).ToArray()));
    }

    private static void Set(Type type, object target, string member, object value) =>
        type.GetField(member)!.SetValue(target, value);

    private static void SetElement(Array array, int index, params (string Member, object Value)[] members)
    {
        var element = array.GetValue(index)
            ?? throw new InvalidOperationException(
                $"element {index} is null — the declaration should have constructed it");

        foreach (var (member, value) in members)
            element.GetType().GetField(member)!.SetValue(element, value);

        array.SetValue(element, index);
    }

    /// <summary>
    /// A deterministic value for every field, chosen to fit the width and the transform.
    /// </summary>
    /// <remarks>
    /// Values are derived from each field's own declaration rather than written out per corpus entry, so
    /// adding a corpus case covers it here without a second edit — and so no case can quietly be given a
    /// value that dodges the part of the encoding it was added to exercise.
    /// </remarks>
    private static Dictionary<string, object?> Sample(IrMessage message)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        // Count fields first: an array's length reads one, so it has to be settled before the array is.
        foreach (var f in message.Fields)
        {
            if (f.Kind != IrFieldKind.Array || f.Array is not { } info) continue;
            if (info.CountFieldIndex is not { } ci) continue;

            var take = Math.Min(3, info.MaxElements);
            counts[message.Fields[ci].Path] = take;
        }

        foreach (var f in message.Fields)
        {
            if (f.Kind == IrFieldKind.Array && f.Array is { } info)
            {
                var take = info.Kind == IrArrayKind.Fixed
                    ? info.ElementCount!.Value
                    : Math.Min(3, info.MaxElements);

                if (info.CountFieldIndex is { } ci) take = counts[message.Fields[ci].Path];

                values[f.Path] = Enumerable.Range(0, take)
                    .Select(i => Scalar(info.ElementPrimitive, info.ElementBits, f.Transform, i + 1))
                    .ToArray();
                continue;
            }

            values[f.Path] = counts.TryGetValue(f.Path, out var n)
                ? Convert.ToInt64(n)
                : Scalar(f.Primitive, f.BitWidth, f.Transform, 1);
        }

        return values;
    }

    /// <summary>One value that fits <paramref name="bits"/> once the transform has been applied.</summary>
    private static object Scalar(PrimitiveKind kind, int bits, ScalarTransform transform, int seed)
    {
        if (kind == PrimitiveKind.Bool) return seed % 2 == 1;

        if (kind.IsFloat() && transform.IsIdentity)
            return kind == PrimitiveKind.F32 ? (object)(1.5f * seed) : 2.25d * seed;

        // The widest wire code the field can hold, then a small one inside it — staying clear of the top
        // so a rounding transform cannot push the result over the edge and out of range.
        var codes = bits >= 63 ? long.MaxValue : (1L << bits) - 1;
        var code = Math.Min(seed, Math.Max(0, codes - 1));

        // wire = (value - offset) / scale, so value = code * scale + offset.
        var value = (code * transform.Scale) + transform.Offset;

        if (kind.IsFloat()) return kind == PrimitiveKind.F32 ? (object)(float)value : (double)value;
        return decimal.ToInt64(decimal.Round(value));
    }

    /// <summary>
    /// Writes one sampled value onto the generated object, walking a dotted path into nested structs.
    /// </summary>
    private static void Assign(object root, Type rootType, IrMessage message, string path, object? value)
    {
        var owner = root;
        var ownerType = rootType;
        var enclosing = rootType.Name;

        var segments = path.Split('.');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var step = ownerType.GetField(CSharpNaming.MemberNameIn(enclosing, segments[i]))
                ?? throw new InvalidOperationException($"no member '{segments[i]}' on {ownerType.Name}");
            owner = step.GetValue(owner)!;
            ownerType = owner.GetType();
            enclosing = ownerType.Name;
        }

        var name = CSharpNaming.MemberNameIn(enclosing, segments[^1]);
        var field = ownerType.GetField(name)
            ?? throw new InvalidOperationException($"no member '{name}' on {ownerType.Name}");

        if (value is object[] items)
        {
            var target = (Array)field.GetValue(owner)!;
            var element = field.FieldType.GetElementType()!;
            for (var i = 0; i < items.Length && i < target.Length; i++)
                target.SetValue(ConvertTo(items[i], element), i);

            // A self-describing array carries its own count; one counted by a field does not, and setting
            // the field's value is what the count-field sample above already did.
            var countField = ownerType.GetField(name + "Count");
            countField?.SetValue(owner, (uint)items.Length);
            return;
        }

        field.SetValue(owner, ConvertTo(value, field.FieldType));
    }

    private static object? ConvertTo(object? value, Type target)
    {
        if (value is null) return null;
        if (target.IsEnum) return Enum.ToObject(target, Convert.ToInt64(value));
        return Convert.ChangeType(value, target);
    }

    private static Assembly Compile(GeneratedFileSet set)
    {
        var sources = set.Files
            .Where(f => f.RelativePath.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Contents, path: f.RelativePath))
            .ToList();

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var compilation = CSharpCompilation.Create(
            "GeneratedCodec", sources, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        Assert.True(result.Success,
            "generated C# did not compile:\n" + string.Join("\n",
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(e => $"  {e}")));

        return Assembly.Load(stream.ToArray());
    }
}
