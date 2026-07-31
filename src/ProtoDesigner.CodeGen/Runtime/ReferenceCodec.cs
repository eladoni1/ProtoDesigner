using ProtoDesigner.Core.Ir;
using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.Runtime;

/// <summary>
/// The canonical encode/decode implementation, driven entirely by <see cref="IrMessage"/>. This is
/// what test suites use to prove the semantics — and what the WPF live preview calls to show what
/// a sample instance would look like on the wire.
/// </summary>
/// <remarks>
/// Values are addressed by their field <see cref="IrField.Path"/>. A field of scalar / enum kind
/// holds an <c>IConvertible</c> number (or a <see cref="bool"/>). An array field holds an
/// <see cref="IReadOnlyList{Object}"/> of element values. That keeps this reference completely
/// generic; a real C++/C# generator emits strongly typed structs and calls into equivalent bit
/// operations directly.
/// </remarks>
public sealed class ReferenceCodec
{
    public byte[] Encode(IrMessage message, IReadOnlyDictionary<string, object?> values)
    {
        var buf = new BitBuffer();
        var byPath = new Dictionary<string, IrField>(message.Fields.Count);
        foreach (var f in message.Fields) byPath[f.Path] = f;

        // Track cursor per region because dynamic arrays split the layout. We write regions in order,
        // padding the tail of each fixed region to a whole byte if the layout said so.
        foreach (var region in message.Regions)
        {
            var fieldsInRegion = message.Fields.Where(f => f.RegionIndex == region.Index).ToList();

            var regionStart = buf.BitLength;

            foreach (var f in fieldsInRegion)
            {
                // Advance the buffer to the field's offset within the region (padding as needed).
                var target = regionStart + f.BitOffset;
                if (buf.BitLength < target) buf.SkipBits(target - buf.BitLength);
                WriteField(buf, message, f, values, byPath);
            }

            if (region.Kind == IrRegionKind.Fixed)
            {
                // Pad the trailing partial byte so the next region starts on a byte boundary.
                buf.PadTo(8);
            }
        }

        return buf.ToArray();
    }

    public Dictionary<string, object?> Decode(IrMessage message, byte[] bytes)
    {
        var buf = new BitBuffer(bytes);
        var values = new Dictionary<string, object?>();

        foreach (var region in message.Regions)
        {
            var fieldsInRegion = message.Fields.Where(f => f.RegionIndex == region.Index).ToList();
            var regionStart = buf.BitLength;

            foreach (var f in fieldsInRegion)
            {
                var target = regionStart + f.BitOffset;
                if (buf.BitLength < target) buf.SkipBits(target - buf.BitLength);
                values[f.Path] = ReadField(buf, message, f, values, region);
            }

            if (region.Kind == IrRegionKind.Fixed) buf.SkipBits((8 - (buf.BitLength % 8)) % 8);
        }

        return values;
    }

    // ---- encode ------------------------------------------------------------------------------

    private static void WriteField(BitBuffer buf, IrMessage message, IrField field,
        IReadOnlyDictionary<string, object?> values, Dictionary<string, IrField> byPath)
    {
        switch (field.Kind)
        {
            case IrFieldKind.Scalar:
            case IrFieldKind.EnumRef:
                WriteScalar(buf, field, GetValue(values, field.Path, defaultValue: 0));
                return;

            case IrFieldKind.Array when field.Array is { } arr:
                WriteArray(buf, message, field, arr, values);
                return;

            default:
                throw new InvalidOperationException($"Unsupported field kind {field.Kind} at {field.Path}.");
        }
    }

    private static void WriteScalar(BitBuffer buf, IrField field, object? raw)
    {
        // A float host with no transform puts its IEEE bit pattern on the wire verbatim.
        if (IsRawFloat(field.Primitive, field.Transform))
        {
            buf.WriteUnsigned(FloatBits(field.Primitive, raw), field.BitWidth, field.Endianness);
            return;
        }

        var value = ToDecimal(raw);
        var code = Quantize(field.Transform.ToWire(value));
        // The wire code must fit the width. Signed vs unsigned is decided by whether the transform
        // (or an inherently signed primitive) can produce negative codes.
        if (IsSigned(field.Primitive))
            buf.WriteSigned(code, field.BitWidth, field.Endianness);
        else
            buf.WriteUnsigned((ulong)code, field.BitWidth, field.Endianness);
    }

    private static void WriteArray(BitBuffer buf, IrMessage message, IrField field, IrArrayInfo arr,
        IReadOnlyDictionary<string, object?> values)
    {
        var list = GetList(values, field.Path);
        var maxCount = arr.Kind == IrArrayKind.Fixed ? arr.ElementCount!.Value : arr.MaxElements;

        // Length prefix (self-describing arrays) goes first, within the CURRENT (variable) region.
        if (arr.Kind == IrArrayKind.LengthPrefixed)
            buf.WriteUnsigned((ulong)list.Count, arr.PrefixBits, Endianness.Little);

        var count = arr.Kind == IrArrayKind.Fixed ? arr.ElementCount!.Value : list.Count;
        if (count > maxCount)
            throw new InvalidOperationException(
                $"Array '{field.Path}' has {count} elements but the maximum is {maxCount}.");

        for (var i = 0; i < count; i++)
        {
            var element = list.Count > i ? list[i] : 0;

            if (IsRawFloat(arr.ElementPrimitive, field.Transform))
            {
                buf.WriteUnsigned(FloatBits(arr.ElementPrimitive, element), arr.ElementBits, field.Endianness);
                continue;
            }

            var code = Quantize(field.Transform.ToWire(ToDecimal(element)));
            if (IsSigned(arr.ElementPrimitive))
                buf.WriteSigned(code, arr.ElementBits, field.Endianness);
            else
                buf.WriteUnsigned((ulong)code, arr.ElementBits, field.Endianness);
        }

        if (arr.Kind == IrArrayKind.Terminated)
            buf.WriteBytes(arr.Sentinel.ToArray());
    }

