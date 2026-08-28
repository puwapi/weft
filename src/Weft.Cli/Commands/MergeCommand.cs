using Spectre.Console;
using Weft.Core.Git;
using Weft.Core.Merge;
using Weft.Core.Store;
using Weft.Core.Workspace;

namespace Weft.Cli.Commands;

/// <summary>Reconciles this machine with another one.</summary>
internal static class MergeCommand
{
    public static async Task<int> RunAsync(
        string? rootOverride, string? fromMachine, bool @continue, bool abort, CancellationToken ct)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());
        if (!root.IsInitialised)
        {
            AnsiConsole.MarkupLine("[red]No workspace here.[/] [dim]Run [bold]weft init[/] first.[/]");
            return 1;
        }

        var key = LocalSecrets.TryLoadKey(root);
        if (key is null)
        {
            AnsiConsole.MarkupLine("[red]This workspace has no key.[/]");
            return 1;
        }

        var store = new ObjectStore(root.StorePath);
        var applier = new MergeApplier(root, store);
        var refs = new RefStore(root.MetaPath);

        if (abort) return Abort(applier);
        if (@continue) return await ContinueAsync(root, store, applier, refs, ct).ConfigureAwait(false);

        var pending = applier.LoadState();
        if (pending is not null)
        {
            AnsiConsole.MarkupLine($"[yellow]A merge is already under way[/] [dim](started {pending.StartedUtc:u})[/]");
            AnsiConsole.MarkupLine("[dim]Finish it with [bold]weft merge --continue[/], or drop it with [bold]weft merge --abort[/].[/]");
            return 1;
        }

        var ours = refs.ReadHead();
        if (ours is null)
        {
            AnsiConsole.MarkupLine("[red]Nothing to merge:[/] [dim]this workspace has no snapshot yet. Run [bold]weft snapshot[/].[/]");
            return 1;
        }

        var theirs = ResolveTheirs(root, fromMachine);
        if (theirs is null) return 1;

        var (theirHead, theirName) = theirs.Value;

        MergeOutcome outcome;
        try
        {
            outcome = new MergeEngine(store).Compute(ours.Value, theirHead);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or CorruptObjectException)
        {
            AnsiConsole.MarkupLine($"[red]Cannot merge:[/] [dim]{Markup.Escape(e.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]weft pull[/] first: the other machine's objects are not all here yet.[/]");
            return 1;
        }

        return await ApplyAsync(root, store, applier, refs, outcome, theirName, ct).ConfigureAwait(false);
    }

    private static async Task<int> ApplyAsync(
        WeftRoot root, ObjectStore store, MergeApplier applier, RefStore refs,
        MergeOutcome outcome, string theirName, CancellationToken ct)
    {
        if (outcome.FastForward && outcome.Items.Count == 0)
        {
            AnsiConsole.MarkupLine($"[dim]Already ahead of[/] [bold]{Markup.Escape(theirName)}[/][dim]. Nothing to do.[/]");
            RenderRepos(outcome);
            return 0;
        }

        var applied = applier.Apply(outcome);
        Render(outcome, applied, theirName);

        if (applied.Conflicts.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Your files were left exactly as they were. The other version is beside each "
                + $"one as [bold]<file>{MergeApplier.TheirsSuffix}[/]"
                + (outcome.Base is null ? "" : $", with the common ancestor as [bold]<file>{MergeApplier.BaseSuffix}[/]")
                + ".[/]");
            AnsiConsole.MarkupLine("[dim]Edit each file until you are happy, then run [bold]weft merge --continue[/].[/]");
            return 3;
        }

        var snapshot = await RecordAsync(root, store, refs, outcome, ct).ConfigureAwait(false);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Merged[/] [dim]recorded as[/] [blue]{snapshot.ToString()[..12]}[/]");
        return 0;
    }

    private static async Task<int> ContinueAsync(
        WeftRoot root, ObjectStore store, MergeApplier applier, RefStore refs, CancellationToken ct)
    {
        var state = applier.LoadState();
        if (state is null)
        {
            AnsiConsole.MarkupLine("[yellow]No merge is under way.[/]");
            return 1;
        }

        var unresolved = applier.UnresolvedOf(state);
        if (unresolved.Count > 0)
        {
            // The companion file still being there is the signal. It is a fact on
            // disk rather than a flag someone has to remember to set, so a merge
            // cannot be declared finished while a decision is still pending.
            AnsiConsole.MarkupLine($"[yellow]{unresolved.Count} conflict(s) still waiting[/]");
            foreach (var p in unresolved)
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(p)}[/]   [dim]{MergeApplier.TheirsSuffix} is still there[/]");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[dim]Settle each file, then delete its [bold]{MergeApplier.TheirsSuffix}[/] "
                + "companion to say so.[/]");
            return 3;
        }

        foreach (var p in state.Conflicts) applier.ClearCompanions(p);

        var outcome = new MergeOutcome
        {
            Base = null,
            Ours = ChunkId.Parse(state.Ours),
            Theirs = ChunkId.Parse(state.Theirs),
            FastForward = false,
            Items = [],
            Repos = [],
        };

        var snapshot = await RecordAsync(root, store, refs, outcome, ct).ConfigureAwait(false);
        applier.ClearState();

        AnsiConsole.MarkupLine($"[green]Merge finished[/] [dim]recorded as[/] [blue]{snapshot.ToString()[..12]}[/]");
        return 0;
    }

    private static int Abort(MergeApplier applier)
    {
        var state = applier.LoadState();
        if (state is null)
        {
            AnsiConsole.MarkupLine("[yellow]No merge is under way.[/]");
            return 1;
        }

        foreach (var p in state.Conflicts) applier.ClearCompanions(p);
        applier.ClearState();

        // Files that were merged cleanly are NOT rolled back, and saying so
        // matters: 'abort' drops the pending decisions, it does not undo the work
        // already reconciled, and every version remains in the object store.
        AnsiConsole.MarkupLine("[green]Merge dropped.[/]");
        AnsiConsole.MarkupLine("[dim]Files that had already merged cleanly were left as they are. "
            + "Nothing was lost: every version is still in the object store.[/]");
        return 0;
    }

    /// <summary>Records the merge, with both heads as parents.</summary>
    private static async Task<ChunkId> RecordAsync(
        WeftRoot root, ObjectStore store, RefStore refs, MergeOutcome outcome, CancellationToken ct)
    {
        var engine = new SnapshotEngine(root, store, new GitRunner());

        var result = await engine.CreateAsync(
            MachineStore.LoadOrMint(),
            extraParents: [outcome.Ours, outcome.Theirs],
            ct: ct).ConfigureAwait(false);

        refs.WriteHead(result.Id);
        return result.Id;
    }

    /// <summary>
    /// Picks the machine to merge with, from what the last pull recorded.
    /// </summary>
    /// <remarks>
    /// No network. A pull fetches and writes down where each machine stood, in
    /// LOCAL snapshot ids; a merge reconciles what is already here. Keeping the
    /// two apart means a merge cannot half-finish because a connection dropped,
    /// and it can be run on a train.
    /// </remarks>
    private static (ChunkId Head, string Name)? ResolveTheirs(WeftRoot root, string? fromMachine)
    {
        var known = new RefStore(root.MetaPath).ReadRemoteHeads();

        if (known.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nothing to merge with.[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]weft pull[/] first: a merge reconciles what is already here.[/]");
            return null;
        }

        var others = known
            .Where(h => fromMachine is null
                     || h.MachineName.Equals(fromMachine, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (others.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No machine called[/] [bold]{Markup.Escape(fromMachine!)}[/] "
                + "[yellow]has anything here.[/] [dim]Known:[/]");
            foreach (var h in known) AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(h.MachineName)}[/]");
            return null;
        }

        if (others.Count > 1)
        {
            // Never chosen for the user. Merging with an arbitrary machine when
            // three are in play produces a result nobody asked for and that the
            // command line does not record.
            AnsiConsole.MarkupLine("[yellow]More than one machine has work to merge.[/] [dim]Say which:[/]");
            foreach (var h in others)
                AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(h.MachineName)}[/] [dim]{h.Snapshot[..12]}"
                    + $"  seen {h.SeenUtc:u}[/]");

            AnsiConsole.MarkupLine("[dim]  weft merge --from <machine>[/]");
            return null;
        }

        var them = others[0];
        if (!ChunkId.TryParse(them.Snapshot, out var head))
        {
            AnsiConsole.MarkupLine("[red]The recorded head is unreadable.[/] [dim]Run [bold]weft pull[/] again.[/]");
            return null;
        }

        return (head, them.MachineName);
    }

    private static void Render(MergeOutcome outcome, ApplyResult applied, string theirName)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Merging with {Markup.Escape(theirName)}[/]"
            + (outcome.Base is null
                ? "  [dim](no shared history: everything reads as added on one side)[/]"
                : $"  [dim](common ancestor {outcome.Base.Value.ToString()[..12]})[/]"));
        AnsiConsole.WriteLine();

        var t = new Table().Border(TableBorder.None).HideHeaders();
        t.AddColumn(new TableColumn("k").PadRight(3));
        t.AddColumn("v");
        t.AddRow("Written", applied.Written == 0 ? "[dim]nothing[/]" : $"[green]{applied.Written}[/]");
        t.AddRow("Deleted", applied.Deleted == 0 ? "[dim]nothing[/]" : $"[red]{applied.Deleted}[/]");
        t.AddRow("Conflicts", applied.Conflicts.Count == 0 ? "[dim]none[/]" : $"[yellow]{applied.Conflicts.Count}[/]");
        AnsiConsole.Write(t);

        if (applied.Notes.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Resolved, but not obviously[/]");
            foreach (var (path, note) in applied.Notes)
                AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(path)}[/]  [dim]{Markup.Escape(note)}[/]");
        }

        if (applied.Conflicts.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Waiting on you[/]");
            foreach (var path in applied.Conflicts)
            {
                var item = outcome.Items.First(i => i.Path == path);
                AnsiConsole.MarkupLine($"  [bold]{Markup.Escape(path)}[/]  [dim]{Markup.Escape(item.ConflictReason ?? "")}[/]");
            }
        }

        RenderRepos(outcome);
    }

    private static void RenderRepos(MergeOutcome outcome)
    {
        if (outcome.Repos.Count == 0) return;

        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.Rounded);
        t.AddColumn("Repository");
        t.AddColumn("Here");
        t.AddColumn("There");

        foreach (var d in outcome.Repos)
            t.AddRow(
                Markup.Escape(d.Path),
                d.Ours is null ? "[red]not cloned[/]" : Markup.Escape(Describe(d.Ours)),
                d.Theirs is null ? "[dim]absent[/]" : Markup.Escape(Describe(d.Theirs)));

        AnsiConsole.Write(t);

        // Stated every time, because it is the rule most likely to surprise: two
        // machines on two branches is normal, not something to reconcile.
        AnsiConsole.MarkupLine("[dim]Repository state is reported, never merged. Two machines on two branches "
            + "is not a disagreement.[/]");

        var missing = outcome.Repos.Where(r => r.MissingHere).ToList();
        if (missing.Count > 0)
        {
            AnsiConsole.MarkupLine($"[dim]{missing.Count} repository(ies) exist there and not here. To get one:[/]");
            foreach (var d in missing.Take(5))
                AnsiConsole.MarkupLine($"  [dim]git clone {Markup.Escape(d.Theirs!.Remote)} {Markup.Escape(d.Path)}[/]");
        }
    }

    private static string Describe(RepoState r)
        => (r.Branch.Length == 0 ? "(detached)" : r.Branch)
         + (r.Head.Length >= 7 ? $" @ {r.Head[..7]}" : "")
         + (r.DirtyFiles > 0 ? $"  {r.DirtyFiles} uncommitted" : "");
}
