using Spectre.Console;
using Weft.Core.Crypto;
using Weft.Core.Git;
using Weft.Core.Protocol;
using Weft.Core.Remote;
using Weft.Core.Store;
using Weft.Core.Workspace;

namespace Weft.Cli.Commands;

/// <summary>Everything that talks to a server, plus the key that keeps it out.</summary>
internal static class RemoteCommands
{
    private sealed record Context(WeftRoot Root, WorkspaceKey Key, MachineIdentity Machine);

    /// <summary>Loads what every remote command needs, or explains exactly what is missing.</summary>
    private static Context? Load(string? rootOverride)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());

        if (!root.IsInitialised)
        {
            AnsiConsole.MarkupLine($"[red]No workspace at[/] [blue]{Markup.Escape(root.Path)}[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]weft init[/] first.[/]");
            return null;
        }

        var key = LocalSecrets.TryLoadKey(root);
        if (key is null)
        {
            AnsiConsole.MarkupLine("[red]This workspace has no key.[/]");
            AnsiConsole.MarkupLine("[dim]Content is encrypted before it reaches the server, so nothing can be "
                + "sent or read without one. Run [bold]weft key set <key>[/] with the key from another machine.[/]");
            return null;
        }

        return new Context(root, key, MachineStore.LoadOrMint());
    }

    // ---------- key ----------

    public static Task<int> KeyShowAsync(string? rootOverride, bool reveal)
    {
        var ctx = Load(rootOverride);
        if (ctx is null) return Task.FromResult(1);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Workspace key [dim]{ctx.Key.Fingerprint()}[/]");

        if (!reveal)
        {
            // Not printed by default. This command gets run to check which
            // workspace a machine is on, often with someone watching or a terminal
            // being recorded, and the fingerprint answers that without putting the
            // key itself into scrollback.
            AnsiConsole.MarkupLine("[dim]Pass [bold]--reveal[/] to print the key itself.[/]");
            return Task.FromResult(0);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(ctx.Key.ToDisplayString())}[/]");
        return Task.FromResult(0);
    }

    public static Task<int> KeySetAsync(string? rootOverride, string keyText, bool force)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());
        if (!root.IsInitialised)
        {
            AnsiConsole.MarkupLine("[red]No workspace here.[/] [dim]Run [bold]weft init[/] first.[/]");
            return Task.FromResult(1);
        }

        WorkspaceKey key;
        try { key = WorkspaceKey.Parse(keyText); }
        catch (FormatException e)
        {
            AnsiConsole.MarkupLine($"[red]That is not a workspace key.[/] [dim]{Markup.Escape(e.Message)}[/]");
            return Task.FromResult(1);
        }

        var existing = LocalSecrets.TryLoadKey(root);
        if (existing is not null && existing.Fingerprint() != key.Fingerprint() && !force)
        {
            // Replacing a key makes every object already stored under it
            // unreadable, on this machine and on the server. It is never an
            // accident worth allowing quietly.
            AnsiConsole.MarkupLine($"[red]This workspace already holds a different key[/] [dim]({existing.Fingerprint()})[/]");
            AnsiConsole.MarkupLine("[dim]Replacing it makes everything stored under the old key unreadable. "
                + "Pass [bold]--force[/] if that is what you mean.[/]");
            return Task.FromResult(1);
        }

        LocalSecrets.SaveKey(root, key);
        AnsiConsole.MarkupLine($"[green]Key set[/] [dim]{key.Fingerprint()}[/]");
        return Task.FromResult(0);
    }

    // ---------- remote ----------

    public static async Task<int> RemoteAddAsync(
        string? rootOverride, string url, string joinSecret, bool insecure, CancellationToken ct)
    {
        var ctx = Load(rootOverride);
        if (ctx is null) return 1;

        // Judged before anything is sent. The join secret is in this command's
        // arguments, so the first request already carries a credential: checking
        // after the probe would mean checking after the leak.
        var verdict = RemoteUrl.Check(url, insecure);
        if (!verdict.Ok)
        {
            AnsiConsole.WriteLine();
            foreach (var line in verdict.Refusal!.Split('\n'))
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(line)}[/]");
            return 1;
        }

        url = verdict.Url;
        if (verdict.Warning is not null)
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(verdict.Warning)}[/]");

        using var probe = new RemoteClient(url, null);

        ServerInfo info;
        try { info = await probe.InfoAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is RemoteException or HttpRequestException or TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Cannot reach that server.[/] [dim]{Markup.Escape(e.Message)}[/]");
            return 1;
        }

        if (info.Protocol != WeftVersion.Protocol)
        {
            AnsiConsole.MarkupLine($"[red]Protocol mismatch.[/] [dim]The server speaks v{info.Protocol}, "
                + $"this build speaks v{WeftVersion.Protocol}.[/]");
            return 1;
        }

        // Caught here rather than after the first push. Enrolling with the wrong
        // key uploads objects nobody else can read, and the mistake would surface
        // much later, on someone else's machine, as an authentication failure.
        if (!string.IsNullOrEmpty(info.WorkspaceFingerprint)
            && info.WorkspaceFingerprint != ctx.Key.Fingerprint())
        {
            AnsiConsole.MarkupLine($"[red]That server holds a different workspace.[/]");
            AnsiConsole.MarkupLine($"[dim]Server: {info.WorkspaceFingerprint}   here: {ctx.Key.Fingerprint()}[/]");
            AnsiConsole.MarkupLine("[dim]Use [bold]weft key set[/] with the key from a machine already on it.[/]");
            return 1;
        }

        EnrolResponse enrolled;
        try
        {
            enrolled = await probe.EnrolAsync(
                RemoteClient.EnrolmentFor(joinSecret, ctx.Machine, ctx.Key), ct).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            AnsiConsole.MarkupLine($"[red]Enrolment refused.[/] [dim]{Markup.Escape(e.Message)}[/]");
            return 1;
        }

        LocalSecrets.SaveRemote(ctx.Root, new RemoteConfig(url, enrolled.Token, DateTimeOffset.UtcNow));

        AnsiConsole.MarkupLine($"[green]Enrolled[/] [dim]as {Markup.Escape(ctx.Machine.Name)} on {Markup.Escape(url)}[/]");
        AnsiConsole.MarkupLine($"[dim]Server {Markup.Escape(info.Server)}, protocol v{info.Protocol}, "
            + $"writes require weft >= {Markup.Escape(info.MinClient)}[/]");
        return 0;
    }

    /// <summary>Lists the machines the server knows, and whether each is still allowed in.</summary>
    public static async Task<int> RemoteMachinesAsync(string? rootOverride, CancellationToken ct)
    {
        var s = Prepare(rootOverride);
        if (s is null) return 1;
        var (ctx, client) = s.Value;

        using (client)
        {
            IReadOnlyList<HeadEntry> heads;
            try { heads = await client.HeadsAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is RemoteException or HttpRequestException) { return Fail(e); }

            AnsiConsole.WriteLine();
            var t = new Table().Border(TableBorder.Rounded);
            t.AddColumn("Machine");
            t.AddColumn("Id");
            t.AddColumn("Platform");
            t.AddColumn("Last seen");
            t.AddColumn("State");

            foreach (var h in heads.OrderBy(h => h.MachineName, StringComparer.Ordinal))
            {
                var mine = h.MachineId == ctx.Machine.Id;
                t.AddRow(
                    mine ? $"[bold]{Markup.Escape(h.MachineName)}[/] [dim](this one)[/]" : Markup.Escape(h.MachineName),
                    $"[dim]{Markup.Escape(h.MachineId)}[/]",
                    Markup.Escape(h.Platform),
                    Ago(h.LastSeenUtc),
                    h.RevokedUtc is null ? "[green]enrolled[/]" : $"[red]revoked[/] [dim]{Ago(h.RevokedUtc.Value)}[/]");
            }

            AnsiConsole.Write(t);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Withdraw one with [bold]weft remote revoke <id> --join <secret>[/].[/]");
            return 0;
        }
    }

    /// <summary>
    /// Withdraws a machine's token.
    /// </summary>
    /// <remarks>
    /// Asks for the join secret rather than using this machine's token, and says
    /// plainly what revoking does not do: the workspace key is on the revoked
    /// machine's disk, and no server-side action can reach it. Someone who thinks
    /// a revoked laptop can no longer read what it already holds is worse off than
    /// someone who knows they have to rotate the key.
    /// </remarks>
    public static async Task<int> RemoteRevokeAsync(
        string? rootOverride, string machineId, string joinSecret, CancellationToken ct)
    {
        var s = Prepare(rootOverride);
        if (s is null) return 1;
        var (ctx, client) = s.Value;

        using (client)
        {
            if (machineId == ctx.Machine.Id)
            {
                AnsiConsole.MarkupLine("[red]That is this machine.[/]");
                AnsiConsole.MarkupLine("[dim]Revoking it here would only cut this workspace off from its own "
                    + "server. Run this from another machine, on the id you want out.[/]");
                return 1;
            }

            try { await client.RevokeAsync(machineId, joinSecret, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is RemoteException or HttpRequestException) { return Fail(e); }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Revoked[/] [dim]{Markup.Escape(machineId)}[/]");
            AnsiConsole.MarkupLine("[dim]Its token no longer works. What it already pushed is untouched, and "
                + "its snapshot still shows in [bold]weft pull[/]: losing a machine is not a reason to lose "
                + "its work.[/]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]This does not undo what that machine can read.[/]");
            AnsiConsole.MarkupLine("[dim]It still holds the workspace key on its disk, and every object it "
                + "already fetched. If the machine is in someone else's hands, generate a new key, run "
                + "[bold]weft key set --force[/] on the machines you keep, and start a fresh server: "
                + "revocation closes the door, it does not empty the room.[/]");
            return 0;
        }
    }

    // ---------- push and pull ----------

    public static async Task<int> PushAsync(string? rootOverride, CancellationToken ct)
    {
        var s = Prepare(rootOverride);
        if (s is null) return 1;
        var (ctx, client) = s.Value;

        using (client)
        {
            PushResult result = default!;
            try
            {
                await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                    .StartAsync("Pushing...", async _ =>
                    {
                        result = await new SyncEngine(ctx.Root, new ObjectStore(ctx.Root.StorePath), ctx.Key, client)
                            .PushAsync(ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RemoteException or InvalidOperationException or HttpRequestException)
            {
                return Fail(e);
            }

            AnsiConsole.WriteLine();
            if (result.AlreadyCurrent)
            {
                AnsiConsole.MarkupLine($"[dim]The server already had everything for[/] "
                    + $"[blue]{result.Snapshot.ToString()[..12]}[/][dim]. Pointer moved.[/]");
                return 0;
            }

            AnsiConsole.MarkupLine($"[green]Pushed[/] [blue]{result.Snapshot.ToString()[..12]}[/]");
            AnsiConsole.MarkupLine($"[dim]{result.ObjectsUploaded} of {result.ObjectsConsidered} objects sent, "
                + $"{Bytes(result.BytesUploaded)} in {result.ElapsedMs} ms[/]");
            return 0;
        }
    }

    public static async Task<int> PullAsync(string? rootOverride, CancellationToken ct)
    {
        var s = Prepare(rootOverride);
        if (s is null) return 1;
        var (ctx, client) = s.Value;

        using (client)
        {
            PullResult result = default!;
            try
            {
                await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                    .StartAsync("Fetching...", async _ =>
                    {
                        result = await new SyncEngine(ctx.Root, new ObjectStore(ctx.Root.StorePath), ctx.Key, client)
                            .PullAsync(ctx.Machine.Id, ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RemoteException or DecryptionFailedException or HttpRequestException)
            {
                return Fail(e);
            }

            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Machine");
            table.AddColumn("Snapshot");
            table.AddColumn(new TableColumn("Files").RightAligned());
            table.AddColumn("Last seen");

            foreach (var h in result.Heads.OrderBy(h => h.MachineName, StringComparer.Ordinal))
            {
                var mine = h.MachineId == ctx.Machine.Id;
                var hit = result.Fetched.FirstOrDefault(f => f.Machine.MachineId == h.MachineId);
                var snap = hit.Snapshot;

                // The LOCAL id, never the server's. They are different names for
                // the same snapshot, and showing the server's makes what you just
                // pushed look like something else when it comes back.
                var shown = snap is not null
                    ? $"[blue]{hit.LocalId.ToString()[..12]}[/]"
                    : h.Snapshot is null
                        ? "[dim]none yet[/]"
                        : "[dim]not fetched[/]";

                table.AddRow(
                    mine ? $"[bold]{Markup.Escape(h.MachineName)}[/] [dim](this one)[/]" : Markup.Escape(h.MachineName),
                    shown,
                    snap is null ? "[dim]-[/]" : snap.FileCount.ToString(),
                    Ago(h.LastSeenUtc));
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine(result.ObjectsFetched == 0
                ? "[dim]Nothing new to fetch.[/]"
                : $"[green]Fetched[/] [dim]{result.ObjectsFetched} objects, {Bytes(result.BytesFetched)} "
                  + $"in {result.ElapsedMs} ms[/]");

            // Said plainly, every time. Someone who runs 'pull' and sees a table
            // may reasonably assume their files changed; nothing on disk moved,
            // and pretending otherwise would be the more dangerous surprise.
            AnsiConsole.MarkupLine("[dim]Objects are in the local store. Nothing was written into your "
                + "working tree: deciding that is the merge step, which is not built yet.[/]");
            return 0;
        }
    }

    // ---------- shared ----------

    private static (Context Ctx, RemoteClient Client)? Prepare(string? rootOverride)
    {
        var ctx = Load(rootOverride);
        if (ctx is null) return null;

        var remote = LocalSecrets.TryLoadRemote(ctx.Root);
        if (remote is null)
        {
            AnsiConsole.MarkupLine("[red]No server configured.[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]weft remote add <url> --join <secret>[/].[/]");
            return null;
        }

        // Repeated on every push and pull rather than only at 'remote add'. A
        // warning shown once, months ago, on a machine someone else set up, is
        // not a warning anyone has seen.
        var warning = RemoteUrl.WarnAbout(remote.Url);
        if (warning is not null) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(warning)}[/]");

        return (ctx, new RemoteClient(remote.Url, remote.Token));
    }

    private static int Fail(Exception e)
    {
        AnsiConsole.WriteLine();

        if (e is RemoteException { Code: "client_too_old" } r)
        {
            AnsiConsole.MarkupLine($"[red]This build is too old for that server.[/] [dim]{Markup.Escape(r.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Reading still works; only writes are refused.[/]");
            return 2;
        }

        AnsiConsole.MarkupLine($"[red]{Markup.Escape(e.Message)}[/]");
        return 1;
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

    private static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{n / 1024.0 / 1024:F1} MB",
        _ => $"{n / 1024.0 / 1024 / 1024:F2} GB",
    };
}
