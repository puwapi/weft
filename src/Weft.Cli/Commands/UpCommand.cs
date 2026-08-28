using Spectre.Console;
using Weft.Core.Protocol;
using Weft.Core.Release;

namespace Weft.Cli.Commands;

/// <summary>Replaces this binary with the published one.</summary>
internal static class UpCommand
{
    public static async Task<int> RunAsync(bool checkOnly, bool force, CancellationToken ct)
    {
        var target = Environment.ProcessPath;
        if (target is null)
        {
            AnsiConsole.MarkupLine("[red]Cannot tell where this binary is.[/]");
            return 1;
        }

        var asset = ReleaseTarget.Current();
        if (asset is null)
        {
            AnsiConsole.MarkupLine("[red]No binary is published for this system.[/] [dim]Build from source.[/]");
            return 1;
        }

        using var feed = new ReleaseFeed();

        PublishedRelease? latest;
        try { latest = await feed.LatestAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Cannot reach the release feed.[/] [dim]{Markup.Escape(e.Message)}[/]");
            return 1;
        }

        if (latest is null)
        {
            AnsiConsole.MarkupLine("[yellow]No published release found.[/]");
            return 1;
        }

        var decision = UpdateDecision.Between(WeftVersion.Build, latest.Tag);
        AnsiConsole.WriteLine();

        switch (decision.Verdict)
        {
            case UpdateVerdict.UpToDate when !force:
                AnsiConsole.MarkupLine($"[green]Up to date.[/] [dim]{Markup.Escape(decision.Describe())}[/]");
                return 0;

            case UpdateVerdict.Ahead when !force:
                // A local build is newer than anything published. Replacing it
                // with an older release would be a downgrade nobody asked for.
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(decision.Describe())}.[/] "
                    + "[dim]Nothing to do; pass --force to install the published one anyway.[/]");
                return 0;

            case UpdateVerdict.Unknown:
                AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(decision.Describe())}[/] "
                    + $"[dim](this build: {WeftVersion.Build}, published: {Markup.Escape(latest.Tag)})[/]");
                return 1;
        }

        if (checkOnly)
        {
            AnsiConsole.MarkupLine($"[blue]{Markup.Escape(decision.Describe())}[/]");
            if (latest.Url is not null) AnsiConsole.MarkupLine($"[dim]{Markup.Escape(latest.Url)}[/]");
            AnsiConsole.MarkupLine("[dim]Run [bold]weft up[/] to install it.[/]");
            return 0;
        }

        // Checked BEFORE downloading. Fetching ten megabytes and then discovering
        // the file cannot be written wastes the user's time and tells them nothing
        // they could not have been told first.
        if (BinarySwap.CanReplace(target) is { } obstacle)
        {
            AnsiConsole.MarkupLine($"[red]Cannot update:[/] [dim]{Markup.Escape(obstacle.Reason)}[/]");
            if (obstacle.Remedy is not null)
                AnsiConsole.MarkupLine($"[dim]{Markup.Escape(obstacle.Remedy)}[/]");
            return 1;
        }

        byte[] bytes = [];
        try
        {
            await AnsiConsole.Status().Spinner(Spinner.Known.Dots)
                .StartAsync($"Fetching {asset} {latest.Tag}...", async _ =>
                {
                    bytes = await feed.DownloadVerifiedAsync(latest.Tag, asset, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (ChecksumMismatchException e)
        {
            AnsiConsole.MarkupLine($"[red]Refusing to install:[/] [dim]{Markup.Escape(e.Message)}[/]");
            AnsiConsole.MarkupLine("[dim]Nothing was changed. Try again; if it keeps happening, download by hand.[/]");
            return 1;
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]Download failed.[/] [dim]{Markup.Escape(e.Message)}[/]");
            return 1;
        }

        // Staged beside the target, not in the temp directory: a rename across
        // filesystems is a copy, and a copy is not atomic. Next to it, the swap
        // is one call.
        var staged = target + ".weft-new";

        try
        {
            await File.WriteAllBytesAsync(staged, bytes, ct).ConfigureAwait(false);
            BinarySwap.Replace(target, staged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch (IOException) { }
            AnsiConsole.MarkupLine($"[red]Could not put the new binary in place.[/] [dim]{Markup.Escape(e.Message)}[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Updated[/] [dim]{WeftVersion.Build} -> {Markup.Escape(latest.Tag)}, "
            + $"checksum verified[/]");

        if (OperatingSystem.IsWindows())
            AnsiConsole.MarkupLine("[dim]The previous binary is removed the next time weft runs: Windows will not "
                + "delete an executable while it is running.[/]");

        return 0;
    }
}
