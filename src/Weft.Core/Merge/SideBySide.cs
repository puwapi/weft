namespace Weft.Core.Merge;

/// <summary>What one row of a side-by-side view holds.</summary>
public enum SideKind
{
    /// <summary>Both sides say the same thing.</summary>
    Same,

    /// <summary>Both sides have a line here and they differ.</summary>
    Changed,

    /// <summary>Only the left side has this line.</summary>
    OnlyLeft,

    /// <summary>Only the right side has this line.</summary>
    OnlyRight,
}

/// <param name="LeftNumber">1-based line number on the left, 0 when absent.</param>
/// <param name="RightNumber">1-based line number on the right, 0 when absent.</param>
public readonly record struct SideRow(SideKind Kind, string? Left, string? Right, int LeftNumber, int RightNumber);

/// <summary>
/// Aligns two versions into rows a person can read across.
/// </summary>
/// <remarks>
/// <para>Built on the same diff the merge uses, so what the screen shows and what
/// the merge decided cannot disagree. A conflict view computed by a second,
/// independent alignment would eventually show a difference the merge did not see,
/// and the person resolving it would be looking at the wrong thing.</para>
///
/// <para>Rows are padded rather than compacted: a changed region of three lines on
/// the left and one on the right produces three rows, so the two columns stay in
/// step and the eye can follow one across.</para>
/// </remarks>
public static class SideBySide
{
    public static IReadOnlyList<SideRow> Align(
        IReadOnlyList<string> left, IReadOnlyList<string> right, int maxEdits = LineDiff.DefaultMaxEdits)
    {
        var hunks = LineDiff.Compute(left, right, maxEdits);
        var rows = new List<SideRow>(Math.Max(left.Count, right.Count));

        var l = 0;
        var r = 0;

        foreach (var h in hunks)
        {
            // Unchanged run before the hunk. Both sides advance together.
            while (l < h.BaseStart)
            {
                rows.Add(new SideRow(SideKind.Same, left[l], right[r], l + 1, r + 1));
                l++;
                r++;
            }

            var take = Math.Max(h.BaseLength, h.OtherLength);
            for (var i = 0; i < take; i++)
            {
                var hasLeft = i < h.BaseLength;
                var hasRight = i < h.OtherLength;

                var kind = hasLeft && hasRight ? SideKind.Changed
                         : hasLeft ? SideKind.OnlyLeft
                         : SideKind.OnlyRight;

                rows.Add(new SideRow(
                    kind,
                    hasLeft ? left[h.BaseStart + i] : null,
                    hasRight ? right[h.OtherStart + i] : null,
                    hasLeft ? h.BaseStart + i + 1 : 0,
                    hasRight ? h.OtherStart + i + 1 : 0));
            }

            l = h.BaseEnd;
            r = h.OtherEnd;
        }

        while (l < left.Count && r < right.Count)
        {
            rows.Add(new SideRow(SideKind.Same, left[l], right[r], l + 1, r + 1));
            l++;
            r++;
        }

        return rows;
    }

    /// <summary>
    /// Indices of rows where the two sides differ, so a reader can jump between
    /// them instead of scrolling through a long identical prefix.
    /// </summary>
    public static IReadOnlyList<int> DifferingRows(IReadOnlyList<SideRow> rows)
    {
        var result = new List<int>();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Kind != SideKind.Same) result.Add(i);
        return result;
    }
}

/// <summary>
/// Frames two versions of a line so the part that differs is on screen.
/// </summary>
/// <remarks>
/// Two long lines that differ near their end truncate to the same visible prefix,
/// and the reader is shown two identical strings and asked to choose between
/// them. Cutting from the left instead puts the difference in view, which is the
/// only reason the row is there.
/// </remarks>
public static class LineFraming
{
    /// <param name="width">Characters available for the text itself.</param>
    /// <param name="margin">How much context to keep before the first difference.</param>
    public static (string Left, string Right) Frame(string? left, string? right, int width, int margin = 8)
    {
        if (width < 8) width = 8;

        var l = Flatten(left);
        var r = Flatten(right);

        // Nothing to align on: one side is absent, or they are the same.
        if (left is null || right is null)
            return (Clip(l, width), Clip(r, width));

        var diff = FirstDifference(l, r);
        if (diff < 0) return (Clip(l, width), Clip(r, width));

        // The difference is already visible from the start of the line.
        if (diff < width - 1) return (Clip(l, width), Clip(r, width));

        var from = Math.Max(0, diff - margin);
        return (Shift(l, from, width), Shift(r, from, width));
    }

    private static int FirstDifference(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    private static string Shift(string text, int from, int width)
    {
        if (from >= text.Length) return "…";

        var rest = text[from..];
        return "…" + (rest.Length <= width - 1 ? rest : rest[..Math.Max(1, width - 2)] + "…");
    }

    private static string Clip(string text, int width)
        => text.Length <= width ? text : text[..Math.Max(1, width - 1)] + "…";

    private static string Flatten(string? text)
        => text is null ? "" : text.Replace('\t', ' ');
}
