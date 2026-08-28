using System.CommandLine;
using Spectre.Console;
using Weft.Cli.Commands;

namespace Weft.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var root = new Option<string?>("--root", "-C")
        {
            Description = "Workspace directory. Defaults to the nearest ancestor holding a .weft directory.",
        };
        var verbose = new Option<bool>("--verbose", "-v")
        {
            Description = "List every repository and every refusal.",
        };

        var scan = new Command("scan", "Report what weft sees, without changing anything.")
        {
            root, verbose,
        };
        scan.SetAction((pr, ct) => ScanCommand.RunAsync(pr.GetValue(root), pr.GetValue(verbose), ct));

        var name = new Option<string?>("--name")
        {
            Description = "Name for this machine. Defaults to the hostname. The identity is a separate id you never see change.",
        };
        var force = new Option<bool>("--force")
        {
            Description = "Rewrite the rule files. Never touches the machine identity.",
        };
        var initRoot = new Option<string?>("--root", "-C") { Description = "Directory to turn into a workspace. Defaults to the current one." };

        var init = new Command("init", "Create a workspace here, importing an existing .stignore if there is one.")
        {
            initRoot, name, force,
        };
        init.SetAction((pr, ct) => InitCommand.RunAsync(pr.GetValue(initRoot), pr.GetValue(name), pr.GetValue(force), ct));

        var snapRoot = new Option<string?>("--root", "-C") { Description = "Workspace directory." };
        var snapVerbose = new Option<bool>("--verbose", "-v") { Description = "List every path that changed." };

        var snapshot = new Command("snapshot", "Record the state of the workspace. Never writes into a working tree.")
        {
            snapRoot, snapVerbose,
        };
        snapshot.SetAction((pr, ct) => SnapshotCommand.RunAsync(pr.GetValue(snapRoot), pr.GetValue(snapVerbose), ct));

        // ---- key ----
        var keyRoot = new Option<string?>("--root", "-C") { Description = "Workspace directory." };
        var reveal = new Option<bool>("--reveal") { Description = "Print the key itself, not just its fingerprint." };
        var keyShow = new Command("show", "Show which workspace key this machine holds.") { keyRoot, reveal };
        keyShow.SetAction((pr, _) => RemoteCommands.KeyShowAsync(pr.GetValue(keyRoot), pr.GetValue(reveal)));

        var keyValue = new Argument<string>("key") { Description = "The key from a machine already on this workspace." };
        var keyForce = new Option<bool>("--force") { Description = "Replace a different key. Makes everything stored under the old one unreadable." };
        var keySet = new Command("set", "Put this machine on an existing workspace.") { keyValue, keyRoot, keyForce };
        keySet.SetAction((pr, _) => RemoteCommands.KeySetAsync(pr.GetValue(keyRoot), pr.GetValue(keyValue)!, pr.GetValue(keyForce)));

        var key = new Command("key", "The secret that keeps the server from reading your files.") { keyShow, keySet };

        // ---- remote ----
        var remoteUrl = new Argument<string>("url") { Description = "Base URL of the weft server." };
        var join = new Option<string>("--join") { Description = "Join secret the server was configured with.", Required = true };
        var remoteRoot = new Option<string?>("--root", "-C") { Description = "Workspace directory." };
        var remoteAdd = new Command("add", "Enrol this machine with a server.") { remoteUrl, join, remoteRoot };
        remoteAdd.SetAction((pr, ct) => RemoteCommands.RemoteAddAsync(
            pr.GetValue(remoteRoot), pr.GetValue(remoteUrl)!, pr.GetValue(join)!, ct));

        var remote = new Command("remote", "The server this workspace syncs with.") { remoteAdd };

        // ---- push and pull ----
        var pushRoot = new Option<string?>("--root", "-C") { Description = "Workspace directory." };
        var push = new Command("push", "Send this machine's latest snapshot to the server.") { pushRoot };
        push.SetAction((pr, ct) => RemoteCommands.PushAsync(pr.GetValue(pushRoot), ct));

        var pullRoot = new Option<string?>("--root", "-C") { Description = "Workspace directory." };
        var pull = new Command("pull", "Fetch what other machines have recorded. Writes nothing into your working tree.") { pullRoot };
        pull.SetAction((pr, ct) => RemoteCommands.PullAsync(pr.GetValue(pullRoot), ct));

        var app = new RootCommand("weft: keep a monorepo of many git repositories in step across machines.")
        {
            init, scan, snapshot, key, remote, push, pull,
        };

        try
        {
            return await app.Parse(args).InvokeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Interrupted.[/]");
            return 130;
        }
    }
}
