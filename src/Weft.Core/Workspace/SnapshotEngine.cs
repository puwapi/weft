using System.Collections.Concurrent;
using System.Diagnostics;
using Weft.Core.Git;
using Weft.Core.Safety;
using Weft.Core.Store;

namespace Weft.Core.Workspace;

/// <summary>Which of the two things a snapshot records a credential turned up in.</summary>
/// <remarks>
/// Kept apart because the way out differs. Uncommitted work can be left behind
/// with '--no-carry'; a loose file cannot, and offering that flag for one would
/// send someone chasing a switch that changes nothing.
/// </remarks>
public enum SecretOrigin
{
    /// <summary>A file the workspace holds directly, outside every repository.</summary>
    LooseFile,

    /// <summary>A patch of uncommitted work inside a checkout.</summary>
    CarriedWork,
}

/// <summary>One credential, and where it was about to be recorded from.</summary>
public sealed record SecretHit(string Where, SecretOrigin Origin, SecretFinding Finding);

/// <summary>Credentials were found in content about to be recorded.</summary>
/// <remarks>
/// Blocks the snapshot rather than warning and continuing. A warning that does not
/// stop anything is read once and then never again, and the cost of the two
/// mistakes is not comparable: a blocked snapshot is fixed in seconds, a key that
/// reached the server is not.
/// </remarks>
public sealed class SecretsFoundException(IReadOnlyList<SecretHit> findings)
    : Exception($"credentials found in content about to be recorded ({findings.Count} occurrence(s))")
{
    public IReadOnlyList<SecretHit> Findings { get; } = findings;
}

/// <summary>What one snapshot cost and contained.</summary>
public sealed record SnapshotResult
{
    public required ChunkId Id { get; init; }
    public required Snapshot Snapshot { get; init; }
    public required ManifestDiff Diff { get; init; }

    /// <summary>Content chunks written that the store did not already hold. This is what a push would send.</summary>
    public required int NewChunks { get; init; }
    public required long NewBytes { get; init; }

    /// <summary>
    /// Bytes of the manifest itself that were new. Reported apart from content so
    /// that the headline figure compares like with like: folding manifest chunks
    /// into the content total once produced "100.82% of the workspace".
    /// </summary>
    public required long ManifestBytes { get; init; }

    /// <summary>
    /// Bytes of carried patches that were new. Reported apart from file content
    /// for the same reason as the manifest: a patch is not part of the workspace's
    /// size, and folding it in once produced a share above 100%.
    /// </summary>
    public required long CarriedBytes { get; init; }

    /// <summary>Uncommitted work recorded, per checkout.</summary>
    public required IReadOnlyList<CarriedWork> Carried { get; init; }

    public required int FilesRead { get; init; }
    public required long BytesRead { get; init; }
    public required long ElapsedMs { get; init; }

    /// <summary>True when nothing changed since the parent, so no snapshot was recorded.</summary>
    public required bool NoChange { get; init; }
}

