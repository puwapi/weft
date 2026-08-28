namespace Weft.Core.Merge;

/// <summary>One row of a side-by-side view, already padded to its columns.</summary>
public readonly record struct DiffCells(
    SideKind Kind, string LeftNumber, string Left, string RightNumber, string Right);

/// <summary>
/// Lays out a side-by-side diff to exact widths.
/// </summary>
/// <remarks>
/// <para>The padding is done here rather than left to the table that draws it. A
/// general-purpose table decides column widths from its headers and its content,
/// so the two sides end up different widths and one of them wraps: the reader is
/// then shown more of one version than the other, which quietly biases a choice
/// that is supposed to be between equals.</para>
///
/// <para>Being a pure function of (rows, window, width) also means the layout can
/// be checked without a terminal, which is the part of a full-screen interface
/// that is otherwise only ever verified by looking at it.</para>
/// </remarks>
public static class DiffLayout
{
    /// <summary>Width of each line-number gutter.</summary>
    public const int GutterWidth = 4;

    /// <summary>Characters the surrounding frame and separators take.</summary>
    public const int Chrome = 13;

    /// <summary>How wide each text column can be, for a given total width.</summary>
    public static int ColumnFor(int totalWidth)
        => Math.Max(10, (totalWidth - Chrome - GutterWidth * 2) / 2);

    /// <param name="first">First row to lay out.</param>
    /// <param name="count">How many rows.</param>
    public static IReadOnlyList<DiffCells> Lay(
        IReadOnlyList<SideRow> rows, int first, int count, int column)
    {
        var result = new List<DiffCells>(count);

        for (var i = first; i < Math.Min(rows.Count, first + count); i++)
        {
            var row = rows[i];
            var (left, right) = LineFraming.Frame(row.Left, row.Right, column);

            result.Add(new DiffCells(
                row.Kind,
                Number(row.LeftNumber),
                Pad(row.Kind is SideKind.OnlyRight ? "·" : left, column),
                Number(row.RightNumber),
                Pad(row.Kind is SideKind.OnlyLeft ? "·" : right, column)));
        }

        return result;
    }

    private static string Number(int n)
        => (n == 0 ? "" : n.ToString()).PadLeft(GutterWidth)[^GutterWidth..];

    /// <summary>
    /// Pads to exactly the column, and never beyond it.
    /// </summary>
    /// <remarks>
    /// Exactly: a row one character too wide pushes the right-hand side off the
    /// screen and the view stops being side by side, which is its only reason to
    /// exist.
    ///
    /// The truncation here is defence in depth and no test reaches it, because
    /// <see cref="LineFraming.Frame"/> already bounds what it returns and this is
    /// its only caller. It stays because the two guarantees are independent: a
    /// change to the framing that widened its output would otherwise break the
    /// layout somewhere far away from the change.
    /// </remarks>
    private static string Pad(string text, int column)
        => text.Length >= column ? text[..column] : text.PadRight(column);
}
