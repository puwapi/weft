namespace Weft.Core.Ui;

/// <summary>Which screen the user is on.</summary>
public enum UiScreen
{
    /// <summary>Machines, and anything that wants attention.</summary>
    Overview,

    /// <summary>Files a pending merge could not settle.</summary>
    Conflicts,

    /// <summary>One conflicted file, both versions side by side.</summary>
    Diff,
}

/// <summary>Why something is on the attention list, worst first.</summary>
public enum AttentionKind
{
    /// <summary>Uncommitted work that exists on this disk and nowhere else.</summary>
    WorkOnlyHere = 0,

    /// <summary>A merge is waiting on a decision.</summary>
    Conflict = 1,

    /// <summary>A checkout under a directory the OS may reclaim.</summary>
    VolatileWorktree = 2,

    /// <summary>Recorded here, not yet on the server.</summary>
    NotPushed = 3,

    /// <summary>A checkout outside the workspace, which weft cannot sync.</summary>
    ExternalWorktree = 4,
}

public sealed record MachineRow(string Name, string? Snapshot, DateTimeOffset? SeenUtc, bool IsThisOne);

public sealed record AttentionRow(AttentionKind Kind, string Subject, string Detail);

public sealed record ConflictRow(
    string Path, string Reason, IReadOnlyList<string> Ours, IReadOnlyList<string> Theirs);

/// <summary>Everything the screen draws from. Loaded once, replaced on reload.</summary>
public sealed record UiData
{
    public required string WorkspacePath { get; init; }
    public required string MachineName { get; init; }
    public required string? Head { get; init; }
    public required bool Pushed { get; init; }
    public required IReadOnlyList<MachineRow> Machines { get; init; }
    public required IReadOnlyList<AttentionRow> Attention { get; init; }
    public required IReadOnlyList<ConflictRow> Conflicts { get; init; }

    public static UiData Empty(string path, string machine) => new()
    {
        WorkspacePath = path,
        MachineName = machine,
        Head = null,
        Pushed = false,
        Machines = [],
        Attention = [],
        Conflicts = [],
    };
}

/// <summary>
/// The whole interface, as a value.
/// </summary>
/// <remarks>
/// Held apart from both the drawing and the key loop on purpose. A terminal
/// interface that mixes state, rendering and input can only be checked by
/// watching it, and watching it is exactly what could not be done for the library
/// this one was chosen over. Keeping the state machine pure means every navigation
/// rule, every clamp and every resolution is a unit test.
/// </remarks>
public sealed record UiState
{
    public required UiData Data { get; init; }

    public UiScreen Screen { get; init; } = UiScreen.Overview;

    /// <summary>Selected row on the current list screen.</summary>
    public int Selected { get; init; }

    /// <summary>First visible row on the diff screen.</summary>
    public int Scroll { get; init; }

    /// <summary>A one-line note about what just happened.</summary>
    public string? Status { get; init; }

    public bool ShowHelp { get; init; }
    public bool Quit { get; init; }

    public ConflictRow? CurrentConflict =>
        Selected >= 0 && Selected < Data.Conflicts.Count ? Data.Conflicts[Selected] : null;

    public static UiState From(UiData data) => new()
    {
        Data = data,

        // Opening straight on the conflicts when there are any: they are the only
        // thing here that blocks work, and a screen that makes the user navigate
        // to the problem buries it.
        Screen = data.Conflicts.Count > 0 ? UiScreen.Conflicts : UiScreen.Overview,
    };
}

/// <summary>Keys the interface understands, named by intent rather than by keycap.</summary>
public enum UiKey
{
    None, Up, Down, PageUp, PageDown, Home, End,
    Enter, Back, Quit, Reload, Help,
    TakeOurs, TakeTheirs, NextScreen,
}

/// <summary>Something the loop must do in the world. Returned, never performed here.</summary>
public abstract record UiCommand
{
    /// <summary>Keep this machine's version of a conflicted file.</summary>
    public sealed record TakeOurs(string Path) : UiCommand;

    /// <summary>Replace it with the other machine's version.</summary>
    public sealed record TakeTheirs(string Path) : UiCommand;

    /// <summary>Read the workspace again.</summary>
    public sealed record Reload : UiCommand;
}

/// <summary>The state after a key, and anything the loop has to carry out.</summary>
public sealed record UiStep(UiState State, UiCommand? Command = null);
