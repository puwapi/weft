namespace Weft.Core.Merge;

/// <summary>
/// One region of the base replaced by one region of the other side.
/// </summary>
/// <param name="BaseStart">First base line the change covers.</param>
/// <param name="BaseLength">How many base lines it replaces. Zero for a pure insertion.</param>
/// <param name="OtherStart">First replacement line.</param>
/// <param name="OtherLength">How many replacement lines. Zero for a pure deletion.</param>
public readonly record struct Hunk(int BaseStart, int BaseLength, int OtherStart, int OtherLength)
{
    public int BaseEnd => BaseStart + BaseLength;
    public int OtherEnd => OtherStart + OtherLength;
}

/// <summary>
/// Line diff, by Myers' algorithm.
/// </summary>
/// <remarks>
/// <para>The same algorithm git uses. It finds a shortest edit script, which
/// matters here for a reason beyond elegance: a diff that reports a larger
/// changed region than necessary makes two edits look like they overlap when they
/// do not, and the merge then asks a person to resolve a conflict that was never
/// there.</para>
/// </remarks>
public static class LineDiff
{
    /// <summary>
    /// Above this many edits the diff stops looking and reports one wholesale
    /// replacement.
    /// </summary>
    /// <remarks>
    /// Myers costs O(ND) in time and, keeping a trace, O(D²) in memory. Two
    /// unrelated files of ten thousand lines have D near twenty thousand, which is
    /// four hundred million trace entries: the process would appear to hang on a
    /// case where the honest answer, "these two files share nothing", was already
    /// obvious. Real diff tools all bail the same way.
    /// </remarks>
    public const int DefaultMaxEdits = 5000;

    public static IReadOnlyList<Hunk> Compute(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> other, int maxEdits = DefaultMaxEdits)
    {
        // Identical prefixes and suffixes are the bulk of any real edit, and
        // trimming them shrinks D far more than it shrinks N.
        var start = 0;
        var maxStart = Math.Min(baseLines.Count, other.Count);
        while (start < maxStart && baseLines[start] == other[start]) start++;

        var endA = baseLines.Count;
        var endB = other.Count;
        while (endA > start && endB > start && baseLines[endA - 1] == other[endB - 1]) { endA--; endB--; }

        if (start == endA && start == endB) return [];

        var a = Slice(baseLines, start, endA);
        var b = Slice(other, start, endB);

        var script = Myers(a, b, maxEdits);
        if (script is null)
            return [new Hunk(start, endA - start, start, endB - start)];

        return script.Select(h => h with { BaseStart = h.BaseStart + start, OtherStart = h.OtherStart + start })
                     .ToList();
    }

    private static string[] Slice(IReadOnlyList<string> src, int from, int to)
    {
        var r = new string[to - from];
        for (var i = 0; i < r.Length; i++) r[i] = src[from + i];
        return r;
    }

    /// <summary>Returns null when the edit distance exceeds the cap.</summary>
    private static List<Hunk>? Myers(string[] a, string[] b, int maxEdits)
    {
        var n = a.Length;
        var m = b.Length;

        if (n == 0 && m == 0) return [];
        if (n == 0) return [new Hunk(0, 0, 0, m)];
        if (m == 0) return [new Hunk(0, n, 0, 0)];

        var max = Math.Min(n + m, maxEdits);
        var offset = max;
        var v = new int[2 * max + 2];
        var trace = new List<int[]>(Math.Min(max, 64));

        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());

            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
                    x = v[k + 1 + offset];
                else
                    x = v[k - 1 + offset] + 1;

                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }

                v[k + offset] = x;

                if (x >= n && y >= m) return Backtrack(trace, a, b, d, offset);
            }
        }

        return null;   // beyond the cap
    }

    private static List<Hunk> Backtrack(List<int[]> trace, string[] a, string[] b, int d, int offset)
    {
        var edits = new List<(int Ax, int Ay, int Bx, int By)>();
        var x = a.Length;
        var y = b.Length;

        for (var step = d; step > 0; step--)
        {
            var v = trace[step];
            var k = x - y;

            int prevK;
            if (k == -step || (k != step && v[k - 1 + offset] < v[k + 1 + offset]))
                prevK = k + 1;
            else
                prevK = k - 1;

            var prevX = v[prevK + offset];
            var prevY = prevX - prevK;

            // Diagonal moves are matches and carry no edit.
            while (x > prevX && y > prevY) { x--; y--; }

            edits.Add((prevX, prevY, x, y));
            x = prevX;
            y = prevY;
        }

        edits.Reverse();

        // Adjacent edits are coalesced: a deletion immediately followed by an
        // insertion is one replacement, and reporting them apart would make two
        // sides look like they touched different regions when they touched one.
        var hunks = new List<Hunk>();
        foreach (var (ax, ay, bx, by) in edits)
        {
            var h = new Hunk(ax, bx - ax, ay, by - ay);
            if (h.BaseLength == 0 && h.OtherLength == 0) continue;

            if (hunks.Count > 0)
            {
                var last = hunks[^1];
                if (last.BaseEnd == h.BaseStart && last.OtherEnd == h.OtherStart)
                {
                    hunks[^1] = new Hunk(last.BaseStart, last.BaseLength + h.BaseLength,
                                         last.OtherStart, last.OtherLength + h.OtherLength);
                    continue;
                }
            }

            hunks.Add(h);
        }

        return hunks;
    }
}
