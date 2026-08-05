namespace ProtoDesigner.Core.Ir;

/// <summary>
/// How long a message is on the wire, region by region. This is the single definition every generator's
/// emitted <c>OnWireLength</c> mirrors, and the one the tests pin against what the codec actually writes.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the answer is not simply <see cref="IrMessage.MaxBits"/>. A message holding a
/// dynamic array is shorter than its maximum whenever the array is not full, and the encoder pads to a
/// byte boundary at the end of each fixed region — so the length is a walk over the regions, not a
/// single number.
/// </para>
/// <para>
/// Keeping it here rather than inside each generator is what stops the emitted formula drifting from the
/// emitted encoder. If this walk changes, every target changes with it.
/// </para>
/// </remarks>
public static class WireLength
{
    /// <summary>
    /// The bit length of <paramref name="message"/> when each variable region holds the element count
    /// given by <paramref name="elementCountForRegion"/>.
    /// </summary>
    /// <param name="message">The message to measure.</param>
    /// <param name="elementCountForRegion">
    /// Called once per variable region with the region's index; returns how many elements it carries.
    /// Counts above the region's declared capacity are clamped, matching the generated encoder's
    /// <c>i &lt; MaxElements</c> guard — an over-long array is truncated on the wire, so reporting the
    /// untruncated length would disagree with what was actually written.
    /// </param>
    public static int Bits(IrMessage message, Func<int, int> elementCountForRegion)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(elementCountForRegion);

        var bits = 0;

        foreach (var region in message.Regions)
        {
            if (region.Kind == IrRegionKind.Fixed)
            {
                // Fields sit at offsets relative to the region's start, so the region contributes its
                // full span whether or not a field happens to end there.
                bits += region.MaxBits;

                // The encoder closes every fixed region with pad_to(8).
                bits = AlignUp(bits, 8);
                continue;
            }

            var count = Math.Clamp(elementCountForRegion(region.Index), 0, region.MaxElements);
            bits += region.PrefixBits + (count * region.ElementBits);
        }

        return bits;
    }

    /// <summary>The byte length, rounding up — a message ending mid-byte still occupies that byte.</summary>
    public static int Bytes(IrMessage message, Func<int, int> elementCountForRegion) =>
        (Bits(message, elementCountForRegion) + 7) / 8;

    /// <summary>
    /// The length of a message that has no variable regions. Throws for a variable-length message, where
    /// the question has no single answer.
    /// </summary>
    public static int FixedBytes(IrMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Regions.Any(r => r.Kind == IrRegionKind.Variable))
            throw new InvalidOperationException(
                $"Message '{message.Name}' has a variable region; its length depends on the element count.");

        return Bytes(message, _ => 0);
    }

    /// <summary>True when every region is fixed, so the length is a compile-time constant.</summary>
    public static bool IsFixedSize(IrMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Regions.All(r => r.Kind == IrRegionKind.Fixed);
    }

    private static int AlignUp(int value, int alignment)
    {
        if (alignment <= 0) return value;
        var mod = value % alignment;
        return mod == 0 ? value : value + (alignment - mod);
    }
}
