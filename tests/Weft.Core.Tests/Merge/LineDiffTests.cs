using Weft.Core.Merge;

namespace Weft.Core.Tests.Merge;

public class LineDiffTests
{
    private static string[] L(params string[] lines) => lines;

    /// <summary>Applies a diff to the base. If this does not reproduce the target, the diff is wrong.</summary>
    private static string[] Apply(IReadOnlyList<string> baseLines, IReadOnlyList<string> other,
                                  IReadOnlyList<Hunk> hunks)
    {
        var result = new List<string>();
        var at = 0;

        foreach (var h in hunks)
        {
            for (var i = at; i < h.BaseStart; i++) result.Add(baseLines[i]);
            for (var i = h.OtherStart; i < h.OtherEnd; i++) result.Add(other[i]);
            at = h.BaseEnd;
        }

        for (var i = at; i < baseLines.Count; i++) result.Add(baseLines[i]);
        return result.ToArray();
    }

    private static void RoundTrips(string[] a, string[] b)
    {
        var hunks = LineDiff.Compute(a, b);
        Assert.Equal(b, Apply(a, b, hunks));
    }

    [Fact]
    public void Identical_input_produces_no_hunks()
        => Assert.Empty(LineDiff.Compute(L("a", "b", "c"), L("a", "b", "c")));

    [Fact]
    public void An_insertion_in_the_middle_is_one_hunk_of_zero_base_length()
    {
        var h = Assert.Single(LineDiff.Compute(L("a", "b", "c"), L("a", "x", "b", "c")));
        Assert.Equal(1, h.BaseStart);
        Assert.Equal(0, h.BaseLength);
        Assert.Equal(1, h.OtherLength);
    }

    [Fact]
    public void A_deletion_is_one_hunk_of_zero_other_length()
    {
        var h = Assert.Single(LineDiff.Compute(L("a", "b", "c"), L("a", "c")));
        Assert.Equal(1, h.BaseStart);
        Assert.Equal(1, h.BaseLength);
        Assert.Equal(0, h.OtherLength);
    }

    [Fact]
    public void A_deletion_next_to_an_insertion_is_reported_as_ONE_replacement()
    {
        // Reported apart, these would make two sides look like they touched
        // different regions when they touched the same one, and the merge would
        // apply both edits to overlapping text.
        var h = Assert.Single(LineDiff.Compute(L("a", "b", "c"), L("a", "x", "c")));
        Assert.Equal(1, h.BaseStart);
        Assert.Equal(1, h.BaseLength);
        Assert.Equal(1, h.OtherLength);
    }

    [Fact]
    public void Two_distant_edits_stay_two_hunks()
    {
        var hunks = LineDiff.Compute(
            L("a", "b", "c", "d", "e", "f", "g"),
            L("a", "X", "c", "d", "e", "Y", "g"));

        Assert.Equal(2, hunks.Count);
    }

    [Theory]
    [InlineData(new string[0], new[] { "a", "b" })]
    [InlineData(new[] { "a", "b" }, new string[0])]
    [InlineData(new string[0], new string[0])]
    public void Empty_sides_are_handled(string[] a, string[] b) => RoundTrips(a, b);

    [Fact]
    public void Wholly_different_content_round_trips()
        => RoundTrips(L("a", "b", "c"), L("x", "y", "z"));

    [Fact]
    public void Appending_at_the_end_is_a_hunk_past_the_last_base_line()
    {
        var h = Assert.Single(LineDiff.Compute(L("a", "b"), L("a", "b", "c", "d")));
        Assert.Equal(2, h.BaseStart);
        Assert.Equal(0, h.BaseLength);
        Assert.Equal(2, h.OtherLength);
    }

    [Fact]
    public void Repeated_lines_do_not_confuse_the_alignment()
    {
        // Duplicate lines are where a naive diff picks the wrong match and
        // reports a much larger region than changed.
        RoundTrips(
            L("x", "x", "x", "a", "x", "x", "x"),
            L("x", "x", "x", "b", "x", "x", "x"));

        var hunks = LineDiff.Compute(
            L("x", "x", "x", "a", "x", "x", "x"),
            L("x", "x", "x", "b", "x", "x", "x"));

        Assert.Single(hunks);
        Assert.Equal(1, hunks[0].BaseLength);
    }

    [Fact]
    public void Any_pair_of_inputs_round_trips()
    {
        // The property the whole merge rests on. A diff that does not reproduce
        // its target silently corrupts every merge built on it, and no example
        // test covers the shape that breaks it.
        var rng = new Random(20260828);
        var vocabulary = new[] { "alpha", "beta", "gamma", "delta", "", "  indented", "}", "if (x) {" };

        for (var trial = 0; trial < 4000; trial++)
        {
            var a = Enumerable.Range(0, rng.Next(0, 40)).Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();

            // The target is derived from the source by real edits, so the pairs
            // look like edits rather than like two unrelated files.
            var b = a.ToList();
            for (var e = 0; e < rng.Next(0, 8); e++)
            {
                if (b.Count > 0 && rng.Next(3) == 0) b.RemoveAt(rng.Next(b.Count));
                else if (b.Count > 0 && rng.Next(2) == 0) b[rng.Next(b.Count)] = vocabulary[rng.Next(vocabulary.Length)];
                else b.Insert(rng.Next(b.Count + 1), vocabulary[rng.Next(vocabulary.Length)]);
            }

            var hunks = LineDiff.Compute(a, b.ToArray());
            Assert.Equal(b.ToArray(), Apply(a, b.ToArray(), hunks));
        }
    }

    [Fact]
    public void Unrelated_files_beyond_the_cap_become_one_wholesale_replacement()
    {
        // The bail-out. Without it, two unrelated large files take O(D squared)
        // memory and the process looks hung on a case whose answer was obvious.
        var a = Enumerable.Range(0, 400).Select(i => $"left {i}").ToArray();
        var b = Enumerable.Range(0, 400).Select(i => $"right {i}").ToArray();

        var hunks = LineDiff.Compute(a, b, maxEdits: 8);

        var h = Assert.Single(hunks);
        Assert.Equal(0, h.BaseStart);
        Assert.Equal(400, h.BaseLength);
        Assert.Equal(400, h.OtherLength);
        Assert.Equal(b, Apply(a, b, hunks));
    }

    [Fact]
    public void Hunks_come_back_in_order_and_never_overlap()
    {
        var rng = new Random(7);
        for (var trial = 0; trial < 500; trial++)
        {
            var a = Enumerable.Range(0, rng.Next(1, 30)).Select(i => $"line {rng.Next(6)}").ToArray();
            var b = Enumerable.Range(0, rng.Next(1, 30)).Select(i => $"line {rng.Next(6)}").ToArray();

            var hunks = LineDiff.Compute(a, b);
            for (var i = 1; i < hunks.Count; i++)
                Assert.True(hunks[i - 1].BaseEnd <= hunks[i].BaseStart,
                    "hunks overlap, so applying them would duplicate or drop base lines");
        }
    }
}
