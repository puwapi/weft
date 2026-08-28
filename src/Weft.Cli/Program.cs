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

        var app = new RootCommand("weft: keep a monorepo of many git repositories in step across machines.")
        {
            init, scan, snapshot,
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
