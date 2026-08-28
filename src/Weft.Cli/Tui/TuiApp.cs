using Spectre.Console;
using Weft.Core.Store;
using Weft.Core.Ui;
using Weft.Core.Workspace;

namespace Weft.Cli.Tui;

/// <summary>
/// The loop: read a key, hand it to the state machine, draw, carry out whatever
/// it asked for.
/// </summary>
/// <remarks>
/// Kept as thin as it can be. Everything worth checking lives in
/// <see cref="UiUpdate"/> and <see cref="TuiView"/>, which need no terminal;
/// what is left here is the part that cannot be unit tested, so there should be
/// as little of it as possible.
/// </remarks>
internal static class TuiApp
{
    public static async Task<int> RunAsync(string? rootOverride, CancellationToken ct)
    {
        var root = WeftRoot.Discover(rootOverride ?? Directory.GetCurrentDirectory());
        if (!root.IsInitialised)
        {
            AnsiConsole.MarkupLine("[red]No workspace here.[/] [dim]Run [bold]weft init[/] first.[/]");
            return 1;
        }

        // A full-screen interface needs a terminal to be full-screen in. Refused
        // with a reason rather than drawing escape codes into a pipe.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.MarkupLine("[red]weft tui needs a terminal.[/] "
                + "[dim]Input or output is redirected. The other commands all work in a pipe.[/]");
            return 2;
        }

        var store = new ObjectStore(root.StorePath);
        var state = UiState.From(await TuiData.LoadAsync(root, ct).ConfigureAwait(false));

        AnsiConsole.Clear();
        AnsiConsole.Cursor.Hide();

        try
        {
            while (!state.Quit)
            {
                var (width, height) = TerminalSize();

                AnsiConsole.Clear();
                AnsiConsole.Write(TuiView.Render(state, width, height));

                var key = Read(ct);
                if (key is null) break;   // cancelled

                var step = UiUpdate.Handle(state, key.Value, Math.Max(3, height - 10));
                state = step.State;

                if (step.Command is null) continue;

                switch (step.Command)
                {
                    case UiCommand.Reload:
                        state = UiUpdate.Rebind(state, await TuiData.LoadAsync(root, ct).ConfigureAwait(false), "reloaded");
                        break;

                    default:
                        // The list is reloaded after the decision, not before, so
                        // a row only disappears once the file has really changed.
                        var note = TuiData.Resolve(root, store, step.Command);
                        state = UiUpdate.Rebind(state, await TuiData.LoadAsync(root, ct).ConfigureAwait(false), note);
                        break;
                }
            }
        }
        finally
        {
            AnsiConsole.Cursor.Show();
            AnsiConsole.Clear();
        }

        return 0;
    }

    /// <summary>
    /// How big the terminal is, or a workable guess.
    /// </summary>
    /// <remarks>
    /// Console.WindowWidth THROWS when the terminal does not report a size, which
    /// happens on a pty opened without one, over some ssh sessions, and inside CI
    /// runners. Reading it unguarded means the interface crashes on exactly the
    /// terminals where a person is least able to work out why. COLUMNS and LINES
    /// are consulted next, because that is what a shell sets when the ioctl is
    /// unavailable, and a plain default is the last resort.
    /// </remarks>
    private static (int Width, int Height) TerminalSize()
    {
        var width = 0;
        var height = 0;

        try { width = Console.WindowWidth; height = Console.WindowHeight; }
        catch (IOException) { }
        catch (PlatformNotSupportedException) { }

        if (width <= 0) int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out width);
        if (height <= 0) int.TryParse(Environment.GetEnvironmentVariable("LINES"), out height);

        return (Math.Max(60, width == 0 ? 100 : width),
                Math.Max(12, height == 0 ? 30 : height));
    }

    /// <summary>Blocks for a key, or returns null when the run is cancelled.</summary>
    private static UiKey? Read(CancellationToken ct)
    {
        // Polled rather than blocking outright, so Ctrl+C is honoured instead of
        // leaving the terminal without a cursor until a key happens to be pressed.
        while (!ct.IsCancellationRequested)
        {
            if (!Console.KeyAvailable) { Thread.Sleep(20); continue; }
            return Map(Console.ReadKey(intercept: true));
        }

        return null;
    }

    private static UiKey Map(ConsoleKeyInfo k) => k.Key switch
    {
        ConsoleKey.UpArrow or ConsoleKey.K => UiKey.Up,
        ConsoleKey.DownArrow or ConsoleKey.J => UiKey.Down,
        ConsoleKey.PageUp => UiKey.PageUp,
        ConsoleKey.PageDown or ConsoleKey.Spacebar => UiKey.PageDown,
        ConsoleKey.Home or ConsoleKey.G => UiKey.Home,
        ConsoleKey.End => UiKey.End,
        ConsoleKey.Enter or ConsoleKey.RightArrow => UiKey.Enter,
        ConsoleKey.Escape or ConsoleKey.Backspace or ConsoleKey.LeftArrow => UiKey.Back,
        ConsoleKey.Tab => UiKey.NextScreen,
        ConsoleKey.Q => UiKey.Quit,
        ConsoleKey.R => UiKey.Reload,
        ConsoleKey.O => UiKey.TakeOurs,
        ConsoleKey.T => UiKey.TakeTheirs,
        _ => k.KeyChar switch
        {
            '?' or 'h' => UiKey.Help,
            _ => UiKey.None,
        },
    };
}