/// <summary>Records the state of a workspace.</summary>
public sealed class SnapshotEngine(WeftRoot root, ObjectStore store, GitRunner git)
{
    /// <summary>
    /// Reads the workspace and records it.
    /// </summary>
    /// <remarks>
    /// Read-only with respect to the workspace: it opens files and asks git
    /// questions, and writes nothing outside the object store and the HEAD
    /// pointer. Snapshotting must always be safe to run, including while a build
    /// is writing, which is why an unreadable file is skipped rather than fatal.
    /// </remarks>
    /// <param name="extraParents">
    /// Additional parents, used to record a merge.
    /// </param>
    /// <remarks>
    /// A merge snapshot carrying BOTH heads is what stops a resolved conflict from
    /// coming back. Without it the next merge finds the same common ancestor, sees
    /// the same two divergent versions, and asks the same question again, forever.
    /// </remarks>
    /// <param name="carryWork">
    /// Capture uncommitted work in every checkout. On by default: this is the
    /// safety net the tool exists for, and one that has to be asked for is one
    /// nobody has switched on when the drive fails.
    /// </param>
    public async Task<SnapshotResult> CreateAsync(
        MachineIdentity machine, int maxParallel = 8,
        IReadOnlyList<ChunkId>? extraParents = null, bool carryWork = true,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var walk = new TreeWalk(root.Path, root.Policy).Run(ct);
        var repos = await new RepoDiscovery(root.Path, git).ResolveAsync(walk.CheckoutRoots, maxParallel, ct)
            .ConfigureAwait(false);
        var repoStates = await new RepoStateReader(git).ReadAsync(repos, maxParallel, ct).ConfigureAwait(false);

        var entries = new ConcurrentBag<FileEntry>();
        var looseFindings = new ConcurrentBag<SecretHit>();
        long bytesRead = 0;
        var newChunks = 0;
        long newBytes = 0;

        await Parallel.ForEachAsync(
            walk.LooseFiles,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (rel, token) =>
            {
                var abs = Path.Combine(root.Path, rel.Replace('/', Path.DirectorySeparatorChar));

                byte[] data;
                FileInfo info;
                try
                {
                    info = new FileInfo(abs);
                    data = await File.ReadAllBytesAsync(abs, token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The tree is live. A file deleted or locked between the walk
                    // and the read is normal, not a failure of the snapshot.
                    return;
                }

                Interlocked.Add(ref bytesRead, data.Length);

                // Split and name first, store second. A file that turns out to
                // hold a credential must not have left a trace in the store, and
                // the scan cannot run until we know the file is new.
                var chunks = new List<ChunkId>();
                var fresh = new List<(int Offset, int Length)>();

                foreach (var (offset, length) in FastCdc.Split(data))
                {
                    var id = ChunkId.Of(data.AsSpan(offset, length));
                    if (!store.Contains(id)) fresh.Add((offset, length));
                    chunks.Add(id);
                }

                // Scanned only when the file brings content the store does not
                // already hold, which is what keeps a snapshot of an unchanged
                // tree from re-reading every line of it. The consequence is worth
                // stating: content already stored is not scanned again, because
                // it is already on the server and refusing it now would cost the
                // snapshot without taking anything back.
                //
                // Whole file, never chunk by chunk: a key that straddles a chunk
                // boundary is invisible to both halves.
                if (fresh.Count > 0)
                {
                    var found = SecretScanner.Scan(data);
                    if (found.Count > 0)
                    {
                        foreach (var f in found) looseFindings.Add(new SecretHit(rel, SecretOrigin.LooseFile, f));
                        return;
                    }
                }

                foreach (var (offset, length) in fresh)
                {
                    store.Put(data.AsSpan(offset, length));
                    Interlocked.Increment(ref newChunks);
                    Interlocked.Add(ref newBytes, length);
                }

                entries.Add(new FileEntry(rel, data.Length, info.LastWriteTimeUtc, IsExecutable(info), chunks));
            }).ConfigureAwait(false);

        var (carried, carryFindings) = carryWork
            ? await CarryAsync(repos, maxParallel, ct).ConfigureAwait(false)
            : ([], []);

        // Both sets are reported together. Refusing on the loose files first and
        // the patches on the next run makes someone fix the same snapshot twice,
        // and the second refusal reads like the first fix did not work.
        if (!looseFindings.IsEmpty || carryFindings.Count > 0)
            throw new SecretsFoundException([
                .. looseFindings.OrderBy(f => f.Where, StringComparer.Ordinal),
                .. carryFindings,
            ]);

        var newCarriedBytes = carried.Sum(c => c.NewBytes);
        newChunks += carried.Sum(c => c.NewChunks);

        var manifest = new Manifest(entries);
        var manifestBytes = manifest.Serialise();

        var manifestChunks = new List<ChunkId>();
        long newManifestBytes = 0;
        foreach (var (offset, length) in FastCdc.Split(manifestBytes))
        {
            var span = manifestBytes.AsSpan(offset, length);
            var id = ChunkId.Of(span);
            if (!store.Contains(id)) { store.Put(span); newManifestBytes += length; }
            manifestChunks.Add(id);
        }

        var refs = new RefStore(root.MetaPath);
        var parentId = refs.ReadHead();
        var parentManifest = parentId is null ? new Manifest([]) : LoadManifest(parentId.Value);
        var diff = ManifestDiff.Between(parentManifest, manifest);

        // Nothing moved and no repository changed: recording a snapshot would add
        // a node to the graph carrying no information, and a machine left running
        // would fill the history with them.
        var merging = extraParents is { Count: > 0 };
        var parentSnapshot = parentId is null ? null : LoadSnapshot(parentId.Value);
        var reposUnchanged = parentSnapshot is not null && SameRepos(parentSnapshot.Repos, repoStates);
        var carryUnchanged = parentSnapshot is not null
            && SameCarried(parentSnapshot.Carried, carried.Select(c => c.Work).ToList());

        // A merge is always recorded, even when it changed no file. The snapshot
        // exists to say "these two histories are now one", and skipping it because
        // nothing moved would leave the two heads forever divergent.
        if (!merging && diff.IsEmpty && reposUnchanged && carryUnchanged)
        {
            return new SnapshotResult
            {
                Id = parentId!.Value,
                Snapshot = LoadSnapshot(parentId.Value),
                Diff = diff,
                NewChunks = 0,
                NewBytes = 0,
                ManifestBytes = 0,
                CarriedBytes = 0,
                Carried = parentSnapshot!.Carried,
                FilesRead = entries.Count,
                BytesRead = bytesRead,
                ElapsedMs = sw.ElapsedMilliseconds,
                NoChange = true,
            };
        }

        var snapshot = new Snapshot
        {
            ManifestId = ChunkId.Of(manifestBytes),
            ManifestChunks = manifestChunks,
            Parents = BuildParents(parentId, extraParents),
            MachineId = machine.Id,
            MachineName = machine.Name,
            CreatedUtc = DateTimeOffset.UtcNow,
            Repos = repoStates,
            Carried = carried.Select(c => c.Work).ToList(),
            FileCount = manifest.Entries.Count,
            TotalBytes = manifest.TotalBytes,
        };

        var snapshotBytes = snapshot.Serialise();
        var snapshotId = store.Put(snapshotBytes);
        refs.WriteHead(snapshotId);

        return new SnapshotResult
        {
            Id = snapshotId,
            Snapshot = snapshot,
            Diff = diff,
            NewChunks = newChunks,
            NewBytes = newBytes,
            ManifestBytes = newManifestBytes,
            CarriedBytes = newCarriedBytes,
            Carried = snapshot.Carried,
            FilesRead = entries.Count,
            BytesRead = bytesRead,
            ElapsedMs = sw.ElapsedMilliseconds,
            NoChange = false,
        };
    }

    private static IReadOnlyList<ChunkId> BuildParents(ChunkId? head, IReadOnlyList<ChunkId>? extra)
    {
        var parents = new List<ChunkId>();
        if (head is not null) parents.Add(head.Value);

        foreach (var p in extra ?? [])
            if (!parents.Contains(p)) parents.Add(p);

        return parents;
    }

    /// <summary>
    /// Captures uncommitted work in every checkout inside the workspace.
    /// </summary>
    /// <remarks>
    /// Scanned for credentials BEFORE anything is stored, and the findings are
    /// returned rather than thrown so the caller can refuse once for the whole
    /// snapshot. A patch is git-tracked content, so the path-based ignore rules
    /// cannot help: a key pasted into a source file while debugging sits in a path
    /// nobody would ever have listed.
    /// </remarks>
    private async Task<(IReadOnlyList<(CarriedWork Work, int NewChunks, long NewBytes)> Work,
                       IReadOnlyList<SecretHit> Findings)> CarryAsync(
        IReadOnlyList<Repository> repos, int maxParallel, CancellationToken ct)
    {
        var capture = new WorkCapture(git);
        var checkouts = repos.SelectMany(r => r.Checkouts).Where(c => c.IsInsideRoot).ToList();

        var patches = new ConcurrentBag<WorkPatch>();

        await Parallel.ForEachAsync(
            checkouts,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (c, token) =>
            {
                var patch = await capture.CaptureAsync(c, token).ConfigureAwait(false);
                if (patch is not null) patches.Add(patch);
            }).ConfigureAwait(false);

        // Returned rather than thrown, so the caller can report these alongside
        // anything found in loose files. Nothing below this point runs when there
        // is a finding, so no chunk of a patch holding a credential is stored.
        var findings = patches
            .OrderBy(p => p.RepoPath, StringComparer.Ordinal)
            .SelectMany(p => SecretScanner.Scan(p.Patch)
                .Select(f => new SecretHit(p.RepoPath, SecretOrigin.CarriedWork, f)))
            .ToList();

        if (findings.Count > 0) return ([], findings);

        var result = new List<(CarriedWork, int, long)>();

        foreach (var p in patches.OrderBy(p => p.RepoPath, StringComparer.Ordinal))
        {
            var chunks = new List<ChunkId>();
            var fresh = 0;
            long freshBytes = 0;

            foreach (var (offset, length) in FastCdc.Split(p.Patch))
            {
                var span = p.Patch.AsSpan(offset, length);
                var id = ChunkId.Of(span);
                if (!store.Contains(id)) { store.Put(span); fresh++; freshBytes += length; }
                chunks.Add(id);
            }

            result.Add((
                new CarriedWork(p.RepoPath, p.BaseCommit, p.Branch, chunks,
                    p.Patch.Length, p.ChangedFiles, p.StagedFiles),
                fresh, freshBytes));
        }

        return (result, []);
    }

    private static bool SameCarried(IReadOnlyList<CarriedWork> a, IReadOnlyList<CarriedWork> b)
    {
        if (a.Count != b.Count) return false;

        var byPath = b.ToDictionary(c => c.RepoPath, StringComparer.Ordinal);
        foreach (var x in a)
        {
            if (!byPath.TryGetValue(x.RepoPath, out var y)) return false;
            if (x.BaseCommit != y.BaseCommit) return false;
            if (!x.PatchChunks.SequenceEqual(y.PatchChunks)) return false;
        }

        return true;
    }

    public Snapshot LoadSnapshot(ChunkId id) => Snapshot.Parse(store.Get(id));

    /// <summary>Reassembles a manifest from its chunks and checks the result against its recorded hash.</summary>
    public Manifest LoadManifest(ChunkId snapshotId)
    {
        var snapshot = LoadSnapshot(snapshotId);

        var parts = snapshot.ManifestChunks.Select(store.Get).ToList();
        var total = parts.Sum(p => p.Length);

        var buffer = new byte[total];
        var offset = 0;
        foreach (var p in parts) { p.CopyTo(buffer, offset); offset += p.Length; }

        // Chunks verify individually, but nothing so far has checked their ORDER.
        var actual = ChunkId.Of(buffer);
        if (actual != snapshot.ManifestId) throw new CorruptObjectException(snapshot.ManifestId, actual);

        return Manifest.Parse(buffer);
    }

    private static bool SameRepos(IReadOnlyList<RepoState> a, IReadOnlyList<RepoState> b)
        => a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);

    private static bool IsExecutable(FileInfo info)
    {
        if (OperatingSystem.IsWindows()) return false;

        // Owner execute bit. Read and write bits are deliberately not carried:
        // they belong to the account that will hold the file, and copying them
        // between machines with different users makes files unreadable on arrival.
        return (info.UnixFileMode & UnixFileMode.UserExecute) != 0;
    }
}
