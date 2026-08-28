using Spectre.Console;
using Spectre.Console.Rendering;
using Weft.Core.Merge;
using Weft.Core.Ui;

namespace Weft.Cli.Tui;

/// <summary>Turns the state into something to draw. Reads nothing, changes nothing.</summary>
internal static class TuiView
{
    public static IRenderable Render(UiState s, int width, int height)
    {
        var bodyRows = Math.Max(3, height - 6);
        _ = width;

        var body = s.ShowHelp
            ? Help()
            : s.Screen switch
            {
                UiScreen.Overview => Overview(s),
                UiScreen.Conflicts => Conflicts(s, bodyRows),
                UiScreen.Diff => Diff(s, width, bodyRows),
                _ => new Markup(""),
            };

        return new Rows(Header(s, width), body, Footer(s));
    }

    private static IRenderable Header(UiState s, int width)
    {
        var d = s.Data;

        var where = d.Head is null
            ? "[dim]no snapshot yet[/]"
            : d.Pushed
                ? $"[blue]{d.Head[..12]}[/] [green]on the server[/]"
                : $"[blue]{d.Head[..12]}[/] [yellow]not pushed[/]";

        // The path is trimmed from the LEFT. Its tail is what identifies a
        // workspace; cutting the end would leave several of them looking alike,
        // and letting it wrap pushes the rest of the line out of view.
        var room = Math.Max(20, width - d.MachineName.Length - 34);
        var path = Shorten(d.WorkspacePath);
        if (path.Length > room) path = "…" + path[^(room - 1)..];

        return new Panel(new Markup($"[bold]{Markup.Escape(path)}[/]   "
                + $"[dim]as[/] {Markup.Escape(d.MachineName)}   {where}"))
            .Expand()
            .Border(BoxBorder.None);
    }

    private static IRenderable Footer(UiState s)
    {
        var keys = s.ShowHelp
            ? "[dim]any key to close[/]"
            : s.Screen switch
            {
                UiScreen.Conflicts => "[bold]enter[/] [dim]diff[/]   [bold]o[/] [dim]keep ours[/]   "
                    + "[bold]t[/] [dim]take theirs[/]   [bold]esc[/] [dim]back[/]   [bold]?[/] [dim]keys[/]   [bold]q[/] [dim]quit[/]",
                UiScreen.Diff => "[dim]↑↓ scroll[/]   [bold]o[/] [dim]keep ours[/]   [bold]t[/] [dim]take theirs[/]   "
                    + "[bold]esc[/] [dim]back[/]   [bold]q[/] [dim]quit[/]",
                _ => "[bold]r[/] [dim]reload[/]   [bold]?[/] [dim]keys[/]   [bold]q[/] [dim]quit[/]",
            };

        var status = s.Status is null ? "" : $"\n[green]{Markup.Escape(s.Status)}[/]";
        return new Panel(new Markup(keys + status)).Expand().Border(BoxBorder.None);
    }

    // ---------- overview ----------

    private static IRenderable Overview(UiState s)
    {
        var d = s.Data;
        var parts = new List<IRenderable>();

        // Attention comes FIRST and unprompted. This is the screen someone opens
        // to find out whether anything is wrong, and burying it under a status
        // table would answer a question nobody asked.
        if (d.Attention.Count > 0)
        {
            var t = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow).Expand();
            t.AddColumn("[yellow]Wants attention[/]");
            t.AddColumn("What");
            t.AddColumn("Why");

            foreach (var a in d.Attention.OrderBy(a => a.Kind))
                t.AddRow(Label(a.Kind), Markup.Escape(a.Subject), $"[dim]{Markup.Escape(a.Detail)}[/]");

            parts.Add(t);
        }
        else
        {
            parts.Add(new Panel(new Markup("[green]Nothing wants attention.[/]"))
                .Expand().BorderColor(Color.Green));
        }

        var m = new Table().Border(TableBorder.Rounded).Expand();
        m.AddColumn("Machine");
        m.AddColumn("Snapshot");
        m.AddColumn("Last seen");

        foreach (var row in d.Machines)
            m.AddRow(
                row.IsThisOne ? $"[bold]{Markup.Escape(row.Name)}[/] [dim](this one)[/]" : Markup.Escape(row.Name),
                row.Snapshot is null ? "[dim]none yet[/]" : $"[blue]{row.Snapshot[..12]}[/]",
                row.SeenUtc is null ? "[dim]-[/]" : Ago(row.SeenUtc.Value));

        parts.Add(m);

        if (d.Conflicts.Count > 0)
            parts.Add(new Markup($"[yellow]{d.Conflicts.Count} conflict(s) waiting.[/] "
                + "[dim]press [bold]enter[/] to settle them[/]"));