    // ---- decode ------------------------------------------------------------------------------

    private static object? ReadField(BitBuffer buf, IrMessage message, IrField field,
        Dictionary<string, object?> valuesSoFar, IrRegion region)
    {
        switch (field.Kind)
        {
            case IrFieldKind.Scalar:
            case IrFieldKind.EnumRef:
                return ReadScalar(buf, field);

            case IrFieldKind.Array when field.Array is { } arr:
                return ReadArray(buf, message, field, arr, valuesSoFar, region);

            default:
                throw new InvalidOperationException($"Unsupported field kind {field.Kind} at {field.Path}.");
        }
    }

    private static object ReadScalar(BitBuffer buf, IrField field)
    {
        if (IsRawFloat(field.Primitive, field.Transform))
            return BitsToFloat(field.Primitive, buf.ReadUnsigned(field.BitWidth, field.Endianness));

        long wire = IsSigned(field.Primitive)
            ? buf.ReadSigned(field.BitWidth, field.Endianness)
            : (long)buf.ReadUnsigned(field.BitWidth, field.Endianness);
        return field.Transform.FromWire(wire);
    }

    private static object ReadArray(BitBuffer buf, IrMessage message, IrField field, IrArrayInfo arr,
        Dictionary<string, object?> valuesSoFar, IrRegion region)
    {
        var count = arr.Kind switch
        {
            IrArrayKind.Fixed => arr.ElementCount!.Value,
            IrArrayKind.CountFromField => arr.CountFieldIndex is { } idx
                ? Convert.ToInt32(valuesSoFar[message.Fields[idx].Path], System.Globalization.CultureInfo.InvariantCulture)
                : throw new InvalidOperationException($"Array '{field.Path}' has no resolved count field."),
            IrArrayKind.LengthPrefixed => (int)buf.ReadUnsigned(arr.PrefixBits, Endianness.Little),
            _ => throw new NotSupportedException($"Array kind {arr.Kind} not yet supported by the reference codec."),
        };

        var list = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            if (IsRawFloat(arr.ElementPrimitive, field.Transform))
            {
                list.Add(BitsToFloat(arr.ElementPrimitive, buf.ReadUnsigned(arr.ElementBits, field.Endianness)));
                continue;
            }

            long wire = IsSigned(arr.ElementPrimitive)
                ? buf.ReadSigned(arr.ElementBits, field.Endianness)
                : (long)buf.ReadUnsigned(arr.ElementBits, field.Endianness);
            list.Add(field.Transform.FromWire(wire));
        }
        return list;
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static bool IsSigned(PrimitiveKind kind) =>
        kind is PrimitiveKind.I8 or PrimitiveKind.I16 or PrimitiveKind.I32 or PrimitiveKind.I64;

    private static bool IsFloat(PrimitiveKind kind) => kind is PrimitiveKind.F32 or PrimitiveKind.F64;

    /// <summary>
    /// A float host with no transform: the IEEE bit pattern goes on the wire untouched. The wire-encoding
    /// propagator guarantees the pairing — a float wire form produces no transform, and an integer wire
    /// form on a float host always produces one.
    /// </summary>
    private static bool IsRawFloat(PrimitiveKind kind, ScalarTransform t) => IsFloat(kind) && t.IsIdentity;

    private static ulong FloatBits(PrimitiveKind kind, object? raw)
    {
        var value = (double)ToDecimal(raw);
        return kind == PrimitiveKind.F32
            ? BitConverter.SingleToUInt32Bits((float)value)
            : BitConverter.DoubleToUInt64Bits(value);
    }

    private static decimal BitsToFloat(PrimitiveKind kind, ulong bits) => kind == PrimitiveKind.F32
        ? (decimal)BitConverter.UInt32BitsToSingle((uint)bits)
        : (decimal)BitConverter.UInt64BitsToDouble(bits);

    /// <summary>
    /// Rounds a scaled value to the nearest wire code, half away from zero — the exact rule the generated
    /// C++ applies via <c>protodesigner::quantize</c>. Truncating instead (which is what a plain cast to
    /// an integer does) would bias every reading down by up to a full code and put the top of a range out
    /// of reach: 70.0 on a -40..70 field scaled by 110/255 lands a hair under 255 and would store 254.
    /// </summary>
    private static long Quantize(decimal wire) =>
        decimal.ToInt64(decimal.Round(wire, MidpointRounding.AwayFromZero));

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0m,
        decimal d => d,
        bool b => b ? 1m : 0m,
        _ => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static object? GetValue(IReadOnlyDictionary<string, object?> values, string path, object defaultValue) =>
        values.TryGetValue(path, out var v) ? v : defaultValue;

    private static IReadOnlyList<object?> GetList(IReadOnlyDictionary<string, object?> values, string path)
    {
        if (!values.TryGetValue(path, out var v) || v is null) return Array.Empty<object?>();
        if (v is IEnumerable<object?> objList) return objList.ToArray();
        if (v is System.Collections.IEnumerable enumerable) return enumerable.Cast<object?>().ToArray();
        throw new InvalidOperationException($"Array field '{path}' is not enumerable.");
    }
}
