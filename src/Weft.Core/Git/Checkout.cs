namespace Weft.Core.Git;

/// <summary>
/// One working tree backed by git: either a repository's own tree, or one of its
/// linked worktrees.
/// </summary>
/// <remarks>
/// The distinction is not cosmetic. A linked worktree shares its object database
/// and its refs with the primary repository, so it is a <em>view</em> of that
/// repository and must never be treated as an independent one. Its '.git' is a
/// file holding a 'gitdir:' pointer rather than a directory, which is why a naive
/// search for '.git' directories misses it entirely.
/// </remarks>
public sealed record Checkout
{
    /// <summary>Path relative to the weft root, '/'-separated. Null when outside the root.</summary>
    public required string? RelativePath { get; init; }

    public required string AbsolutePath { get; init; }

    /// <summary>Absolute path of the shared git directory. Identifies the repository.</summary>
    public required string CommonDir { get; init; }

    /// <summary>True for the repository's own tree, false for a linked worktree.</summary>
    public required bool IsPrimary { get; init; }

    /// <summary>False when the tree lives outside the weft root: weft cannot sync it.</summary>
    public bool IsInsideRoot => RelativePath is not null;

    /// <summary>
    /// True when the tree sits under a directory the OS may reclaim.
    /// </summary>
    /// <remarks>
    /// Worth surfacing loudly rather than merely recording. On the reference
    /// machine six worktrees of one repository live under the macOS temp
    /// directory, each on its own feature branch. That is the "work that exists
    /// in one place and nothing knows it" failure, waiting for a reboot.
    /// </remarks>
    public bool IsVolatile
    {
        get
        {
            var p = AbsolutePath;
            return p.StartsWith("/tmp/", StringComparison.Ordinal)
                || p.StartsWith("/var/folders/", StringComparison.Ordinal)
                || p.StartsWith("/private/var/folders/", StringComparison.Ordinal)
                || p.StartsWith("/private/tmp/", StringComparison.Ordinal)
                || p.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Display name: the relative path inside the root, otherwise the absolute one.</summary>
    public string Display => RelativePath ?? AbsolutePath;
}

/// <summary>A repository and every working tree that views it.</summary>
public sealed record Repository
{
    /// <summary>Absolute path of the shared git directory. The repository's identity.</summary>
    public required string CommonDir { get; init; }

    /// <summary>The primary tree, when it is known. A bare or unreachable repo has none.</summary>
    public required Checkout? Primary { get; init; }

    /// <summary>Every tree viewing this repository, primary included.</summary>
    public required IReadOnlyList<Checkout> Checkouts { get; init; }

    /// <summary>Name used in output: the primary's relative path, else the git dir's parent.</summary>
    public string Name =>
        Primary?.RelativePath
        ?? Checkouts.FirstOrDefault(c => c.IsInsideRoot)?.RelativePath
        ?? Path.GetFileName(Path.GetDirectoryName(CommonDir.TrimEnd('/')) ?? CommonDir);

    /// <summary>Trees that weft cannot reach because they live outside the root.</summary>
    public IEnumerable<Checkout> External => Checkouts.Where(c => !c.IsInsideRoot);

    /// <summary>Trees sitting somewhere the OS may reclaim.</summary>
    public IEnumerable<Checkout> Volatile => Checkouts.Where(c => c.IsVolatile);
}
