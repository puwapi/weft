using Weft.Core.Merge;

namespace Weft.Core.Tests.Merge;

public class Diff3Tests
{
    private static string[] L(params string[] lines) => lines;

    private static Diff3Result Merge(string[] b, string[] o, string[] t) => Diff3.Merge(b, o, t);

    [Fact]
    public void Nobody_changed_anything()
    {
        var r = Merge(L("a", "b", "c"), L("a", "b", "c"), L("a", "b", "c"));
        Assert.True(r.Clean);
        Assert.Equal(L("a", "b", "c"), r.Lines);
    }

    [Fact]
    public void Only_we_changed_it()
    {
        var r = Merge(L("a", "b", "c"), L("a", "X", "c"), L("a", "b", "c"));
        Assert.True(r.Clean);
        Assert.Equal(L("a", "X", "c"), r.Lines);
    }

    [Fact]
    public void Only_they_changed_it()
    {
        var r = Merge(L("a", "b", "c"), L("a", "b", "c"), L("a", "Y", "c"));
        Assert.True(r.Clean);
        Assert.Equal(L("a", "Y", "c"), r.Lines);
    }

    [Fact]
    public void Both_made_the_same_change()
    {
        // Applying the same fix on two machines is common and is not a
        // disagreement. Reporting it as one would train people to ignore
        // conflicts.
        var r = Merge(L("a", "b", "c"), L("a", "FIX", "c"), L("a", "FIX", "c"));
        Assert.True(r.Clean);
        Assert.Equal(L("a", "FIX", "c"), r.Lines);
    }

    [Fact]
    public void Changes_far_apart_are_both_applied()
    {
        var r = Merge(
            L("1", "2", "3", "4", "5", "6", "7", "8", "9"),
            L("1", "OURS", "3", "4", "5", "6", "7", "8", "9"),
            L("1", "2", "3", "4", "5", "6", "7", "THEIRS", "9"));

        Assert.True(r.Clean);
        Assert.Equal(L("1", "OURS", "3", "4", "5", "6", "7", "THEIRS", "9"), r.Lines);
    }

    [Fact]
    public void Different_changes_to_the_same_line_conflict()
    {
        var r = Merge(L("a", "b", "c"), L("a", "OURS", "c"), L("a", "THEIRS", "c"));

        Assert.False(r.Clean);
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(L("b"), c.Base);
        Assert.Equal(L("OURS"), c.Ours);
        Assert.Equal(L("THEIRS"), c.Theirs);
    }

    [Fact]
    public void A_conflicted_region_contributes_nothing_to_the_output()
    {
        // A file on disk made of one side's text with the other side's silently
        // dropped is worse than no file: it looks finished.
        var r = Merge(L("a", "b", "c"), L("a", "OURS", "c"), L("a", "THEIRS", "c"));

        Assert.DoesNotContain("OURS", r.Lines);
        Assert.DoesNotContain("THEIRS", r.Lines);
        Assert.Contains("a", r.Lines);
        Assert.Contains("c", r.Lines);
    }

    [Fact]
    public void Adjacent_but_distinct_edits_merge_cleanly()
    {
        // Standard diff3 semantics, and the ones git implements: only OVERLAPPING
        // regions conflict. Treating adjacency as a disagreement produces
        // conflicts nobody else reports, and it breaks the far more common case of
        // an append on one side and an unrelated edit on the other.
        var r = Merge(
            L("a", "b", "c", "d"),
            L("a", "OURS", "c", "d"),
            L("a", "b", "THEIRS", "d"));

        Assert.True(r.Clean);
        Assert.Equal(L("a", "OURS", "THEIRS", "d"), r.Lines);
    }

    [Fact]
    public void Insertions_at_the_same_point_conflict()
    {
        // Two appends at the end of a file. Ordering cannot be inferred, so this
        // is a genuine conflict at the line level. The text driver handles the
        // common shape of it separately.
        var r = Merge(L("a"), L("a", "OURS"), L("a", "THEIRS"));
        Assert.False(r.Clean);
    }

