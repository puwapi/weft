using Spectre.Console;
using Weft.Core.Crypto;
using Weft.Core.Ignore;
using Weft.Core.Workspace;

namespace Weft.Cli.Commands;

/// <summary>Creates a workspace: rule files, metadata directory, machine identity.</summary>
internal static class InitCommand
{
    public static Task<int> RunAsync(string? rootOverride, string? machineName, bool force, CancellationToken ct)
    {
        _ = ct;
        var root = Path.GetFullPath(rootOverride ?? Directory.GetCurrentDirectory());
        var meta = Path.Combine(root, WeftRoot.MetaDir);

        if (Directory.Exists(meta) && !force)
        {
            AnsiConsole.MarkupLine($"[yellow]Already a workspace:[/] [blue]{Markup.Escape(root)}[/]");
            AnsiConsole.MarkupLine("[dim]Pass --force to rewrite the rule files. Your machine identity is untouched either way.[/]");
            return Task.FromResult(1);
        }

        // The identity is loaded, not minted, when one already exists. Minting a
        // second id for a machine the remote already knows would orphan
        // everything it had pushed.
        var machine = MachineStore.LoadOrMint(machineName);
        var reusedIdentity = machineName is null || machine.Name == machineName;

        Directory.CreateDirectory(meta);

        // The key is generated once per workspace and reused if one is already
        // here. Regenerating it would make every object already on the server
        // undecryptable, and --force is about rule files, not about that.
        var existingKey = LocalSecrets.TryLoadKey(new WeftRoot { Path = root, Policy = null!, IsInitialised = true });
        var key = existingKey ?? WorkspaceKey.Generate();
        if (existingKey is null)
            LocalSecrets.SaveKey(new WeftRoot { Path = root, Policy = null!, IsInitialised = true }, key);

        var (ignoreText, neverText, import) = BuildRules(root);

        WriteIfAbsent(Path.Combine(root, WeftRoot.IgnoreFile), ignoreText, force);
        WriteIfAbsent(Path.Combine(root, WeftRoot.NeverFile), neverText, force);

        Report(root, machine, reusedIdentity, import, key, existingKey is not null);
        return Task.FromResult(0);
    }

    private static (string Ignore, string Never, StIgnoreImport? Import) BuildRules(string root)
    {
        var st = Path.Combine(root, ".stignore");
        if (!File.Exists(st))
            return (DefaultRules.Ignore, DefaultRules.Never, null);

        var import = StIgnoreReader.Parse(File.ReadAllText(st));

        var ignore = DefaultRules.Ignore
            + "\n\n# ---- carried over from .stignore ----\n"
            + string.Join('\n', import.Ignore);

        var never = DefaultRules.Never
            + "\n\n# ---- carried over from .stignore, classified as confidential ----\n"
            + "# These were routed here by a keyword guess, biased towards over-classifying:\n"
            + "# a rule wrongly placed here costs one file not syncing, and you were told.\n"
            + "# The opposite mistake sends a secret to the remote and tells nobody.\n"
            + string.Join('\n', import.Never);

        if (import.Unsupported.Count > 0)
            ignore += "\n\n# ---- not carried over, no weft equivalent ----\n"
                + string.Join('\n', import.Unsupported.Select(u => "# " + u));

        return (ignore, never, import);
    }

    private static void WriteIfAbsent(string path, string content, bool force)
    {
        if (File.Exists(path) && !force) return;
        File.WriteAllText(path, content.TrimEnd() + "\n");
    }

    private static void Report(string root, MachineIdentity machine, bool reusedIdentity,
        StIgnoreImport? import, WorkspaceKey key, bool keyExisted)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Workspace ready[/] [blue]{Markup.Escape(root)}[/]");
        AnsiConsole.WriteLine();

        var t = new Table().Border(TableBorder.None).HideHeaders();
        t.AddColumn(new TableColumn("k").PadRight(3));
        t.AddColumn("v");
        t.AddRow("Machine", $"[bold]{Markup.Escape(machine.Name)}[/] [dim]{machine.Id}[/]"
            + (reusedIdentity ? "" : " [yellow](existing identity kept)[/]"));
        t.AddRow("Rules", $"[dim]{WeftRoot.IgnoreFile}, {WeftRoot.NeverFile}[/]");
        t.AddRow("Workspace key", $"[dim]{key.Fingerprint()}[/]"
            + (keyExisted ? " [dim](kept)[/]" : " [green](new)[/]"));
        AnsiConsole.Write(t);

        if (!keyExisted)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(new Rows(
                    new Markup("[bold]" + Markup.Escape(key.ToDisplayString()) + "[/]"),
                    new Markup(""),
                    new Markup("[dim]Every other machine on this workspace needs this exact key.[/]"),
                    new Markup("[dim]Content is encrypted with it before it reaches the server, so the[/]"),
                    new Markup("[dim]server cannot read your files, and cannot help you recover this.[/]"),
                    new Markup(""),
                    new Markup("[dim]On the next machine: [bold]weft key set <key>[/][/]")))
                .Header("[yellow] Workspace key: write this down [/]")
                .BorderColor(Color.Yellow));
        }

        if (import is null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Run [bold]weft scan[/] to see what weft would manage.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[blue]Imported .stignore[/] [dim]{import.Ignore.Count(l => !l.StartsWith('#'))} rules kept, "
            + $"{import.Never.Count} moved to {WeftRoot.NeverFile}[/]");

        if (import.Never.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Classified as confidential[/] [dim](guessed: move any of these back to {WeftRoot.IgnoreFile} if wrong)[/]");
            foreach (var n in import.Never)
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(n)}[/]");
        }

        if (import.Unsupported.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Not carried over[/] [dim](kept as comments, no weft equivalent)[/]");
            foreach (var u in import.Unsupported)
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(u)}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Run [bold]weft scan[/] to see what weft would manage.[/]");
    }
}
