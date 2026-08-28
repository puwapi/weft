using Weft.Core.Ignore;

namespace Weft.Core.Git;

/// <summary>What one pass over the tree found.</summary>
public sealed record TreeWalkResult
{
    /// <summary>Directories holding a '.git' entry, relative to the root, '/'-separated.</summary>
    public required IReadOnlyList<string> CheckoutRoots { get; init; }

    /// <summary>Files outside every checkout that the policy admits.</summary>
    public required IReadOnlyList<string> LooseFiles { get; init; }

    /// <summary>Paths the confidentiality set refused. Reported, never snapshotted.</summary>
    public required IReadOnlyList<string> Refused { get; init; }

    /// <summary>Directories pruned without being entered. Diagnostic only.</summary>
    public required int PrunedDirectories { get; init; }

    /// <summary>Entries actually looked at. Compare against the tree's true size.</summary>
    public required int VisitedEntries { get; init; }
}

/// <summary>
/// One pass over the tree that classifies everything it meets.
/// </summary>
/// <remarks>
/// <para>A single walk yields both categories on purpose. Walking twice, once for
/// repositories and once for loose files, would double the syscall cost of the
/// most expensive operation weft performs.</para>
///
/// <para>The walk stops at any directory holding a '.git' entry and does not
/// descend: everything below belongs to git, which weft asks directly. That is
/// what turns 1.8 million filesystem entries into a few thousand.</para>
/// </remarks>
public sealed class TreeWalk
{
    private readonly string _root;
    private readonly IgnorePolicy _policy;

    public TreeWalk(string root, IgnorePolicy policy)
    {
        _root = Path.GetFullPath(root);
        _policy = policy;
    }

    public TreeWalkResult Run(CancellationToken ct = default)
    {
        var checkouts = new List<string>();
        var loose = new List<string>();
        var refused = new List<string>();
        var pruned = 0;
        var visited = 0;

        // Explicit stack rather than recursion: a symlink cycle or a pathological
        // tree must not take the process down with a stack overflow, which is
        // uncatchable in .NET.
        var stack = new Stack<string>();
        stack.Push("");

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var rel = stack.Pop();
            var abs = rel.Length == 0 ? _root : Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(abs).EnumerateFileSystemInfos();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // A directory that vanished or is unreadable is skipped, not
                // fatal. Scans run against a live tree; a build deleting its own
                // output mid-walk is normal, not an error.
                continue;
            }

            var subdirs = new List<string>();
            var files = new List<string>();
            var isCheckout = false;

            foreach (var e in entries)
            {
                visited++;

                // '.git' is a directory in a repository and a FILE in a linked
                // worktree. Testing only for the directory misses every worktree,
                // and their contents would then be snapshotted as loose files.
                if (e.Name == ".git") { isCheckout = true; continue; }

                var child = rel.Length == 0 ? e.Name : $"{rel}/{e.Name}";

                // A symlinked directory is recorded as a file entry and never
                // followed: following them turns a cycle into an infinite walk
                // and duplicates content that already lives elsewhere.
                var isDir = e is DirectoryInfo && !e.Attributes.HasFlag(FileAttributes.ReparsePoint);

                if (isDir) subdirs.Add(child); else files.Add(child);
            }

            if (isCheckout)
            {
                // Below this point is git's business. Do not descend.
                checkouts.Add(rel.Length == 0 ? "." : rel);
                continue;
            }

            foreach (var d in subdirs)
            {
                switch (_policy.Match(d, isDirectory: true))
                {
                    case IgnoreVerdict.Include: stack.Push(d); break;
                    case IgnoreVerdict.Never: refused.Add(d + "/"); pruned++; break;
                    default: pruned++; break;
                }
            }

            foreach (var f in files)
            {
                switch (_policy.Match(f, isDirectory: false))
                {
                    case IgnoreVerdict.Include: loose.Add(f); break;
                    case IgnoreVerdict.Never: refused.Add(f); break;
                }
            }
        }

        loose.Sort(StringComparer.Ordinal);
        checkouts.Sort(StringComparer.Ordinal);
        refused.Sort(StringComparer.Ordinal);

        return new TreeWalkResult
        {
            CheckoutRoots = checkouts,
            LooseFiles = loose,
            Refused = refused,
            PrunedDirectories = pruned,
            VisitedEntries = visited,
        };
    }
}
