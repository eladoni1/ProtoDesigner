namespace ProtoDesigner.CodeGen.Tests;

/// <summary>
/// The shared corpus of projects that both the golden-file tests and the round-trip tests run against.
/// Adding a protocol shape here exercises it in every suite at once — that's the point.
/// </summary>
internal static class Corpus
{
    /// <summary>Plain scalars at natural widths, one big-endian field to prove endianness flows through.</summary>
    public static (Project Project, Bus Bus) Scalars()
    {
        var p = new Project("ScalarSample");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = p.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));
        var i32 = p.Types.Add(new ParameterType(TypeId.New(), "i32", PrimitiveKind.I32));

        var bus = new Bus(BusId.New(), "Main", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Reading") { WireId = 3 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "id", u8.Id));
        m.Fields.Add(new FieldBinding(FieldId.New(), "sequence", u16.Id,
            new FieldEncoding { Endianness = Endianness.Big }));
        m.Fields.Add(new FieldBinding(FieldId.New(), "delta", i32.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>The headline case: a 4-bit enum and a range-compressed value sharing a byte.</summary>
    public static (Project Project, Bus Bus) PackedBits()
    {
        var p = new Project("PackedSample");
        var u16 = p.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));
        var temperature = p.Types.Add(new ParameterType(TypeId.New(), "Temperature",
            PrimitiveKind.U16, new NumericRange(1000, 1015)));
        var mode = p.Types.Add(new EnumType(TypeId.New(), "Mode", PrimitiveKind.U32)
            .With("Idle", 0).With("Arming", 1).With("Running", 5).With("Fault", 10));

        var bus = new Bus(BusId.New(), "Control", Transport.Uart);
        var m = new Message(MessageId.New(), "Status") { WireId = 9 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "mode", mode.Id, FieldEncoding.Packed(4)));
        m.Fields.Add(new FieldBinding(FieldId.New(), "temperature", temperature.Id,
            new FieldEncoding { BitWidth = 4, AllowBitPacking = true, Transform = new ScalarTransform(1000, 1) }));
        m.Fields.Add(new FieldBinding(FieldId.New(), "checksum", u16.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>A struct flattened inline, plus a static array.</summary>
    public static (Project Project, Bus Bus) StructAndArray()
    {
        var p = new Project("CompositeSample");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = p.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));
        var u32 = p.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var header = p.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "messageId", u8.Id),
                  new FieldBinding(FieldId.New(), "timestamp", u32.Id)));
        var samples = p.Types.Add(new ArrayType(TypeId.New(), "Samples", u16.Id, new ArrayLength.Fixed(4)));

        var bus = new Bus(BusId.New(), "Telemetry", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Frame") { WireId = 12 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));
        m.Fields.Add(new FieldBinding(FieldId.New(), "samples", samples.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>
    /// Arrays whose elements are structs — the shape a device protocol reaches for constantly:
    /// "N readings, each a (channel, value) pair".
    /// </summary>
    /// <remarks>
    /// Three things in one message, because each needs different emission. <c>readings</c> is the
    /// minimal composite element, two bytes wide. <c>blocks</c> has a wider element carrying a raw
    /// float and a fixed array, so the generator has to run an inner loop inside the element loop.
    /// The trailing <c>crc</c> proves the region after the last variable one still gets constant
    /// offsets.
    /// </remarks>
    public static (Project Project, Bus Bus) StructArray()
    {
        var p = new Project("StructArraySample");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u32 = p.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));
        var f32 = p.Types.Add(new ParameterType(TypeId.New(), "f32", PrimitiveKind.F32));

        var reading = p.Types.Add(new StructType(TypeId.New(), "Reading")
            .With(new FieldBinding(FieldId.New(), "channel", u8.Id),
                  new FieldBinding(FieldId.New(), "value", u8.Id)));

        var samples = p.Types.Add(new ArrayType(TypeId.New(), "Samples", u8.Id,
            new ArrayLength.Fixed(4)));
        var block = p.Types.Add(new StructType(TypeId.New(), "ChannelBlock")
            .With(new FieldBinding(FieldId.New(), "channelId", u32.Id),
                  new FieldBinding(FieldId.New(), "scale", f32.Id),
                  new FieldBinding(FieldId.New(), "samples", samples.Id)));

        var readingCount = new FieldBinding(FieldId.New(), "readingCount", u8.Id);
        var readings = p.Types.Add(new ArrayType(TypeId.New(), "Readings", reading.Id,
            new ArrayLength.CountFromField(readingCount.Id, 8)));

        var blockCount = new FieldBinding(FieldId.New(), "blockCount", u8.Id);
        var blocks = p.Types.Add(new ArrayType(TypeId.New(), "Blocks", block.Id,
            new ArrayLength.CountFromField(blockCount.Id, 2)));

        var bus = new Bus(BusId.New(), "Sensors", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Bundle") { WireId = 31 };
        m.Fields.Add(readingCount);
        m.Fields.Add(new FieldBinding(FieldId.New(), "readings", readings.Id));
        m.Fields.Add(blockCount);
        m.Fields.Add(new FieldBinding(FieldId.New(), "blocks", blocks.Id));
        m.Fields.Add(new FieldBinding(FieldId.New(), "crc", u32.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>A dynamic array driven by a count field, with a fixed field after it — the region-split case.</summary>
    public static (Project Project, Bus Bus) DynamicArray()
    {
        var p = new Project("DynamicSample");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = p.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));

        var count = new FieldBinding(FieldId.New(), "count", u8.Id);
        var payload = p.Types.Add(new ArrayType(TypeId.New(), "Payload", u8.Id,
            new ArrayLength.CountFromField(count.Id, 32)));

        var bus = new Bus(BusId.New(), "Bulk", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Batch") { WireId = 21 };
        m.Fields.Add(count);
        m.Fields.Add(new FieldBinding(FieldId.New(), "payload", payload.Id));
        m.Fields.Add(new FieldBinding(FieldId.New(), "crc", u16.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>
    /// A dynamic array that carries its own count inline, with a fixed field after it.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="DynamicArray"/>, and here because its absence was the whole reason a
    /// length-prefixed array never reached a generator: the shape had layout tests and no corpus entry, so
    /// nothing ever asked the IR builder, the C generator or the reference codec to handle it. Every suite
    /// runs over this list, which is what makes one entry cover all of them.
    /// </remarks>
    public static (Project Project, Bus Bus) LengthPrefixedArray()
    {
        var p = new Project("PrefixedSample");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = p.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));

        // An 8-bit prefix for a capacity of 24: wide enough, and byte-aligned so the elements after it
        // stay addressable under the default byte padding.
        var payload = p.Types.Add(new ArrayType(TypeId.New(), "Prefixed", u8.Id,
            new ArrayLength.LengthPrefixed(8, 24)));

        var bus = new Bus(BusId.New(), "Framed", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Frame") { WireId = 31 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "kind", u8.Id));
        m.Fields.Add(new FieldBinding(FieldId.New(), "payload", payload.Id));
        m.Fields.Add(new FieldBinding(FieldId.New(), "trailer", u16.Id));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>
    /// Floating-point hosts quantized onto narrow integer wires — what the editor now produces whenever a
    /// range is narrowed. The scales are irrational-ish fractions, which is what makes the precision of
    /// the emitted literal matter.
    /// </summary>
    public static (Project Project, Bus Bus) Quantized()
    {
        var p = new Project("QuantizedSample");

        // -40..70 in one byte: offset at the range minimum, scale 110/255.
        var temperature = p.Types.Add(new ParameterType(TypeId.New(), "Temperature",
            PrimitiveKind.F64, new NumericRange(-40, 70)) { WireForm = WireForm.Unsigned, WireBits = 8 });

        // 0..100 in one byte: scale 100/255.
        var battery = p.Types.Add(new ParameterType(TypeId.New(), "BatteryPercent",
            PrimitiveKind.F32, new NumericRange(0, 100)) { WireForm = WireForm.Unsigned, WireBits = 8 });

        var bus = new Bus(BusId.New(), "Sensors", Transport.Uart);
        var m = new Message(MessageId.New(), "Ambient") { WireId = 5 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "temperature", temperature.Id, Quantize(temperature, 8)));
        m.Fields.Add(new FieldBinding(FieldId.New(), "battery", battery.Id, Quantize(battery, 8)));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>
    /// The same encoding the editor derives for an unsigned wire: offset at the range minimum, scale at
    /// the finest the width can carry. Built from <see cref="BitMath.MinimumScale"/> so the corpus and the
    /// app cannot drift apart.
    /// </summary>
    private static FieldEncoding Quantize(ParameterType type, int bits)
    {
        var range = type.Range!.Value;
        return new FieldEncoding
        {
            BitWidth = bits,
            AllowBitPacking = true,
            Transform = new ScalarTransform(range.Min, BitMath.MinimumScale(range, bits)),
        };
    }

    /// <summary>
    /// A float sent at its natural width with no transform, so its IEEE bit pattern goes on the wire
    /// verbatim. This is what <see cref="WireForm.Float"/> means, and the case a value-to-integer cast
    /// silently destroys.
    /// </summary>
    public static (Project Project, Bus Bus) RawFloats()
    {
        var p = new Project("RawFloatSample");
        var f64 = p.Types.Add(new ParameterType(TypeId.New(), "Reading", PrimitiveKind.F64)
        {
            WireForm = WireForm.Float,
            WireBits = 64,
        });

        var bus = new Bus(BusId.New(), "Analog", Transport.Ethernet);
        var m = new Message(MessageId.New(), "Raw") { WireId = 2 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "reading", f64.Id,
            new FieldEncoding { BitWidth = 64, AllowBitPacking = true }));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>
    /// A signed host whose transform biases its range non-negative. The wire codes run 0..255, so the
    /// field must be written and read <em>unsigned</em> even though the host type is signed — the case
    /// that silently corrupted every value from code 0x80 up while signedness was taken from the host.
    /// </summary>
    public static (Project Project, Bus Bus) BiasedSigned()
    {
        var p = new Project("BiasedSignedSample");
        var range = new NumericRange(-100, 100);

        var tilt = p.Types.Add(new ParameterType(TypeId.New(), "Tilt", PrimitiveKind.I16, range)
        {
            WireForm = WireForm.Unsigned,
            WireBits = 8,
        });

        var bus = new Bus(BusId.New(), "Attitude", Transport.Uart);
        var m = new Message(MessageId.New(), "Angles") { WireId = 4 };
        m.Fields.Add(new FieldBinding(FieldId.New(), "tilt", tilt.Id, new FieldEncoding
        {
            BitWidth = 8,
            AllowBitPacking = true,
            Transform = new ScalarTransform(range.Min, BitMath.MinimumScale(range, 8)),
        }));
        bus.Messages.Add(m);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>
    /// One struct used by two messages, plus a struct nested inside another. Proves the shared type is
    /// declared once and referenced by name rather than flattened into each user.
    /// </summary>
    public static (Project Project, Bus Bus) SharedStruct()
    {
        var p = new Project("SharedStructSample");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u16 = p.Types.Add(new ParameterType(TypeId.New(), "u16", PrimitiveKind.U16));
        var u32 = p.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var header = p.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "messageId", u8.Id),
                  new FieldBinding(FieldId.New(), "timestamp", u32.Id)));

        // A struct inside a struct: the inner one must be declared before the outer.
        var envelope = p.Types.Add(new StructType(TypeId.New(), "Envelope")
            .With(new FieldBinding(FieldId.New(), "head", header.Id),
                  new FieldBinding(FieldId.New(), "sequence", u16.Id)));

        var bus = new Bus(BusId.New(), "Shared", Transport.Ethernet);

        var first = new Message(MessageId.New(), "Alpha") { WireId = 1 };
        first.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));
        first.Fields.Add(new FieldBinding(FieldId.New(), "value", u16.Id));

        var second = new Message(MessageId.New(), "Beta") { WireId = 2 };
        second.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));
        second.Fields.Add(new FieldBinding(FieldId.New(), "envelope", envelope.Id));

        bus.Messages.Add(first);
        bus.Messages.Add(second);
        p.Buses.Add(bus);
        return (p, bus);
    }

    /// <summary>
    /// One project, two buses, both using the same Header. Types are project-wide, so this is one struct
    /// used twice — and both bus headers have to be includable in the same translation unit.
    /// </summary>
    public static (Project Project, Bus First) TwoBusesSharingAStruct()
    {
        var p = new Project("TwoBusSample");
        var u8 = p.Types.Add(new ParameterType(TypeId.New(), "u8", PrimitiveKind.U8));
        var u32 = p.Types.Add(new ParameterType(TypeId.New(), "u32", PrimitiveKind.U32));

        var header = p.Types.Add(new StructType(TypeId.New(), "Header")
            .With(new FieldBinding(FieldId.New(), "messageId", u8.Id),
                  new FieldBinding(FieldId.New(), "timestamp", u32.Id)));

        var primary = new Bus(BusId.New(), "Primary", Transport.Ethernet);
        var m1 = new Message(MessageId.New(), "Status") { WireId = 1 };
        m1.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));
        primary.Messages.Add(m1);

        var backup = new Bus(BusId.New(), "Backup", Transport.Uart);
        var m2 = new Message(MessageId.New(), "Heartbeat") { WireId = 1 };   // ids restart per bus
        m2.Fields.Add(new FieldBinding(FieldId.New(), "header", header.Id));
        backup.Messages.Add(m2);

        p.Buses.Add(primary);
        p.Buses.Add(backup);
        return (p, primary);
    }

    public static ProtocolIr BuildIr((Project Project, Bus Bus) sample) =>
        new IrBuilder().Build(sample.Project, sample.Bus);

    public static IEnumerable<(string Name, Func<(Project, Bus)> Factory)> All()
    {
        yield return ("scalars", Scalars);
        yield return ("packed-bits", PackedBits);
        yield return ("struct-and-array", StructAndArray);
        yield return ("struct-array", StructArray);
        yield return ("dynamic-array", DynamicArray);
        yield return ("length-prefixed-array", LengthPrefixedArray);
        yield return ("quantized", Quantized);
        yield return ("raw-floats", RawFloats);
        yield return ("biased-signed", BiasedSigned);
        yield return ("shared-struct", SharedStruct);
    }
}
