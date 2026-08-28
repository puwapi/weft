using Weft.Core.Ui;

namespace Weft.Core.Tests.Ui;

/// <summary>
/// The rules a terminal interface gets wrong, checked without a terminal.
/// </summary>
/// <remarks>
/// A selection past the end of a list, a scroll that keeps going on a short file,
/// a destructive key still live on a screen where it means nothing. None of these
/// can be caught by looking at the screen once; all of them are one assertion here.
/// </remarks>
public class UiUpdateTests
{
    private const int Rows = 10;

    private static ConflictRow Conflict(string path, int lines = 3) => new(
        path, "both changed it",
        Enumerable.Range(0, lines).Select(i => $"ours {i}").ToArray(),
        Enumerable.Range(0, lines).Select(i => $"theirs {i}").ToArray());

    private static UiData Data(int conflicts = 0, int attention = 0) => new()
    {
        WorkspacePath = "/w",
        MachineName = "mac-studio",
        Head = "abc123",
        Pushed = true,
        Machines = [new MachineRow("mac-studio", "abc123", DateTimeOffset.UnixEpoch, true)],
        Attention = Enumerable.Range(0, attention)
            .Select(i => new AttentionRow(AttentionKind.NotPushed, $"s{i}", "d")).ToArray(),
        Conflicts = Enumerable.Range(0, conflicts).Select(i => Conflict($"file{i}.txt")).ToArray(),
    };

    private static UiState Step(UiState s, params UiKey[] keys)
    {
        foreach (var k in keys) s = UiUpdate.Handle(s, k, Rows).State;
        return s;
    }

    // ---------- opening ----------

    [Fact]
    public void With_nothing_pending_it_opens_on_the_overview()
        => Assert.Equal(UiScreen.Overview, UiState.From(Data()).Screen);

    [Fact]
    public void With_conflicts_pending_it_opens_on_them()
    {
        // Conflicts are the only thing here that blocks work. A screen that makes
        // the user navigate to the problem buries it.
        Assert.Equal(UiScreen.Conflicts, UiState.From(Data(conflicts: 2)).Screen);
    }

    // ---------- navigation ----------

    [Fact]
    public void The_selection_never_runs_past_either_end()
    {
        var s = UiState.From(Data(conflicts: 3));

        Assert.Equal(0, Step(s, UiKey.Up, UiKey.Up, UiKey.Up).Selected);
        Assert.Equal(2, Step(s, UiKey.Down, UiKey.Down, UiKey.Down, UiKey.Down, UiKey.Down).Selected);
    }

    [Fact]
    public void Paging_is_clamped_the_same_way()
    {
        var s = UiState.From(Data(conflicts: 3));
        Assert.Equal(2, Step(s, UiKey.PageDown).Selected);
        Assert.Equal(0, Step(s, UiKey.PageDown, UiKey.PageUp).Selected);
    }

    [Fact]
    public void Home_and_End_go_to_the_ends()
    {
        var s = UiState.From(Data(conflicts: 5));
        Assert.Equal(4, Step(s, UiKey.End).Selected);
        Assert.Equal(0, Step(s, UiKey.End, UiKey.Home).Selected);
    }

    [Fact]
    public void Enter_opens_the_diff_and_Back_returns()
    {
        var s = UiState.From(Data(conflicts: 2));
        Assert.Equal(UiScreen.Diff, Step(s, UiKey.Enter).Screen);
        Assert.Equal(UiScreen.Conflicts, Step(s, UiKey.Enter, UiKey.Back).Screen);
    }

    [Fact]
    public void The_conflicts_screen_is_unreachable_when_there_are_none()
    {
        // Offering an empty screen teaches people the key does nothing.
        var s = UiState.From(Data());
        Assert.Equal(UiScreen.Overview, Step(s, UiKey.NextScreen).Screen);
        Assert.Equal(UiScreen.Overview, Step(s, UiKey.Enter).Screen);
    }

    // ---------- scrolling ----------

    [Fact]
    public void Scrolling_stops_at_the_last_row_rather_than_running_into_empty_space()
    {
        var s = Step(UiState.From(Data(conflicts: 1)), UiKey.Enter);

        // Three identical-length sides align to three rows.
        var far = Step(s, Enumerable.Repeat(UiKey.Down, 50).ToArray());
        Assert.Equal(2, far.Scroll);
    }

    [Fact]
    public void Scrolling_never_goes_above_the_first_row()
        => Assert.Equal(0, Step(UiState.From(Data(conflicts: 1)),
            UiKey.Enter, UiKey.Up, UiKey.Up, UiKey.Up).Scroll);

    [Fact]
    public void Opening_a_different_file_starts_at_the_top()
    {
        var s = UiState.From(Data(conflicts: 2));
        var scrolled = Step(s, UiKey.Enter, UiKey.Down, UiKey.Down);
        Assert.True(scrolled.Scroll > 0);

        Assert.Equal(0, Step(scrolled, UiKey.Back, UiKey.Down, UiKey.Enter).Scroll);
    }

    // ---------- resolution ----------

    [Fact]
    public void Taking_ours_asks_the_loop_to_do_it_and_names_the_file()
    {
        var s = UiState.From(Data(conflicts: 2));
        var step = UiUpdate.Handle(s with { Selected = 1 }, UiKey.TakeOurs, Rows);

        var cmd = Assert.IsType<UiCommand.TakeOurs>(step.Command);
        Assert.Equal("file1.txt", cmd.Path);
    }

