using ProtoDesigner.Core.Model;

namespace ProtoDesigner.CodeGen.Runtime;

/// <summary>
/// A bit-level buffer. Writes and reads happen at bit granularity; the buffer grows a byte at a time.
/// This is the canonical reference implementation of the wire semantics — every generator must produce
/// code that matches it byte for byte on any well-formed message.
/// </summary>
/// <remarks>
/// Encoding model:
/// <list type="bullet">
///   <item>Whole-byte scalars honour <see cref="Endianness"/>. Sub-byte scalars are always packed MSB-first
///   in the storage byte, matching what most protocol specs describe (<see cref="BitOrder.MsbFirst"/>).</item>
///   <item>Bit-packed fields may straddle byte boundaries in Contiguous mode; the caller (validator)
///   guarantees that the layout is legal before we get here.</item>
/// </list>
/// This is deliberately small; generated code eventually inlines this logic.
/// </remarks>
public sealed class BitBuffer
{
    private readonly List<byte> _bytes;
    private int _bitCursor;

    public BitBuffer()
    {
        _bytes = new List<byte>();
        _bitCursor = 0;
    }

    public BitBuffer(byte[] bytes)
    {
        _bytes = new List<byte>(bytes);
        _bitCursor = 0;
    }

    /// <summary>Total number of bits written so far.</summary>
    public int BitLength => _bitCursor;

    /// <summary>Rounds up — the number of bytes needed to hold everything written so far.</summary>
    public int ByteLength => (_bitCursor + 7) / 8;

    /// <summary>Whether the read cursor has reached the end of the buffer.</summary>
    public bool AtEnd => _bitCursor >= _bytes.Count * 8;

    /// <summary>Move the cursor without reading — used to skip over padding.</summary>
    public void SkipBits(int bits) => _bitCursor += bits;

    /// <summary>Get the final byte array, padded to a whole byte.</summary>
    public byte[] ToArray()
    {
        // Ensure the last partial byte is included by extending if necessary.
        var needed = ByteLength;
        while (_bytes.Count < needed) _bytes.Add(0);
        return _bytes.ToArray();
    }

    // ---- write --------------------------------------------------------------------------------

    /// <summary>
    /// Writes an unsigned value at the current cursor. For widths &lt; 8, packs MSB-first within the
    /// storage byte. For 8/16/24/…/64-bit widths on byte-aligned cursors, honours <paramref name="endianness"/>.
    /// </summary>
    public void WriteUnsigned(ulong value, int width, Endianness endianness)
    {
        if (width is <= 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be 1..64.");

        // Byte-aligned whole-byte writes honour endianness on the fast path.
        if ((_bitCursor % 8) == 0 && (width % 8) == 0)
        {
            var byteCount = width / 8;
            EnsureCapacity(byteCount);
            var start = _bitCursor / 8;
            if (endianness == Endianness.Big)
            {
                for (var i = 0; i < byteCount; i++)
                    _bytes[start + i] = (byte)((value >> ((byteCount - 1 - i) * 8)) & 0xFF);
            }
            else
            {
                for (var i = 0; i < byteCount; i++)
                    _bytes[start + i] = (byte)((value >> (i * 8)) & 0xFF);
            }
            _bitCursor += width;
            return;
        }

        // Slow path: bit-by-bit, MSB-first inside the current byte.
        for (var i = width - 1; i >= 0; i--)
        {
            var bit = (int)((value >> i) & 1UL);
            EnsureCapacityBits(_bitCursor + 1);
            var byteIndex = _bitCursor / 8;
            var bitIndex = 7 - (_bitCursor % 8);
            if (bit != 0) _bytes[byteIndex] |= (byte)(1 << bitIndex);
            _bitCursor++;
        }
    }

    public void WriteSigned(long value, int width, Endianness endianness)
    {
        // Encode as two's complement in the given width, then defer to the unsigned writer.
        ulong mask = width == 64 ? ulong.MaxValue : ((1UL << width) - 1);
        var encoded = unchecked((ulong)value) & mask;
        WriteUnsigned(encoded, width, endianness);
    }

    /// <summary>Write raw bytes at a byte-aligned cursor. Used for sentinels and string data.</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        if ((_bitCursor % 8) != 0)
            throw new InvalidOperationException("WriteBytes requires a byte-aligned cursor.");
        var start = _bitCursor / 8;
        EnsureCapacity(data.Length);
        for (var i = 0; i < data.Length; i++) _bytes[start + i] = data[i];
        _bitCursor += data.Length * 8;
    }

    /// <summary>Pad with zero bits until the cursor sits on the next multiple of <paramref name="alignmentBits"/>.</summary>
    public void PadTo(int alignmentBits)
    {
        if (alignmentBits <= 0) return;
        var mod = _bitCursor % alignmentBits;
        if (mod == 0) return;
        var pad = alignmentBits - mod;
        _bitCursor += pad;
        EnsureCapacityBits(_bitCursor);
    }

    // ---- read ---------------------------------------------------------------------------------

    public ulong ReadUnsigned(int width, Endianness endianness)
    {
        if (width is <= 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be 1..64.");

        if ((_bitCursor % 8) == 0 && (width % 8) == 0)
        {
            var byteCount = width / 8;
            if (_bitCursor + width > _bytes.Count * 8)
                throw new EndOfStreamException($"Attempted to read {byteCount} bytes past the end of the buffer.");
            var start = _bitCursor / 8;
            ulong result = 0;
            if (endianness == Endianness.Big)
            {
                for (var i = 0; i < byteCount; i++)
                    result = (result << 8) | _bytes[start + i];
            }
            else
            {
                for (var i = byteCount - 1; i >= 0; i--)
                    result = (result << 8) | _bytes[start + i];
            }
            _bitCursor += width;
            return result;
        }

        ulong value = 0;
        for (var i = 0; i < width; i++)
        {
            if (_bitCursor + 1 > _bytes.Count * 8)
                throw new EndOfStreamException("Read past the end of the buffer.");
            var byteIndex = _bitCursor / 8;
            var bitIndex = 7 - (_bitCursor % 8);
            var bit = (_bytes[byteIndex] >> bitIndex) & 1;
            value = (value << 1) | (uint)bit;
            _bitCursor++;
        }
        return value;
    }

    public long ReadSigned(int width, Endianness endianness)
    {
        var raw = ReadUnsigned(width, endianness);
        if (width == 64) return unchecked((long)raw);

        // Sign-extend from `width` to 64.
        var signBit = 1UL << (width - 1);
        if ((raw & signBit) == 0) return (long)raw;
        var mask = ~((1UL << width) - 1);   // upper bits set
        return unchecked((long)(raw | mask));
    }

    public byte[] ReadBytes(int byteCount)
    {
        if ((_bitCursor % 8) != 0)
            throw new InvalidOperationException("ReadBytes requires a byte-aligned cursor.");
        if (_bitCursor + byteCount * 8 > _bytes.Count * 8)
            throw new EndOfStreamException("Read past the end of the buffer.");
        var start = _bitCursor / 8;
        var result = new byte[byteCount];
        for (var i = 0; i < byteCount; i++) result[i] = _bytes[start + i];
        _bitCursor += byteCount * 8;
        return result;
    }

    // ---- capacity -----------------------------------------------------------------------------

    private void EnsureCapacity(int extraBytes)
    {
        var needed = (_bitCursor / 8) + extraBytes;
        while (_bytes.Count < needed) _bytes.Add(0);
    }

    private void EnsureCapacityBits(int neededBits)
    {
        var neededBytes = (neededBits + 7) / 8;
        while (_bytes.Count < neededBytes) _bytes.Add(0);
    }
}