        return new Rows(parts);
    }

    private static string Label(AttentionKind kind) => kind switch
    {
        AttentionKind.WorkOnlyHere => "[red]only here[/]",
        AttentionKind.Conflict => "[yellow]conflict[/]",
        AttentionKind.VolatileWorktree => "[red]temp dir[/]",
        AttentionKind.NotPushed => "[yellow]not pushed[/]",
        AttentionKind.ExternalWorktree => "[dim]outside[/]",
        _ => "[dim]?[/]",
    };

    // ---------- conflicts ----------

    private static IRenderable Conflicts(UiState s, int rows)
    {
        var t = new Table().Border(TableBorder.Rounded).Expand();
        t.AddColumn(" ");
        t.AddColumn("File");
        t.AddColumn("Why");

        var window = Window(s.Data.Conflicts.Count, s.Selected, rows);

        for (var i = window.First; i < window.Last; i++)
        {
            var c = s.Data.Conflicts[i];
            var here = i == s.Selected;

            t.AddRow(
                here ? "[bold]>[/]" : " ",
                here ? $"[bold]{Markup.Escape(c.Path)}[/]" : Markup.Escape(c.Path),
                $"[dim]{Markup.Escape(c.Reason)}[/]");
        }

        return new Rows(
            new Markup($"[bold]{s.Data.Conflicts.Count} file(s) the merge could not settle[/]  "
                + "[dim]your files are untouched; the other version is beside each one[/]"),
            t);
    }

    // ---------- diff ----------

    private static IRenderable Diff(UiState s, int width, int rows)
    {
        var c = s.CurrentConflict;
        if (c is null) return new Markup("[dim]nothing selected[/]");

        var aligned = SideBySide.Align(c.Ours, c.Theirs);
        var window = Window(aligned.Count, s.Scroll, rows, anchorAtTop: true);
        // The panel this goes inside costs a border and a padding on each side.
        // Over-estimating the width by even one character makes every long line
        // wrap, and a wrapped row breaks the alignment the whole view exists for,
        // so the estimate errs low.
        var column = DiffLayout.ColumnFor(width - 4);

        // Composed line by line rather than handed to a table. A table sizes its
        // columns from its headers and content, so the two sides come out
        // different widths and one of them wraps: the reader then sees more of one
        // version than the other, which biases a choice meant to be between equals.
        var lines = new List<IRenderable>
        {
            new Markup($"[dim]{"#".PadLeft(DiffLayout.GutterWidth)}[/]  "
                + $"[bold]{Pad("ours (here)", column)}[/]  "
                + $"[dim]{"#".PadLeft(DiffLayout.GutterWidth)}[/]  "
                + $"[bold]{Pad("theirs", column)}[/]"),
            new Markup("[dim]" + new string('─', DiffLayout.GutterWidth * 2 + column * 2 + 6) + "[/]"),
        };

        foreach (var cell in DiffLayout.Lay(aligned, window.First, rows, column))
        {
            var colour = cell.Kind switch
            {
                SideKind.Same => "dim",
                SideKind.Changed => "yellow",
                SideKind.OnlyLeft => "green",
                SideKind.OnlyRight => "blue",
                _ => "dim",
            };

            var leftColour = cell.Kind is SideKind.OnlyRight ? "dim" : colour;
            var rightColour = cell.Kind is SideKind.OnlyLeft ? "dim" : colour;

            lines.Add(new Markup(
                $"[dim]{cell.LeftNumber}[/]  [{leftColour}]{Markup.Escape(cell.Left)}[/]  "
                + $"[dim]{cell.RightNumber}[/]  [{rightColour}]{Markup.Escape(cell.Right)}[/]"));
        }

        var differing = SideBySide.DifferingRows(aligned).Count;

        return new Panel(new Rows(lines))
            .Header($"[bold] {Markup.Escape(c.Path)} [/][dim] {differing} differing, "
                + $"rows {window.First + 1}-{Math.Min(aligned.Count, window.First + rows)} of {aligned.Count} [/]")
            .Expand()
            .BorderColor(Color.Grey)
            .Padding(1, 0)
            .Collapse();
    }

    private static string Pad(string text, int width)
        => text.Length >= width ? text[..width] : text.PadRight(width);

    // ---------- help ----------

    private static IRenderable Help()
    {
        var t = new Table().Border(TableBorder.None).HideHeaders();
        t.AddColumn(new TableColumn("k").PadRight(4));
        t.AddColumn("v");

        foreach (var (key, what) in new[]
                 {
                     ("↑ ↓ / j k", "move"),
                     ("pgup pgdn", "move by a screenful"),
                     ("home end", "first, last"),
                     ("enter", "open the diff, or go back"),
                     ("esc", "back"),
                     ("o", "keep OUR version of this file"),
                     ("t", "take THEIR version of this file"),
                     ("r", "read the workspace again"),
                     ("?", "these keys"),
                     ("q", "quit"),
                 })
            t.AddRow($"[bold]{key}[/]", $"[dim]{what}[/]");

        return new Panel(t).Header("[bold] keys [/]").Expand();
    }

    // ---------- shared ----------

    /// <summary>
    /// The slice of a list to draw, keeping the cursor on screen.
    /// </summary>
    /// <remarks>
    /// The cursor is kept a little away from the edge so there is always context
    /// after it. A list that scrolls only once the selection reaches the last row
    /// makes it impossible to see what is coming.
    /// </remarks>
    private static (int First, int Last) Window(int count, int cursor, int rows, bool anchorAtTop = false)
    {
        if (count <= rows) return (0, count);

        if (anchorAtTop)
        {
            var top = Math.Clamp(cursor, 0, Math.Max(0, count - rows));
            return (top, Math.Min(count, top + rows));
        }

        var margin = Math.Min(2, rows / 3);
        var first = Math.Clamp(cursor - margin, 0, Math.Max(0, count - rows));
        return (first, Math.Min(count, first + rows));
    }

    private static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        return d switch
        {
            { TotalSeconds: < 90 } => "[green]just now[/]",
            { TotalMinutes: < 90 } => $"[dim]{d.TotalMinutes:F0} min ago[/]",
            { TotalHours: < 36 } => $"[dim]{d.TotalHours:F0} h ago[/]",
            _ => $"[dim]{d.TotalDays:F0} d ago[/]",
        };
    }

    private static string Shorten(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
    }
}
