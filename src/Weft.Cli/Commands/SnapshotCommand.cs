using Spectre.Console;
using Weft.Core.Git;
using Weft.Core.Store;
using Weft.Core.Workspace;

namespace Weft.Cli.Commands;

/// <summary>Records the state of the workspace.</summary>
internal static class SnapshotCommand
{
    public static async Task<int> RunAsync(string? rootOverride, bool verbose, CancellationToken ct)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());

        if (!root.IsInitialised)
        {
            AnsiConsole.MarkupLine($"[red]No workspace at[/] [blue]{Markup.Escape(root.Path)}[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]weft init[/] first. weft will not create one as a side effect of a snapshot.[/]");
            return 1;
        }

        var git = new GitRunner();
        if (await git.ProbeAsync(ct).ConfigureAwait(false) is null)
        {
            AnsiConsole.MarkupLine("[red]git is not on PATH.[/]");
            return 1;
        }

        var machine = MachineStore.LoadOrMint();
        var store = new ObjectStore(root.StorePath);
        var engine = new SnapshotEngine(root, store, git);

        SnapshotResult result = default!;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Reading the workspace...", async _ =>
            {
                result = await engine.CreateAsync(machine, ct: ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

        Render(result, store, machine, verbose);
        return 0;
    }

    private static void Render(SnapshotResult r, ObjectStore store, MachineIdentity machine, bool verbose)
    {
        AnsiConsole.WriteLine();

        if (r.NoChange)
        {
            AnsiConsole.MarkupLine($"[dim]Nothing changed since[/] [blue]{r.Id.ToString()[..12]}[/][dim]. No snapshot recorded.[/]");
            AnsiConsole.MarkupLine($"[dim]{r.FilesRead} files read in {r.ElapsedMs} ms.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[green]Snapshot[/] [blue]{r.Id.ToString()[..12]}[/] "
            + $"[dim]on {Markup.Escape(machine.Name)}[/]");
        AnsiConsole.WriteLine();

        var t = new Table().Border(TableBorder.None).HideHeaders();
        t.AddColumn(new TableColumn("k").PadRight(3));
        t.AddColumn("v");

        t.AddRow("Loose files", $"[bold]{r.Snapshot.FileCount}[/] [dim]{Bytes(r.Snapshot.TotalBytes)}[/]");

        var d = r.Diff;
        t.AddRow("Changed", d.IsEmpty
            ? "[dim]nothing[/]"
            : $"[green]+{d.Added.Count}[/] [yellow]~{d.Changed.Count}[/] [red]-{d.Removed.Count}[/]");

        // The number that says whether the design works: what a push would send,
        // against what the workspace actually holds. Content only; the manifest
        // is reported beside it so the share cannot exceed 100%.
        var share = r.Snapshot.TotalBytes == 0 ? 0 : (double)r.NewBytes / r.Snapshot.TotalBytes;
        t.AddRow("To send", r.NewChunks == 0 && r.ManifestBytes == 0
            ? "[dim]nothing new[/]"
            : $"[bold]{Bytes(r.NewBytes)}[/] [dim]in {r.NewChunks} chunks, {share:P2} of the workspace"
              + $"  (+{Bytes(r.ManifestBytes)} manifest)[/]");

        var (objects, storedBytes) = store.Measure();
        t.AddRow("Store", $"[dim]{objects} objects, {Bytes(storedBytes)}[/]");
        t.AddRow("Repositories", $"[dim]{r.Snapshot.Repos.Count(x => x.IsPrimary)} "
            + $"({r.Snapshot.Repos.Count} working trees)[/]");
        t.AddRow("Took", $"[dim]{r.ElapsedMs} ms[/]");

        AnsiConsole.Write(t);

        // Uncommitted work, the failure this tool exists for. Reported every time,
        // not only when asked: a branch that lives on one disk is invisible
        // precisely because nobody thought to look.
        var dirty = r.Snapshot.Repos.Where(x => x.DirtyFiles > 0).ToList();
        if (dirty.Count > 0)
        {
            AnsiConsole.WriteLine();
            var w = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
            w.AddColumn("[yellow]Uncommitted[/]");
            w.AddColumn("Branch");
            w.AddColumn(new TableColumn("Files").RightAligned());

            foreach (var x in dirty.OrderByDescending(x => x.DirtyFiles))
                w.AddRow(Markup.Escape(x.Path),
                    x.Branch.Length == 0 ? "[dim](detached)[/]" : Markup.Escape(x.Branch),
                    x.DirtyFiles.ToString());

            AnsiConsole.Write(w);
        }

        if (!verbose) return;

        foreach (var (label, colour, paths) in new (string, string, IEnumerable<string>)[]
        {
            ("Added", "green", d.Added.Select(e => e.Path)),
            ("Changed", "yellow", d.Changed.Select(e => e.To.Path)),
            ("Removed", "red", d.Removed.Select(e => e.Path)),
        })
        {
            var list = paths.ToList();
            if (list.Count == 0) continue;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[{colour}]{label}[/]");
            foreach (var p in list.Take(50)) AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(p)}[/]");
            if (list.Count > 50) AnsiConsole.MarkupLine($"  [dim]and {list.Count - 50} more[/]");
        }
    }

    private static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{n / 1024.0 / 1024:F1} MB",
        _ => $"{n / 1024.0 / 1024 / 1024:F2} GB",
    };
}
