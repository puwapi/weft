using Spectre.Console;
using Weft.Core.Git;
using Weft.Core.Store;
using Weft.Core.Workspace;

namespace Weft.Cli.Commands;

/// <summary>Uncommitted work: what is at risk here, and how to put it down elsewhere.</summary>
internal static class CarryCommands
{
    // ---------- carry: is my work anywhere but on this disk? ----------

    public static async Task<int> CarryAsync(string? rootOverride, CancellationToken ct)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());
        if (!root.IsInitialised)
        {
            AnsiConsole.MarkupLine("[red]No workspace here.[/] [dim]Run [bold]weft init[/] first.[/]");
            return 1;
        }

        var store = new ObjectStore(root.StorePath);
        var refs = new RefStore(root.MetaPath);
        var git = new GitRunner();

        var head = refs.ReadHead();
        var pushed = refs.ReadPushed();

        var recorded = head is null
            ? []
            : Snapshot.Parse(store.Get(head.Value)).Carried;

        // What is on disk RIGHT NOW, which is not the same as what the last
        // snapshot recorded. The gap between the two is exactly the work that
        // exists nowhere else.
        var walk = new TreeWalk(root.Path, root.Policy).Run(ct);
        var repos = await new RepoDiscovery(root.Path, git).ResolveAsync(walk.CheckoutRoots, ct: ct).ConfigureAwait(false);
        var live = await new RepoStateReader(git).ReadAsync(repos, ct: ct).ConfigureAwait(false);

        var dirty = live.Where(r => r.DirtyFiles > 0).ToList();

        AnsiConsole.WriteLine();

        if (dirty.Count == 0 && recorded.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing uncommitted anywhere.[/]");
            return 0;
        }

        var byPath = recorded.ToDictionary(c => c.RepoPath, StringComparer.Ordinal);
        var safeEverywhere = head is not null && pushed == head;

        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Checkout");
        t.AddColumn("Branch");
        t.AddColumn(new TableColumn("Files").RightAligned());
        t.AddColumn("Where it exists");

        foreach (var r in dirty.OrderByDescending(r => r.DirtyFiles))
        {
            var carried = byPath.GetValueOrDefault(r.Path);

            var where = carried is null
                ? "[red]this disk only[/]"
                : safeEverywhere
                    ? "[green]on the server[/]"
                    : "[yellow]snapshotted, not pushed[/]";

            t.AddRow(
                Markup.Escape(r.Path),
                r.Branch.Length == 0 ? "[dim](detached)[/]" : Markup.Escape(r.Branch),
                r.DirtyFiles.ToString(),
                where);
        }

        AnsiConsole.Write(t);
        AnsiConsole.WriteLine();

        var atRisk = dirty.Count(r => !byPath.ContainsKey(r.Path));
        if (atRisk > 0)
            AnsiConsole.MarkupLine($"[red]{atRisk} checkout(s) hold work that exists on this disk and nowhere else.[/] "
                + "[dim]Run [bold]weft snapshot[/] then [bold]weft push[/].[/]");
        else if (!safeEverywhere)
            AnsiConsole.MarkupLine("[yellow]Recorded here, but not on the server.[/] [dim]Run [bold]weft push[/].[/]");
        else
            AnsiConsole.MarkupLine("[green]All of it is on the server.[/] "
                + "[dim]Another machine can pick it up with [bold]weft land[/].[/]");

        return 0;
    }

    // ---------- land: put someone else's work down here ----------

    public static async Task<int> LandAsync(
        string? rootOverride, string? fromMachine, string? repoFilter, bool force, bool threeWay,
        bool dryRun, CancellationToken ct)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());
        if (!root.IsInitialised)
        {
            AnsiConsole.MarkupLine("[red]No workspace here.[/]");
            return 1;
        }

        var store = new ObjectStore(root.StorePath);
        var known = new RefStore(root.MetaPath).ReadRemoteHeads();

        if (known.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to land.[/] [dim]Run [bold]weft pull[/] first.[/]");
            return 1;
        }

        var candidates = known
            .Where(h => fromMachine is null || h.MachineName.Equals(fromMachine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No machine called[/] [bold]{Markup.Escape(fromMachine!)}[/] [yellow]is known here.[/]");
            return 1;
        }

        if (candidates.Count > 1)
        {
            AnsiConsole.MarkupLine("[yellow]More than one machine.[/] [dim]Say which with [bold]--from[/]:[/]");
            foreach (var h in candidates) AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(h.MachineName)}[/]");
            return 1;
        }

        var them = candidates[0];
        if (!ChunkId.TryParse(them.Snapshot, out var theirHead))
        {
            AnsiConsole.MarkupLine("[red]The recorded head is unreadable.[/] [dim]Run [bold]weft pull[/] again.[/]");
            return 1;
        }

        var carried = Snapshot.Parse(store.Get(theirHead)).Carried
            .Where(c => repoFilter is null || c.RepoPath.Equals(repoFilter, StringComparison.Ordinal))
            .ToList();

        if (carried.Count == 0)
        {
            AnsiConsole.MarkupLine(repoFilter is null
                ? $"[dim]{Markup.Escape(them.MachineName)} has no uncommitted work.[/]"
                : $"[dim]{Markup.Escape(them.MachineName)} has no uncommitted work in[/] [bold]{Markup.Escape(repoFilter)}[/]");
            return 0;
        }

        var git = new GitRunner();
        var capture = new WorkCapture(git);
        var reader = new RepoStateReader(git);

        var walk = new TreeWalk(root.Path, root.Policy).Run(ct);
        var repos = await new RepoDiscovery(root.Path, git).ResolveAsync(walk.CheckoutRoots, ct: ct).ConfigureAwait(false);
        var here = (await reader.ReadAsync(repos, ct: ct).ConfigureAwait(false))
            .ToDictionary(r => r.Path, StringComparer.Ordinal);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Landing work from {Markup.Escape(them.MachineName)}[/]"
            + (dryRun ? "  [dim](dry run: nothing will be written)[/]" : ""));
        AnsiConsole.WriteLine();

        var landed = 0;
        var refused = 0;

        foreach (var work in carried)
        {
            var outcome = await LandOneAsync(
                root, store, capture, here, work, force, threeWay, dryRun, ct).ConfigureAwait(false);

            if (outcome) landed++; else refused++;
        }

        AnsiConsole.WriteLine();
        if (dryRun)
            AnsiConsole.MarkupLine($"[dim]{landed} would land, {refused} would be refused. Nothing was written.[/]");
        else
            AnsiConsole.MarkupLine($"[green]{landed} landed[/]"
                + (refused > 0 ? $", [yellow]{refused} refused[/]" : ""));

        return refused > 0 ? 3 : 0;
    }

    private static async Task<bool> LandOneAsync(
        WeftRoot root, ObjectStore store, WorkCapture capture,
        IReadOnlyDictionary<string, RepoState> here, CarriedWork work,
        bool force, bool threeWay, bool dryRun, CancellationToken ct)
    {
        void Refuse(string why, string? remedy = null)
        {
            AnsiConsole.MarkupLine($"  [yellow]refused[/]  [bold]{Markup.Escape(work.RepoPath)}[/]  [dim]{Markup.Escape(why)}[/]");
            if (remedy is not null) AnsiConsole.MarkupLine($"           [dim]{remedy}[/]");
        }

        if (!here.TryGetValue(work.RepoPath, out var target))
        {
            Refuse($"no checkout at '{work.RepoPath}' here",
                "clone the repository first: landing cannot create one, and guessing where it belongs would be worse");
            return false;
        }

        var dir = Path.Combine(root.Path, work.RepoPath.Replace('/', Path.DirectorySeparatorChar));

        // Refusing a dirty tree is the single most important check here. Applying
        // a patch on top of somebody else's uncommitted changes is exactly the
        // gesture this whole feature exists to make unnecessary, and doing it by
        // accident would destroy the work it was meant to protect.
        if (target.DirtyFiles > 0 && !force)
        {
            Refuse($"{target.DirtyFiles} uncommitted file(s) here already",
                "commit or stash them first, or pass --force if you are certain");
            return false;
        }

        if (target.Head != work.BaseCommit && !threeWay)
        {
            Refuse($"taken on {work.BaseCommit[..Math.Min(7, work.BaseCommit.Length)]}, "
                 + $"this checkout is on {(target.Head.Length >= 7 ? target.Head[..7] : target.Head)}",
                "check out the same commit, or pass --3way to let git reconcile it");
            return false;
        }

        var patch = Reassemble(store, work.PatchChunks);

        var check = await capture.CheckAsync(dir, patch, threeWay, ct).ConfigureAwait(false);
        if (!check.Ok)
        {
            // Checked before applying, always. 'git apply' is all-or-nothing, but
            // only if it never starts: reporting the reason beforehand is what
            // keeps a refusal from becoming a half-applied tree.
            Refuse("the patch does not apply cleanly",
                FirstLine(check.StdErr) + (threeWay ? "" : "  (try --3way)"));
            return false;
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine($"  [green]would land[/]  [bold]{Markup.Escape(work.RepoPath)}[/]  "
                + $"[dim]{work.ChangedFiles} file(s), {work.PatchBytes / 1024.0:F1} KB, from {Markup.Escape(Branch(work))}[/]");
            return true;
        }

        var apply = await capture.ApplyAsync(dir, patch, threeWay, ct: ct).ConfigureAwait(false);
        if (!apply.Ok)
        {
            Refuse("git refused the patch", FirstLine(apply.StdErr));
            return false;
        }

        AnsiConsole.MarkupLine($"  [green]landed[/]  [bold]{Markup.Escape(work.RepoPath)}[/]  "
            + $"[dim]{work.ChangedFiles} file(s) from {Markup.Escape(Branch(work))}[/]");

        // Said plainly rather than left to be discovered. Someone who staged work
        // on the other machine will look for it in the index here and not find it.
        if (work.StagedFiles > 0)
            AnsiConsole.MarkupLine($"          [dim]{work.StagedFiles} file(s) were staged there; "
                + "everything landed unstaged here.[/]");

        return true;
    }

    private static string Branch(CarriedWork w)
        => w.Branch.Length == 0 ? "a detached HEAD" : w.Branch;

    private static byte[] Reassemble(ObjectStore store, IReadOnlyList<ChunkId> chunks)
    {
        var parts = chunks.Select(store.Get).ToList();
        var buffer = new byte[parts.Sum(p => p.Length)];

        var at = 0;
        foreach (var p in parts) { p.CopyTo(buffer, at); at += p.Length; }
        return buffer;
    }

    private static string FirstLine(string text)
        => text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
}
