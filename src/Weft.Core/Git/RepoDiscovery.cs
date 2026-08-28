using System.Collections.Concurrent;

namespace Weft.Core.Git;

/// <summary>Resolves the checkout roots found by <see cref="TreeWalk"/> into repositories.</summary>
public sealed class RepoDiscovery
{
    private readonly string _root;
    private readonly GitRunner _git;

    public RepoDiscovery(string root, GitRunner git)
    {
        _root = Path.GetFullPath(root);
        _git = git;
    }

    /// <summary>
    /// Turns checkout paths into repositories, grouping every linked worktree
    /// under the repository it views, and pulling in worktrees that live outside
    /// the weft root.
    /// </summary>
    /// <param name="checkoutRoots">Paths relative to the root, as returned by the walk.</param>
    /// <param name="maxParallel">
    /// Measured on the reference tree: 49 repositories resolve in about 100 ms at
    /// 8 fronts, and 16 buys nothing. The work is process startup, not CPU.
    /// </param>
    public async Task<IReadOnlyList<Repository>> ResolveAsync(
        IReadOnlyList<string> checkoutRoots,
        int maxParallel = 8,
        CancellationToken ct = default)
    {
        var found = new ConcurrentBag<Checkout>();

        await Parallel.ForEachAsync(
            checkoutRoots,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (rel, token) =>
            {
                var abs = rel == "." ? _root : Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
                var c = await ResolveOneAsync(abs, token).ConfigureAwait(false);
                if (c is not null) found.Add(c);
            }).ConfigureAwait(false);

        // Group by the shared git directory: that, and not the path, is what
        // identifies a repository. Two paths with the same common dir are two
        // views of one repository.
        var byCommonDir = found.GroupBy(c => c.CommonDir, StringComparer.Ordinal)
                               .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Ask each repository for its full worktree list. This is the only way to
        // learn about trees outside the weft root, which the walk cannot see and
        // weft cannot sync, but whose existence the user needs to know about.
        await Parallel.ForEachAsync(
            byCommonDir.Keys.ToList(),
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (commonDir, token) =>
            {
                var anchor = byCommonDir[commonDir][0].AbsolutePath;
                var r = await _git.RunAsync(anchor, token, "worktree", "list", "--porcelain").ConfigureAwait(false);
                if (!r.Ok) return;

                var known = byCommonDir[commonDir]
                    .Select(c => c.AbsolutePath)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var line in r.Lines)
                {
                    if (!line.StartsWith("worktree ", StringComparison.Ordinal)) continue;

                    var path = Path.GetFullPath(line["worktree ".Length..].Trim());
                    lock (known)
                    {
                        if (!known.Add(path)) continue;
                    }

                    lock (byCommonDir)
                    {
                        byCommonDir[commonDir].Add(new Checkout
                        {
                            RelativePath = ToRelative(path),
                            AbsolutePath = path,
                            CommonDir = commonDir,
                            IsPrimary = false,
                        });
                    }
                }
            }).ConfigureAwait(false);

        return byCommonDir
            .Select(kv => new Repository
            {
                CommonDir = kv.Key,
                Primary = kv.Value.FirstOrDefault(c => c.IsPrimary),
                Checkouts = kv.Value.OrderByDescending(c => c.IsPrimary)
                                    .ThenBy(c => c.Display, StringComparer.Ordinal)
                                    .ToList(),
            })
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<Checkout?> ResolveOneAsync(string absolutePath, CancellationToken ct)
    {
        // One invocation for both values: process startup dominates the cost, so
        // asking twice would double the price of discovery.
        var r = await _git.RunAsync(absolutePath, ct,
            "rev-parse", "--absolute-git-dir", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);

        if (!r.Ok) return null;

        var lines = r.Lines;
        if (lines.Length < 2) return null;

        var gitDir = Path.GetFullPath(lines[0]);
        var commonDir = Path.GetFullPath(lines[1]);

        return new Checkout
        {
            RelativePath = ToRelative(absolutePath),
            AbsolutePath = absolutePath,
            CommonDir = commonDir,

            // A linked worktree's git dir is '<common>/worktrees/<name>'; the
            // primary's git dir IS the common dir. Comparing the two is what
            // separates a repository from a view of it.
            IsPrimary = string.Equals(gitDir, commonDir, StringComparison.Ordinal),
        };
    }

    /// <summary>Path relative to the weft root, or null when it lies outside.</summary>
    private string? ToRelative(string absolute)
    {
        var rel = Path.GetRelativePath(_root, absolute);
        if (rel == ".") return ".";
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
