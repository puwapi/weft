using Weft.Core.Merge;

namespace Weft.Core.Ui;

/// <summary>
/// Every navigation and every decision, as a pure function of state and key.
/// </summary>
/// <remarks>
/// Nothing here reads a file, draws anything, or looks at a terminal. That is what
/// makes the rules below testable, and they are exactly the rules that go wrong in
/// a terminal interface: a selection that runs past the end of a list, a scroll
/// that keeps going on a short file, a destructive key that stays live on a screen
/// where it means nothing.
/// </remarks>
public static class UiUpdate
{
    public static UiStep Handle(UiState state, UiKey key, int viewportRows)
    {
        // Help is a modal layer: any key closes it, and nothing underneath acts on
        // that key. Letting a keystroke both dismiss help and do something would
        // make it possible to resolve a conflict while reading the key list.
        if (state.ShowHelp)
            return new UiStep(state with { ShowHelp = false, Status = null });

        return key switch
        {
            UiKey.Quit => new UiStep(state with { Quit = true }),
            UiKey.Help => new UiStep(state with { ShowHelp = true }),
            UiKey.Reload => new UiStep(state, new UiCommand.Reload()),

            _ => state.Screen switch
            {
                UiScreen.Overview => Overview(state, key),
                UiScreen.Conflicts => Conflicts(state, key, viewportRows),
                UiScreen.Diff => Diff(state, key, viewportRows),
                _ => new UiStep(state),
            },
        };
    }

    private static UiStep Overview(UiState s, UiKey key) => key switch
    {
        // Reachable only when there is something to see. Offering an empty screen
        // teaches people the key does nothing.
        UiKey.NextScreen or UiKey.Enter when s.Data.Conflicts.Count > 0
            => new UiStep(s with { Screen = UiScreen.Conflicts, Selected = 0, Status = null }),

        _ => new UiStep(s),
    };

    private static UiStep Conflicts(UiState s, UiKey key, int viewportRows)
    {
        var count = s.Data.Conflicts.Count;
        if (count == 0) return new UiStep(s with { Screen = UiScreen.Overview });

        return key switch
        {
            UiKey.Up => new UiStep(s with { Selected = Math.Max(0, s.Selected - 1), Status = null }),
            UiKey.Down => new UiStep(s with { Selected = Math.Min(count - 1, s.Selected + 1), Status = null }),
            UiKey.PageUp => new UiStep(s with { Selected = Math.Max(0, s.Selected - viewportRows) }),
            UiKey.PageDown => new UiStep(s with { Selected = Math.Min(count - 1, s.Selected + viewportRows) }),
            UiKey.Home => new UiStep(s with { Selected = 0 }),
            UiKey.End => new UiStep(s with { Selected = count - 1 }),

            UiKey.Enter => new UiStep(s with { Screen = UiScreen.Diff, Scroll = 0, Status = null }),
            UiKey.NextScreen or UiKey.Back => new UiStep(s with { Screen = UiScreen.Overview, Status = null }),

            UiKey.TakeOurs => Resolve(s, ours: true),
            UiKey.TakeTheirs => Resolve(s, ours: false),

            _ => new UiStep(s),
        };
    }

    private static UiStep Diff(UiState s, UiKey key, int viewportRows)
    {
        var conflict = s.CurrentConflict;
        if (conflict is null) return new UiStep(s with { Screen = UiScreen.Conflicts });

        var rows = SideBySide.Align(conflict.Ours, conflict.Theirs).Count;

        // The last line can reach the top of the viewport and no further. Without
        // this the view scrolls into empty space and the file appears to vanish.
        var maxScroll = Math.Max(0, rows - 1);

        return key switch
        {
            UiKey.Up => new UiStep(s with { Scroll = Math.Max(0, s.Scroll - 1) }),
            UiKey.Down => new UiStep(s with { Scroll = Math.Min(maxScroll, s.Scroll + 1) }),
            UiKey.PageUp => new UiStep(s with { Scroll = Math.Max(0, s.Scroll - viewportRows) }),
            UiKey.PageDown => new UiStep(s with { Scroll = Math.Min(maxScroll, s.Scroll + viewportRows) }),
            UiKey.Home => new UiStep(s with { Scroll = 0 }),
            UiKey.End => new UiStep(s with { Scroll = maxScroll }),

            UiKey.Back or UiKey.Enter => new UiStep(s with { Screen = UiScreen.Conflicts, Scroll = 0 }),
            UiKey.NextScreen => new UiStep(s with { Screen = UiScreen.Overview, Scroll = 0 }),

            UiKey.TakeOurs => Resolve(s, ours: true),
            UiKey.TakeTheirs => Resolve(s, ours: false),

            _ => new UiStep(s),
        };
    }

    /// <summary>
    /// Settles one conflict.
    /// </summary>
    /// <remarks>
    /// The list is not shortened here. The file has not been written yet, and a
    /// screen that removed the row before the write succeeded would tell the user
    /// something that is not true. The loop reloads once the command has run, and
    /// the row disappears then, because it really is settled.
    /// </remarks>
    private static UiStep Resolve(UiState s, bool ours)
    {
        var conflict = s.CurrentConflict;
        if (conflict is null) return new UiStep(s);

        UiCommand command = ours
            ? new UiCommand.TakeOurs(conflict.Path)
            : new UiCommand.TakeTheirs(conflict.Path);

        return new UiStep(
            s with { Screen = UiScreen.Conflicts, Scroll = 0 },
            command);
    }

    /// <summary>Keeps the selected row inside the list after the data underneath changed.</summary>
    /// <remarks>
    /// Resolving the last conflict in the list is the case this exists for: the
    /// list shrinks under a selection that was valid a moment ago.
    /// </remarks>
    public static UiState Rebind(UiState previous, UiData fresh, string? status = null)
    {
        var screen = previous.Screen;
        if (fresh.Conflicts.Count == 0 && screen is UiScreen.Conflicts or UiScreen.Diff)
            screen = UiScreen.Overview;

        return previous with
        {
            Data = fresh,
            Screen = screen,
            Selected = Math.Clamp(previous.Selected, 0, Math.Max(0, fresh.Conflicts.Count - 1)),
            Scroll = 0,
            Status = status,
        };
    }
}
