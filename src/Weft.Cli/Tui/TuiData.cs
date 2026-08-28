using System.Text;
using Weft.Core.Git;
using Weft.Core.Merge;
using Weft.Core.Store;
using Weft.Core.Ui;
using Weft.Core.Workspace;

namespace Weft.Cli.Tui;

/// <summary>Reads the workspace into something the screen can draw.</summary>
internal static class TuiData
{
    public static async Task<UiData> LoadAsync(WeftRoot root, CancellationToken ct)
    {
        var machine = MachineStore.LoadOrMint();
        var store = new ObjectStore(root.StorePath);
        var refs = new RefStore(root.MetaPath);
        var applier = new MergeApplier(root, store);
        var git = new GitRunner();

        var head = refs.ReadHead();
        var pushed = refs.ReadPushed();

        var machines = new List<MachineRow>
        {
            new(machine.Name, head?.ToString(), DateTimeOffset.UtcNow, true),
        };
        machines.AddRange(refs.ReadRemoteHeads()
            .Select(h => new MachineRow(h.MachineName, h.Snapshot, h.SeenUtc, false)));

        var attention = new List<AttentionRow>();
        var conflicts = new List<ConflictRow>();

        // A merge waiting on a decision, and the files it could not settle.
        var pending = applier.LoadState();
        if (pending is not null)
        {
            foreach (var path in applier.UnresolvedOf(pending))
            {
                var ours = ReadLines(Path.Combine(root.Path, path));
                var theirs = ReadLines(Path.Combine(root.Path, path + MergeApplier.TheirsSuffix));
                conflicts.Add(new ConflictRow(path, "both machines changed it", ours, theirs));
            }

            if (conflicts.Count > 0)
                attention.Add(new AttentionRow(AttentionKind.Conflict,
                    $"{conflicts.Count} file(s)", "a merge is waiting on you"));
        }

        var walk = new TreeWalk(root.Path, root.Policy).Run(ct);
        var repos = await new RepoDiscovery(root.Path, git).ResolveAsync(walk.CheckoutRoots, ct: ct).ConfigureAwait(false);
        var live = await new RepoStateReader(git).ReadAsync(repos, ct: ct).ConfigureAwait(false);

        var carried = head is null
            ? []
            : Snapshot.Parse(store.Get(head.Value)).Carried.Select(c => c.RepoPath).ToHashSet(StringComparer.Ordinal);

        // The question the whole tool exists for, asked before anything else.
        foreach (var r in live.Where(r => r.DirtyFiles > 0))
        {
            if (!carried.Contains(r.Path))
                attention.Add(new AttentionRow(AttentionKind.WorkOnlyHere,
                    r.Path, $"{r.DirtyFiles} uncommitted file(s), recorded nowhere"));
            else if (head is not null && pushed != head)
                attention.Add(new AttentionRow(AttentionKind.NotPushed,
                    r.Path, $"{r.DirtyFiles} uncommitted file(s), recorded but not on the server"));
        }

        foreach (var c in repos.SelectMany(r => r.Volatile))
            attention.Add(new AttentionRow(AttentionKind.VolatileWorktree,
                Path.GetFileName(c.AbsolutePath), "under a directory the OS may reclaim"));

        foreach (var c in repos.SelectMany(r => r.External).Where(c => !c.IsVolatile))
            attention.Add(new AttentionRow(AttentionKind.ExternalWorktree,
                Path.GetFileName(c.AbsolutePath), "outside the workspace; weft cannot sync it"));

        if (head is not null && pushed != head && !attention.Any(a => a.Kind == AttentionKind.NotPushed))
            attention.Add(new AttentionRow(AttentionKind.NotPushed,
                "this workspace", "the latest snapshot is not on the server"));

        return new UiData
        {
            WorkspacePath = root.Path,
            MachineName = machine.Name,
            Head = head?.ToString(),
            Pushed = head is not null && pushed == head,
            Machines = machines,
            Attention = attention,
            Conflicts = conflicts,
        };
    }

    /// <summary>
    /// Carries out a decision the screen made.
    /// </summary>
    /// <remarks>
    /// Deletes the companion files, which is the same signal 'weft merge --continue'
    /// looks for. Resolving here and resolving in an editor therefore mean exactly
    /// the same thing, and neither can leave the other's idea of "settled" behind.
    /// </remarks>
    public static string Resolve(WeftRoot root, ObjectStore store, UiCommand command)
    {
        var applier = new MergeApplier(root, store);

        switch (command)
        {
            case UiCommand.TakeOurs ours:
                applier.ClearCompanions(ours.Path);
                return $"kept our version of {ours.Path}";

            case UiCommand.TakeTheirs theirs:
                var target = Path.Combine(root.Path, theirs.Path);
                var source = target + MergeApplier.TheirsSuffix;

                if (!File.Exists(source)) return $"{theirs.Path} was already settled";

                // Through a temp file and a rename, like every other write: a
                // process killed here would otherwise leave a half-copied file
                // under the real name, which reads as a considered answer.
                var temp = target + ".weft-tmp";
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, target, overwrite: true);

                applier.ClearCompanions(theirs.Path);
                return $"took their version of {theirs.Path}";

            default:
                return "";
        }
    }

    private static IReadOnlyList<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return [];

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0)
                return ["(binary file)"];

            var text = Encoding.UTF8.GetString(bytes);
            if (text.EndsWith('\n')) text = text[..^1];
            return text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [$"(cannot read: {e.Message})"];
        }
    }
}