    [Fact]
    public void Taking_theirs_does_the_same()
    {
        var step = UiUpdate.Handle(UiState.From(Data(conflicts: 1)), UiKey.TakeTheirs, Rows);
        Assert.Equal("file0.txt", Assert.IsType<UiCommand.TakeTheirs>(step.Command).Path);
    }

    [Fact]
    public void Resolving_from_inside_the_diff_returns_to_the_list()
    {
        var s = Step(UiState.From(Data(conflicts: 2)), UiKey.Enter);
        var step = UiUpdate.Handle(s, UiKey.TakeOurs, Rows);

        Assert.Equal(UiScreen.Conflicts, step.State.Screen);
        Assert.NotNull(step.Command);
    }

    [Fact]
    public void Resolving_does_NOT_shorten_the_list_by_itself()
    {
        // The file has not been written yet. Removing the row before the write
        // succeeded would tell the user something that is not true.
        var s = UiState.From(Data(conflicts: 2));
        var step = UiUpdate.Handle(s, UiKey.TakeOurs, Rows);

        Assert.Equal(2, step.State.Data.Conflicts.Count);
    }

    [Fact]
    public void A_resolution_key_on_the_overview_does_nothing()
    {
        // The most dangerous key in the interface, live only where it has a
        // subject. A stray press on the overview must not reach a file.
        var s = UiState.From(Data());
        var step = UiUpdate.Handle(s, UiKey.TakeTheirs, Rows);

        Assert.Null(step.Command);
        Assert.Equal(UiScreen.Overview, step.State.Screen);
    }

    // ---------- help ----------

    [Fact]
    public void Any_key_dismisses_help_and_does_nothing_else()
    {
        // Otherwise a keystroke could both close the help and resolve a conflict,
        // which is a file written by someone who was reading the key list.
        var s = Step(UiState.From(Data(conflicts: 2)), UiKey.Help);
        Assert.True(s.ShowHelp);

        var step = UiUpdate.Handle(s, UiKey.TakeTheirs, Rows);
        Assert.False(step.State.ShowHelp);
        Assert.Null(step.Command);
    }

    // ---------- reload ----------

    [Fact]
    public void Reload_is_asked_for_from_every_screen()
    {
        foreach (var screen in new[] { UiScreen.Overview, UiScreen.Conflicts, UiScreen.Diff })
        {
            var s = UiState.From(Data(conflicts: 1)) with { Screen = screen };
            Assert.IsType<UiCommand.Reload>(UiUpdate.Handle(s, UiKey.Reload, Rows).Command);
        }
    }

    [Fact]
    public void Quitting_works_from_every_screen()
    {
        foreach (var screen in new[] { UiScreen.Overview, UiScreen.Conflicts, UiScreen.Diff })
        {
            var s = UiState.From(Data(conflicts: 1)) with { Screen = screen };
            Assert.True(UiUpdate.Handle(s, UiKey.Quit, Rows).State.Quit);
        }
    }

    // ---------- rebinding after the world changed ----------

    [Fact]
    public void Resolving_the_last_conflict_lands_back_on_the_overview()
    {
        // The list shrinks under a selection that was valid a moment ago.
        var before = UiState.From(Data(conflicts: 1));
        var after = UiUpdate.Rebind(before, Data(), "kept ours");

        Assert.Equal(UiScreen.Overview, after.Screen);
        Assert.Equal(0, after.Selected);
        Assert.Equal("kept ours", after.Status);
    }

    [Fact]
    public void A_selection_past_the_new_end_is_pulled_back_in()
    {
        var before = UiState.From(Data(conflicts: 5)) with { Selected = 4 };
        Assert.Equal(1, UiUpdate.Rebind(before, Data(conflicts: 2)).Selected);
    }

    [Fact]
    public void Reloading_with_conflicts_still_pending_stays_where_it_was()
    {
        var before = UiState.From(Data(conflicts: 3)) with { Selected = 1 };
        var after = UiUpdate.Rebind(before, Data(conflicts: 3));

        Assert.Equal(UiScreen.Conflicts, after.Screen);
        Assert.Equal(1, after.Selected);
    }

    // ---------- properties ----------

    [Fact]
    public void No_sequence_of_keys_can_leave_the_state_out_of_bounds()
    {
        // A terminal interface is a state machine driven by a person mashing keys.
        // Checking every rule by hand misses the sequence nobody thought of.
        var rng = new Random(20260828);
        var keys = Enum.GetValues<UiKey>();

        for (var trial = 0; trial < 3000; trial++)
        {
            var s = UiState.From(Data(conflicts: rng.Next(0, 4), attention: rng.Next(0, 3)));

            for (var i = 0; i < 40 && !s.Quit; i++)
            {
                s = UiUpdate.Handle(s, keys[rng.Next(keys.Length)], rng.Next(1, 30)).State;

                Assert.InRange(s.Selected, 0, Math.Max(0, s.Data.Conflicts.Count - 1));
                Assert.True(s.Scroll >= 0);

                // Never on a screen with nothing to show.
                if (s.Data.Conflicts.Count == 0)
                    Assert.NotEqual(UiScreen.Diff, s.Screen);
            }
        }
    }
}
