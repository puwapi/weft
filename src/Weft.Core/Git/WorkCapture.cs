using System.Text;

namespace Weft.Core.Git;

/// <summary>Everything a checkout holds that git does not.</summary>
/// <param name="RepoPath">Checkout path, relative to the workspace root.</param>
/// <param name="BaseCommit">The commit the patch applies to.</param>
/// <param name="Branch">Branch it was taken on. Empty when detached.</param>
/// <param name="Patch">A unified diff that turns the base commit into what is on disk.</param>
/// <param name="ChangedFiles">How many files the patch touches.</param>
/// <param name="StagedFiles">How many were staged. Recorded so landing can say what it did not restore.</param>
public sealed record WorkPatch(
    string RepoPath,
    string BaseCommit,
    string Branch,
    byte[] Patch,
    int ChangedFiles,
    int StagedFiles);

/// <summary>
/// Captures the uncommitted state of a checkout.
/// </summary>
/// <remarks>
/// <para>This is the gap the whole tool exists for. A branch that lives on one
/// disk, that the notes describe as shipped, and that a dead drive would erase
/// with nothing to show it existed.</para>
///
/// <para>Captured as a patch rather than as file contents, because git already
/// knows how to produce and apply one: renames, mode changes, deletions and
/// binary deltas all come free, and <c>--3way</c> can still land it when the
/// target has moved on. Reimplementing that is precisely where a reimplementation
/// drifts from git.</para>
/// </remarks>
public sealed class WorkCapture(GitRunner git)
{
    public async Task<WorkPatch?> CaptureAsync(Checkout checkout, CancellationToken ct = default)
    {
        var dir = checkout.AbsolutePath;

        var head = await git.RunAsync(dir, ct, "rev-parse", "HEAD").ConfigureAwait(false);
        if (!head.Ok) return null;   // a repository with no commit yet has no base to anchor to

        var baseCommit = head.StdOut.Trim();

        var branchResult = await git.RunAsync(dir, ct, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        var branch = branchResult.Ok ? branchResult.StdOut.Trim() : "";
        if (branch == "HEAD") branch = "";   // detached

        var staged = await git.RunAsync(dir, ct, "diff", "--cached", "--name-only").ConfigureAwait(false);
        var stagedCount = staged.Ok ? staged.Lines.Length : 0;

        // A throwaway index. 'git add -A' against it stages tracked modifications
        // AND untracked files in one pass, honouring .gitignore, while the index
        // the user is actually working with is never touched. Without this, a
        // capture would either miss every new file or quietly restage the user's
        // work, and the second is far worse than the first.
        var indexFile = Path.Combine(Path.GetTempPath(), "weft-index-" + Guid.NewGuid().ToString("n"));
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = indexFile };

        try
        {
            var read = await git.RunAsync(dir, env, ct, "read-tree", baseCommit).ConfigureAwait(false);
            if (!read.Ok) return null;

            var add = await git.RunAsync(dir, env, ct, "add", "-A", ".").ConfigureAwait(false);
            if (!add.Ok) return null;

            // '--full-index' writes whole blob hashes rather than abbreviations,
            // which is what lets 'git apply --3way' find the pre-image on the
            // other machine and land the patch even when the base has moved.
            var diff = await git.RunAsync(dir, env, ct,
                "diff", "--cached", "--binary", "--full-index", "--no-color",
                "--no-ext-diff", baseCommit).ConfigureAwait(false);

            if (!diff.Ok || diff.StdOut.Length == 0) return null;   // nothing uncommitted

            var names = await git.RunAsync(dir, env, ct,
                "diff", "--cached", "--name-only", baseCommit).ConfigureAwait(false);

            return new WorkPatch(
                RepoPath: checkout.RelativePath ?? checkout.AbsolutePath,
                BaseCommit: baseCommit,
                Branch: branch,
                Patch: Encoding.UTF8.GetBytes(diff.StdOut),
                ChangedFiles: names.Ok ? names.Lines.Length : 0,
                StagedFiles: stagedCount);
        }
        finally
        {
            if (File.Exists(indexFile)) { try { File.Delete(indexFile); } catch (IOException) { } }
        }
    }

    /// <summary>Whether a patch can land here, without changing anything.</summary>
    public async Task<GitResult> CheckAsync(string checkoutDir, byte[] patch, bool threeWay, CancellationToken ct = default)
        => await ApplyAsync(checkoutDir, patch, threeWay, check: true, ct).ConfigureAwait(false);

    /// <summary>Applies a patch to a working tree.</summary>
    public async Task<GitResult> ApplyAsync(
        string checkoutDir, byte[] patch, bool threeWay, bool check = false, CancellationToken ct = default)
    {
        var file = Path.Combine(Path.GetTempPath(), "weft-patch-" + Guid.NewGuid().ToString("n") + ".patch");

        try
        {
            await File.WriteAllBytesAsync(file, patch, ct).ConfigureAwait(false);

            var args = new List<string> { "apply" };
            if (check) args.Add("--check");
            if (threeWay && !check) args.Add("--3way");
            args.Add(file);

            return await git.RunAsync(checkoutDir, ct, args.ToArray()).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(file)) { try { File.Delete(file); } catch (IOException) { } }
        }
    }
}
