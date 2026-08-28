using System.Diagnostics;
using Spectre.Console;
using Weft.Core.Git;
using Weft.Core.Workspace;

namespace Weft.Cli.Commands;

/// <summary>
/// Reports what weft sees: repositories, worktrees, loose files, refusals.
/// </summary>
/// <remarks>
/// Read-only by design. It is the command to reach for when something looks
/// wrong, so it must never be the command that changes anything.
/// </remarks>
internal static class ScanCommand
{
    public static async Task<int> RunAsync(string? rootOverride, bool verbose, CancellationToken ct)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());

        if (!root.IsInitialised)
            AnsiConsole.MarkupLine(
                $"[yellow]No workspace here.[/] Reading [blue]{Markup.Escape(root.Path)}[/] with the built-in rules. " +
                "Run [bold]weft init[/] to keep your own.");

        var git = new GitRunner();
        if (await git.ProbeAsync(ct).ConfigureAwait(false) is null)
        {
            AnsiConsole.MarkupLine("[red]git is not on PATH.[/] weft delegates every repository operation to git.");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var walk = new TreeWalk(root.Path, root.Policy).Run(ct);
        var walkMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var repos = await new RepoDiscovery(root.Path, git)
            .ResolveAsync(walk.CheckoutRoots, ct: ct).ConfigureAwait(false);
        var gitMs = sw.ElapsedMilliseconds;

        Render(root, walk, repos, walkMs, gitMs, verbose);
        return 0;
    }

    private static void Render(
        WeftRoot root, TreeWalkResult walk, IReadOnlyList<Repository> repos,
        long walkMs, long gitMs, bool verbose)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(root.Path)}[/]");
        AnsiConsole.WriteLine();

        var summary = new Table().Border(TableBorder.None).HideHeaders();
        summary.AddColumn(new TableColumn("k").PadRight(3));
        summary.AddColumn("v");
        summary.AddRow("Repositories", $"[bold]{repos.Count}[/]");
        summary.AddRow("Working trees", $"[bold]{repos.Sum(r => r.Checkouts.Count)}[/]");
        summary.AddRow("Loose files", $"[bold]{walk.LooseFiles.Count}[/]");
        summary.AddRow("Refused", walk.Refused.Count == 0
            ? "[dim]none[/]"
            : $"[yellow]{walk.Refused.Count}[/] [dim](confidential)[/]");
        summary.AddRow("Walk", $"[dim]{walk.VisitedEntries} entries seen, "
            + $"{walk.PrunedDirectories} directories pruned, {walkMs} ms[/]");
        summary.AddRow("git", $"[dim]{gitMs} ms[/]");
        AnsiConsole.Write(summary);

        // Trees that cannot be synced, or that the OS may delete. This is the
        // section the whole command exists for: a branch living only in a temp
        // directory is invisible everywhere else.
        var volatiles = repos.SelectMany(r => r.Volatile).ToList();
        var external = repos.SelectMany(r => r.External).Where(c => !c.IsVolatile).ToList();

        if (volatiles.Count > 0 || external.Count > 0)
        {
            AnsiConsole.WriteLine();
            var risk = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
            risk.AddColumn("[yellow]At risk[/]");
            risk.AddColumn("Repository");
            risk.AddColumn("Why");

            foreach (var c in volatiles)
                risk.AddRow(
                    $"[dim]{Markup.Escape(Shorten(c.AbsolutePath))}[/]",
                    Markup.Escape(RepoNameOf(repos, c)),
                    "[yellow]temp directory, the OS may reclaim it[/]");

            foreach (var c in external)
                risk.AddRow(
                    $"[dim]{Markup.Escape(Shorten(c.AbsolutePath))}[/]",
                    Markup.Escape(RepoNameOf(repos, c)),
                    "[yellow]outside the workspace, weft cannot sync it[/]");

            AnsiConsole.Write(risk);
        }

        if (!verbose) return;

        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Repository");
        t.AddColumn(new TableColumn("Trees").RightAligned());
        foreach (var r in repos)
        {
            var extra = r.Checkouts.Count - 1;
            t.AddRow(Markup.Escape(r.Name), extra == 0 ? "[dim]1[/]" : $"[bold]{r.Checkouts.Count}[/]");
        }
        AnsiConsole.Write(t);

        if (walk.LooseFiles.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[blue]Loose files[/] [dim](outside every repository: these have no other home)[/]");
            foreach (var g in walk.LooseFiles
                         .GroupBy(f => f.Contains('/') ? f[..f.IndexOf('/')] + "/" : "(root)")
                         .OrderByDescending(g => g.Count()))
            {
                AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(g.Key)}[/] [dim]{g.Count()}[/]");
                if (g.Key == "(root)")
                    foreach (var f in g.OrderBy(x => x, StringComparer.Ordinal))
                        AnsiConsole.MarkupLine($"      [dim]{Markup.Escape(f)}[/]");
            }
        }

        if (walk.Refused.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Refused as confidential[/] [dim](not overridable)[/]");
            foreach (var p in walk.Refused.Take(40))
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(p)}[/]");
            if (walk.Refused.Count > 40)
                AnsiConsole.MarkupLine($"  [dim]and {walk.Refused.Count - 40} more[/]");
        }
    }

    private static string RepoNameOf(IReadOnlyList<Repository> repos, Checkout c)
        => repos.FirstOrDefault(r => r.CommonDir == c.CommonDir)?.Name ?? "?";

    private static string Shorten(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }
}
