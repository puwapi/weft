namespace Weft.Core.Store;

/// <summary>
/// Splits data on boundaries determined by its content, not by offset.
/// </summary>
/// <remarks>
/// <para>This is what makes an edit cost one chunk instead of a whole file.
/// Fixed-size blocks shift on every insertion: adding one byte at the top of a
/// 386 KB document changes every block after it, and the whole file re-uploads.
/// Content-defined boundaries re-align a few kilobytes past the edit, so the rest
/// of the file is recognised as chunks the store already holds.</para>
///
/// <para>Implements FastCDC with normalised chunking (Xia et al., 2016): a
/// stricter mask is used before the target size and a looser one after, which
/// pulls the size distribution towards the average instead of leaving it
/// exponential.</para>
/// </remarks>
public static class FastCdc
{
    /// <summary>Smallest chunk. Below this no boundary is even looked for.</summary>
    public const int MinSize = 2 * 1024;

    /// <summary>Target average.</summary>
    public const int AvgSize = 8 * 1024;

    /// <summary>Hard ceiling. A cut is forced here whatever the content says.</summary>
    public const int MaxSize = 64 * 1024;

    // Masks from the FastCDC paper for an 8 KB target. MaskS has more bits set,
    // so a boundary is harder to hit and short chunks are discouraged; MaskL has
    // fewer, so past the average a boundary is found quickly. The measured
    // distribution is checked by test rather than assumed.
    private const ulong MaskS = 0x0003590703530000UL;
    private const ulong MaskL = 0x0000d90003530000UL;

    /// <summary>
    /// Length of the chunk starting at the beginning of <paramref name="data"/>.
    /// </summary>
    public static int NextCut(ReadOnlySpan<byte> data)
    {
        var n = data.Length;
        if (n <= MinSize) return n;
        if (n > MaxSize) n = MaxSize;

        // Hoisted out of the loops: this is indexed once per byte scanned.
        var gear = Gear.Table;

        var normal = Math.Min(AvgSize, n);
        ulong fp = 0;
        var i = MinSize;

        // Below the target size: strict mask, boundaries are rare.
        for (; i < normal; i++)
        {
            fp = (fp << 1) + gear[data[i]];
            if ((fp & MaskS) == 0) return i;
        }

        // Past the target size: loose mask, a boundary turns up quickly.
        for (; i < n; i++)
        {
            fp = (fp << 1) + gear[data[i]];
            if ((fp & MaskL) == 0) return i;
        }

        return n;
    }

    /// <summary>Chunk offsets and lengths over a whole buffer.</summary>
    public static IEnumerable<(int Offset, int Length)> Split(ReadOnlyMemory<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var len = NextCut(data.Span[offset..]);
            yield return (offset, len);
            offset += len;
        }
    }
}
