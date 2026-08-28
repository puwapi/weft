using System.Collections.Concurrent;
using Weft.Core.Store;

namespace Weft.Core.Git;

/// <summary>Reads where each checkout stands right now.</summary>
public sealed class RepoStateReader(GitRunner git)
{
    /// <summary>
    /// Reads branch, HEAD, remote and dirty count for every checkout inside the
    /// workspace.
    /// </summary>
    /// <remarks>
    /// Checkouts outside the workspace are skipped: weft records what it can act
    /// on, and a state it can never reconcile would be noise in every snapshot.
    /// A checkout git refuses to answer about is skipped too, never recorded with
    /// blank fields, which would be indistinguishable from a repository with no
    /// remote and no branch.
    /// </remarks>
    public async Task<IReadOnlyList<RepoState>> ReadAsync(
        IReadOnlyList<Repository> repos, int maxParallel = 8, CancellationToken ct = default)
    {
        var checkouts = repos.SelectMany(r => r.Checkouts).Where(c => c.IsInsideRoot).ToList();
        var states = new ConcurrentBag<RepoState>();

        await Parallel.ForEachAsync(
            checkouts,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (c, token) =>
            {
                var s = await ReadOneAsync(c, token).ConfigureAwait(false);
                if (s is not null) states.Add(s);
            }).ConfigureAwait(false);

        return states.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
    }

    private async Task<RepoState?> ReadOneAsync(Checkout c, CancellationToken ct)
    {
        var dir = c.AbsolutePath;

        // '--porcelain=v2 --branch' answers branch, HEAD and working-tree state in
        // one invocation. Asking separately would triple the process count, and
        // process startup is the entire cost of talking to git.
        var status = await git.RunAsync(dir, ct,
            "status", "--porcelain=v2", "--branch", "--untracked-files=all").ConfigureAwait(false);
        if (!status.Ok) return null;

        var branch = "";
        var head = "";
        var dirty = 0;

        foreach (var line in status.Lines)
        {
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = line["# branch.head ".Length..].Trim();
                // git writes '(detached)' here, which is a state and not a name.
                if (branch == "(detached)") branch = "";
            }
            else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                head = line["# branch.oid ".Length..].Trim();
                if (head == "(initial)") head = "";
            }
            else if (line.Length > 0 && line[0] != '#')
            {
                dirty++;
            }
        }

        var remote = await git.RunAsync(dir, ct, "remote", "get-url", "origin").ConfigureAwait(false);

        return new RepoState(
            Path: c.RelativePath!,
            Remote: remote.Ok ? remote.StdOut.Trim() : "",
            Branch: branch,
            Head: head,
            IsPrimary: c.IsPrimary,
            DirtyFiles: dirty);
    }
}
