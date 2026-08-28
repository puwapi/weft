using Weft.Core.Merge;

namespace Weft.Core.Tests.Merge;

/// <summary>
/// The layout of a full-screen view, checked without a screen.
/// </summary>
/// <remarks>
/// Column widths are the part of a terminal interface that is normally only ever
/// verified by looking at it, and looking once does not catch the line that is
/// three characters too long. Laying out here rather than in the table that draws
/// it makes every rule an assertion.
/// </remarks>
public class DiffLayoutTests
{
    private static IReadOnlyList<SideRow> Rows(params (string? L, string? R)[] pairs)
        => pairs.Select((p, i) => new SideRow(
            p.L is null ? SideKind.OnlyRight : p.R is null ? SideKind.OnlyLeft
                : p.L == p.R ? SideKind.Same : SideKind.Changed,
            p.L, p.R, p.L is null ? 0 : i + 1, p.R is null ? 0 : i + 1)).ToList();

    [Fact]
    public void Every_cell_is_exactly_the_column_width()
    {
        // A row one character too wide pushes the right-hand side off the screen
        // and the view stops being side by side, which is its only reason to exist.
        var rows = Rows(("short", "short"), (new string('x', 300), new string('y', 300)), ("", ""));
        var cells = DiffLayout.Lay(rows, 0, rows.Count, column: 20);

        Assert.All(cells, c =>
        {
            Assert.Equal(20, c.Left.Length);
            Assert.Equal(20, c.Right.Length);
            Assert.Equal(DiffLayout.GutterWidth, c.LeftNumber.Length);
            Assert.Equal(DiffLayout.GutterWidth, c.RightNumber.Length);
        });
    }

    [Fact]
    public void A_line_only_one_side_has_shows_a_marker_opposite_it()
    {
        var cells = DiffLayout.Lay(Rows((null, "theirs only")), 0, 1, column: 20);
        var c = Assert.Single(cells);

        Assert.Equal("·", c.Left.TrimEnd());
        Assert.Equal("theirs only", c.Right.TrimEnd());
    }

    [Fact]
    public void An_absent_line_number_is_blank_and_still_takes_its_column()
    {
        var c = Assert.Single(DiffLayout.Lay(Rows((null, "x")), 0, 1, column: 20));

        Assert.Equal(DiffLayout.GutterWidth, c.LeftNumber.Length);
        Assert.Equal("", c.LeftNumber.Trim());
        Assert.Equal("1", c.RightNumber.Trim());
    }

    [Fact]
    public void The_window_takes_only_the_rows_asked_for()
    {
        var rows = Rows(("a", "a"), ("b", "b"), ("c", "c"), ("d", "d"), ("e", "e"));
        var cells = DiffLayout.Lay(rows, first: 1, count: 2, column: 10);

        Assert.Equal(2, cells.Count);
        Assert.Equal("b", cells[0].Left.TrimEnd());
        Assert.Equal("c", cells[1].Left.TrimEnd());
    }

    [Fact]
    public void Asking_past_the_end_stops_rather_than_throwing()
    {
        var rows = Rows(("a", "a"), ("b", "b"));
        Assert.Equal(2, DiffLayout.Lay(rows, first: 0, count: 50, column: 10).Count);
        Assert.Empty(DiffLayout.Lay(rows, first: 10, count: 5, column: 10));
    }

    [Fact]
    public void A_long_line_that_differs_at_its_END_still_shows_the_difference()
    {
        // The layout has to preserve what the framing achieved. Padding a framed
        // line from the left, or clipping it again from the right, would undo it.
        var prefix = new string('=', 80);
        var cells = DiffLayout.Lay(Rows((prefix + "OURS", prefix + "THEIRS")), 0, 1, column: 24);
        var c = Assert.Single(cells);

        Assert.Contains("OURS", c.Left, StringComparison.Ordinal);
        Assert.Contains("THEIRS", c.Right, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void The_whole_row_fits_the_terminal_it_was_sized_for(int width)
    {
        // Two gutters, two text columns and the chrome between them. If this sum
        // exceeds the terminal, every long line wraps and the alignment is gone.
        var column = DiffLayout.ColumnFor(width);
        var total = DiffLayout.GutterWidth * 2 + column * 2 + DiffLayout.Chrome;

        Assert.True(total <= width, $"a row of {total} does not fit {width} columns");
    }

    [Fact]
    public void A_terminal_too_narrow_to_be_useful_still_produces_a_usable_column()
    {
        // Refusing to draw is worse than drawing something cramped: the user is on
        // that terminal for a reason.
        Assert.True(DiffLayout.ColumnFor(20) >= 10);
        Assert.True(DiffLayout.ColumnFor(1) >= 10);
    }

    [Fact]
    public void Nothing_ever_exceeds_its_column_whatever_the_input()
    {
        var rng = new Random(20260828);

        for (var i = 0; i < 3000; i++)
        {
            var column = rng.Next(10, 60);
            var rows = Rows(
                (rng.Next(4) == 0 ? null : new string('a', rng.Next(0, 300)) + rng.Next(),
                 rng.Next(4) == 0 ? null : new string('a', rng.Next(0, 300)) + rng.Next()));

            foreach (var c in DiffLayout.Lay(rows, 0, rows.Count, column))
            {
                Assert.Equal(column, c.Left.Length);
                Assert.Equal(column, c.Right.Length);
            }
        }
    }
}
