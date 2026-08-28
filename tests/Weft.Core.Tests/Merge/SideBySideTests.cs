using Weft.Core.Merge;

namespace Weft.Core.Tests.Merge;

public class SideBySideTests
{
    private static string[] L(params string[] lines) => lines;

    [Fact]
    public void Identical_input_is_all_same_rows()
    {
        var rows = SideBySide.Align(L("a", "b", "c"), L("a", "b", "c"));

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(SideKind.Same, r.Kind));
        Assert.Equal([1, 2, 3], rows.Select(r => r.LeftNumber));
    }

    [Fact]
    public void A_changed_line_shows_both_versions_on_one_row()
    {
        var rows = SideBySide.Align(L("a", "OURS", "c"), L("a", "THEIRS", "c"));

        var changed = Assert.Single(rows, r => r.Kind == SideKind.Changed);
        Assert.Equal("OURS", changed.Left);
        Assert.Equal("THEIRS", changed.Right);
        Assert.Equal(2, changed.LeftNumber);
        Assert.Equal(2, changed.RightNumber);
    }

    [Fact]
    public void A_line_only_one_side_has_leaves_the_other_column_empty()
    {
        var rows = SideBySide.Align(L("a", "c"), L("a", "b", "c"));

        var only = Assert.Single(rows, r => r.Kind == SideKind.OnlyRight);
        Assert.Null(only.Left);
        Assert.Equal("b", only.Right);
        Assert.Equal(0, only.LeftNumber);
    }

    [Fact]
    public void An_uneven_change_is_padded_so_the_columns_stay_in_step()
    {
        // Three lines replaced by one. Compacting would slide everything after it
        // out of alignment and the eye could no longer follow a line across.
        var rows = SideBySide.Align(L("a", "1", "2", "3", "z"), L("a", "X", "z"));

        Assert.Equal(SideKind.Same, rows[0].Kind);
        Assert.Equal(SideKind.Changed, rows[1].Kind);
        Assert.Equal(SideKind.OnlyLeft, rows[2].Kind);
        Assert.Equal(SideKind.OnlyLeft, rows[3].Kind);
        Assert.Equal(SideKind.Same, rows[4].Kind);
        Assert.Equal("z", rows[4].Left);
        Assert.Equal("z", rows[4].Right);
    }

    [Fact]
    public void Every_line_of_both_sides_appears_exactly_once()
    {
        // The property a conflict view rests on: a reader has to be able to trust
        // that nothing was dropped from what they are choosing between.
        var rng = new Random(20260828);
        var vocabulary = new[] { "alpha", "beta", "gamma", "", "}", "if (x) {" };

        for (var trial = 0; trial < 2000; trial++)
        {
            var left = Enumerable.Range(0, rng.Next(0, 25))
                .Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();
            var right = Enumerable.Range(0, rng.Next(0, 25))
                .Select(_ => vocabulary[rng.Next(vocabulary.Length)]).ToArray();

            var rows = SideBySide.Align(left, right);

            Assert.Equal(left, rows.Where(r => r.Left is not null).Select(r => r.Left!).ToArray());
            Assert.Equal(right, rows.Where(r => r.Right is not null).Select(r => r.Right!).ToArray());
        }
    }

    [Fact]
    public void Line_numbers_run_in_order_and_skip_nothing()
    {
        var rows = SideBySide.Align(L("a", "b", "c", "d"), L("a", "X", "d"));

        Assert.Equal([1, 2, 3, 4], rows.Where(r => r.LeftNumber > 0).Select(r => r.LeftNumber));
        Assert.Equal([1, 2, 3], rows.Where(r => r.RightNumber > 0).Select(r => r.RightNumber));
    }

    [Fact]
    public void An_empty_side_is_handled()
    {
        var rows = SideBySide.Align([], L("a", "b"));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(SideKind.OnlyRight, r.Kind));
    }

    [Fact]
    public void Differing_rows_are_the_ones_worth_jumping_to()
    {
        var rows = SideBySide.Align(L("a", "b", "c", "d", "e"), L("a", "X", "c", "d", "Y"));
        Assert.Equal([1, 4], SideBySide.DifferingRows(rows));
    }
}

public class LineFramingTests
{
    [Fact]
    public void Short_lines_are_shown_from_the_start()
    {
        var (l, r) = LineFraming.Frame("hello", "world", width: 40);
        Assert.Equal("hello", l);
        Assert.Equal("world", r);
    }

    [Fact]
    public void A_difference_near_the_end_is_brought_into_view()
    {
        // The case the whole helper exists for. Truncating from the start shows
        // the reader two identical strings and asks them to choose between them.
        var prefix = new string('x', 60);
        var (l, r) = LineFraming.Frame(prefix + "OURS", prefix + "THEIRS", width: 30);

        Assert.Contains("OURS", l, StringComparison.Ordinal);
        Assert.Contains("THEIRS", r, StringComparison.Ordinal);
        Assert.NotEqual(l, r);
    }

    [Fact]
    public void Framing_shows_that_the_start_was_cut()
    {
        var prefix = new string('x', 60);
        var (l, _) = LineFraming.Frame(prefix + "A", prefix + "B", width: 20);
        Assert.StartsWith("…", l, StringComparison.Ordinal);
    }

    [Fact]
    public void Some_context_before_the_difference_is_kept()
    {
        // Landing exactly on the differing character gives no clue where you are.
        var (l, r) = LineFraming.Frame(
            "const timeout = 30; // aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa OURS",
            "const timeout = 30; // aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa THEIRS",
            width: 24);

        Assert.Contains("a", l, StringComparison.Ordinal);
        Assert.Contains("OURS", l, StringComparison.Ordinal);
        Assert.Contains("THEIRS", r, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_sides_are_shifted_by_the_SAME_amount()
    {
        // Shifting them independently would put unrelated text opposite each
        // other, which is worse than truncating: it reads as a difference that is
        // not there.
        var (l, r) = LineFraming.Frame(
            new string('=', 50) + "left-tail",
            new string('=', 50) + "right-tail",
            width: 20);

        Assert.True(l.StartsWith('…') && r.StartsWith('…'));
        Assert.Equal(l.TakeWhile(c => c == '=').Count(), r.TakeWhile(c => c == '=').Count());
    }

    [Fact]
    public void An_absent_side_is_handled()
    {
        var (l, r) = LineFraming.Frame(null, "only theirs", width: 30);
        Assert.Equal("", l);
        Assert.Equal("only theirs", r);
    }

    [Fact]
    public void Identical_lines_are_not_shifted()
    {
        var text = new string('y', 80);
        var (l, r) = LineFraming.Frame(text, text, width: 20);
        Assert.False(l.StartsWith('…'));
        Assert.Equal(l, r);
    }

    [Fact]
    public void Tabs_become_spaces_so_the_columns_stay_in_step()
    {
        var (l, _) = LineFraming.Frame("a\tb", "a b", width: 30);
        Assert.DoesNotContain('\t', l);
    }

    [Fact]
    public void Nothing_ever_comes_back_wider_than_asked_for()
    {
        // A row that overflows its column pushes the other side off screen, and
        // the view stops being side by side.
        var rng = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var width = rng.Next(8, 60);
            var a = new string('a', rng.Next(0, 200)) + rng.Next();
            var b = new string('a', rng.Next(0, 200)) + rng.Next();

            var (l, r) = LineFraming.Frame(a, b, width);
            Assert.True(l.Length <= width, $"left was {l.Length} for a width of {width}");
            Assert.True(r.Length <= width, $"right was {r.Length} for a width of {width}");
        }
    }
}
