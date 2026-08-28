namespace Weft.Core.Merge;

/// <summary>A region both sides changed differently. Nobody but a person can settle it.</summary>
/// <param name="BaseStart">Where in the base the disagreement begins.</param>
/// <param name="Base">What the base said.</param>
/// <param name="Ours">What this machine says.</param>
/// <param name="Theirs">What the other machine says.</param>
public sealed record Diff3Conflict(
    int BaseStart,
    IReadOnlyList<string> Base,
    IReadOnlyList<string> Ours,
    IReadOnlyList<string> Theirs);

/// <summary>Outcome of a three-way merge.</summary>
public sealed record Diff3Result(
    IReadOnlyList<string> Lines,
    IReadOnlyList<Diff3Conflict> Conflicts)
{
    public bool Clean => Conflicts.Count == 0;
}

/// <summary>
/// Three-way line merge.
/// </summary>
/// <remarks>
/// <para>The common ancestor is what makes this possible at all. With only two
/// versions there is no way to tell "they added a line" from "I deleted one", so
/// a two-way synchroniser can do nothing but pick a winner or drop a copy beside
/// the original. With the base in hand, most differences resolve without asking
/// anyone.</para>
///
/// <para>Regions each side changed independently are both applied. Regions both
/// sides changed are a conflict, unless they changed them to the same thing, in
/// which case there was never a disagreement to report.</para>
/// </remarks>
public static class Diff3
{
    public static Diff3Result Merge(
        IReadOnlyList<string> baseLines,
        IReadOnlyList<string> ours,
        IReadOnlyList<string> theirs,
        int maxEdits = LineDiff.DefaultMaxEdits)
    {
        var ourHunks = LineDiff.Compute(baseLines, ours, maxEdits);
        var theirHunks = LineDiff.Compute(baseLines, theirs, maxEdits);

        if (ourHunks.Count == 0) return new Diff3Result(theirs, []);
        if (theirHunks.Count == 0) return new Diff3Result(ours, []);

        var output = new List<string>(Math.Max(ours.Count, theirs.Count));
        var conflicts = new List<Diff3Conflict>();

        var basePos = 0;
        var oi = 0;
        var ti = 0;

        while (oi < ourHunks.Count || ti < theirHunks.Count)
        {
            var nextOur = oi < ourHunks.Count ? ourHunks[oi].BaseStart : int.MaxValue;
            var nextTheir = ti < theirHunks.Count ? theirHunks[ti].BaseStart : int.MaxValue;
            var nextChange = Math.Min(nextOur, nextTheir);

            // Base lines neither side touched are copied through untouched.
            for (; basePos < nextChange; basePos++) output.Add(baseLines[basePos]);

            // Grow a window until it holds whole hunks from both sides. A window
            // that cut a hunk in half would attribute part of one side's edit to
            // the untouched region and silently drop the rest.
            var start = nextChange;
            var oStart = oi;
            var tStart = ti;

            // Seed with everything that begins exactly here. Two insertions at
            // one point are caught by this, and they are a real conflict: the
            // order of two insertions at the same place cannot be inferred.
            var end = start;
            while (oi < ourHunks.Count && ourHunks[oi].BaseStart == start)
            { end = Math.Max(end, ourHunks[oi].BaseEnd); oi++; }
            while (ti < theirHunks.Count && theirHunks[ti].BaseStart == start)
            { end = Math.Max(end, theirHunks[ti].BaseEnd); ti++; }

            // Then pull in whatever begins STRICTLY INSIDE the window.
            //
            // Strictly: a hunk starting exactly where this one ends did not touch
            // any line this one touched, so it belongs to the next window. An
            // earlier version used '<=' here, which made merely ADJACENT edits
            // collide and reported conflicts that standard diff3 resolves. It
            // also broke the far more common case of an append on one side and an
            // unrelated edit on the other.
            bool grew;
            do
            {
                grew = false;

                while (oi < ourHunks.Count && ourHunks[oi].BaseStart < end)
                { end = Math.Max(end, ourHunks[oi].BaseEnd); oi++; grew = true; }

                while (ti < theirHunks.Count && theirHunks[ti].BaseStart < end)
                { end = Math.Max(end, theirHunks[ti].BaseEnd); ti++; grew = true; }
            }
            while (grew);

            end = Math.Min(end, baseLines.Count);

            var ourText = Project(baseLines, ours, ourHunks, oStart, oi, start, end);
            var theirText = Project(baseLines, theirs, theirHunks, tStart, ti, start, end);

            var touchedByUs = oi > oStart;
            var touchedByThem = ti > tStart;

            if (touchedByUs && touchedByThem)
            {
                if (Same(ourText, theirText))
                {
                    // Both machines made the same edit. Common when the same fix
                    // is applied twice, and it is not a disagreement.
                    output.AddRange(ourText);
                }
                else
                {
                    conflicts.Add(new Diff3Conflict(
                        start, Sub(baseLines, start, end), ourText, theirText));

                    // Nothing is written for a conflicted region. The caller
                    // decides what reaches disk, and a half-merged file on disk is
                    // worse than none: it looks finished.
                }
            }
            else
            {
                output.AddRange(touchedByUs ? ourText : theirText);
            }

            basePos = end;
        }

        for (; basePos < baseLines.Count; basePos++) output.Add(baseLines[basePos]);

        return new Diff3Result(output, conflicts);
    }

    /// <summary>What one side says in place of base lines [start, end).</summary>
    private static List<string> Project(
        IReadOnlyList<string> baseLines, IReadOnlyList<string> side,
        IReadOnlyList<Hunk> hunks, int from, int to, int start, int end)
    {
        var result = new List<string>();
        var at = start;

        for (var i = from; i < to; i++)
        {
            var h = hunks[i];

            // Base lines before this hunk are unchanged on this side, so they
            // appear as they are.
            for (var b = at; b < Math.Min(h.BaseStart, end); b++) result.Add(baseLines[b]);

            for (var o = h.OtherStart; o < h.OtherEnd; o++) result.Add(side[o]);
            at = Math.Max(at, h.BaseEnd);
        }

        for (var b = at; b < end; b++) result.Add(baseLines[b]);
        return result;
    }

    private static List<string> Sub(IReadOnlyList<string> src, int start, int end)
    {
        var r = new List<string>(Math.Max(0, end - start));
        for (var i = start; i < end && i < src.Count; i++) r.Add(src[i]);
        return r;
    }

    private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