    [Fact]
    public void We_deleted_a_region_they_left_alone()
    {
        var r = Merge(L("a", "b", "c"), L("a", "c"), L("a", "b", "c"));
        Assert.True(r.Clean);
        Assert.Equal(L("a", "c"), r.Lines);
    }

    [Fact]
    public void One_deleted_and_the_other_edited_the_same_region_conflicts()
    {
        var r = Merge(L("a", "b", "c"), L("a", "c"), L("a", "EDITED", "c"));
        Assert.False(r.Clean);
    }

    [Fact]
    public void An_empty_base_with_two_different_sides_conflicts()
    {
        var r = Merge([], L("ours"), L("theirs"));
        Assert.False(r.Clean);
    }

    [Fact]
    public void An_empty_base_where_both_added_the_same_thing_is_clean()
    {
        var r = Merge([], L("same"), L("same"));
        Assert.True(r.Clean);
        Assert.Equal(L("same"), r.Lines);
    }

    [Fact]
    public void Emptying_a_file_on_one_side_only_is_clean()
    {
        var r = Merge(L("a", "b"), [], L("a", "b"));
        Assert.True(r.Clean);
        Assert.Empty(r.Lines);
    }

    [Fact]
    public void A_clean_merge_keeps_every_line_each_side_added()
    {
        // The property that matters more than any single example: when weft
        // reports a clean merge, nothing either machine wrote was quietly lost.
        var rng = new Random(20260828);
        var vocabulary = new[] { "alpha", "beta", "gamma", "delta", "", "}", "if (x) {" };

        var checkedCases = 0;

        for (var trial = 0; trial < 3000; trial++)
        {
            var baseLines = Enumerable.Range(0, rng.Next(4, 30))
                .Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();

            // Each side edits a different half, so most trials merge cleanly.
            var ours = baseLines.ToList();
            var theirs = baseLines.ToList();
            var half = baseLines.Length / 2;

            for (var e = 0; e < rng.Next(1, 3); e++)
            {
                var at = rng.Next(0, Math.Max(1, half));
                ours.Insert(Math.Min(at, ours.Count), $"OURS-{trial}-{e}");
            }
            for (var e = 0; e < rng.Next(1, 3); e++)
            {
                var at = rng.Next(half + 1, Math.Max(half + 2, theirs.Count + 1));
                theirs.Insert(Math.Min(at, theirs.Count), $"THEIRS-{trial}-{e}");
            }

            var r = Diff3.Merge(baseLines, ours.ToArray(), theirs.ToArray());
            if (!r.Clean) continue;

            checkedCases++;

            foreach (var line in ours.Where(l => l.StartsWith("OURS-", StringComparison.Ordinal)))
                Assert.Contains(line, r.Lines);
            foreach (var line in theirs.Where(l => l.StartsWith("THEIRS-", StringComparison.Ordinal)))
                Assert.Contains(line, r.Lines);
        }

        Assert.True(checkedCases > 1000, $"only {checkedCases} trials merged cleanly; the property was barely exercised");
    }

    [Fact]
    public void A_clean_merge_never_invents_a_line_nobody_wrote()
    {
        var rng = new Random(99);

        for (var trial = 0; trial < 2000; trial++)
        {
            var baseLines = Enumerable.Range(0, rng.Next(1, 20)).Select(i => $"b{rng.Next(8)}").ToArray();
            var ours = baseLines.ToList();
            var theirs = baseLines.ToList();

            if (ours.Count > 0 && rng.Next(2) == 0) ours[rng.Next(ours.Count)] = $"o{rng.Next(8)}";
            else ours.Insert(rng.Next(ours.Count + 1), $"o{rng.Next(8)}");

            if (theirs.Count > 0 && rng.Next(2) == 0) theirs[rng.Next(theirs.Count)] = $"t{rng.Next(8)}";
            else theirs.Insert(rng.Next(theirs.Count + 1), $"t{rng.Next(8)}");

            var r = Diff3.Merge(baseLines, ours.ToArray(), theirs.ToArray());
            if (!r.Clean) continue;

            var known = baseLines.Concat(ours).Concat(theirs).ToHashSet(StringComparer.Ordinal);
            foreach (var line in r.Lines)
                Assert.Contains(line, known);
        }
    }
}
