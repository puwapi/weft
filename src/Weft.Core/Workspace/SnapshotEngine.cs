using System.Collections.Concurrent;
using System.Diagnostics;
using Weft.Core.Git;
using Weft.Core.Store;

namespace Weft.Core.Workspace;

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
    public async Task<SnapshotResult> CreateAsync(
        MachineIdentity machine, int maxParallel = 8, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var walk = new TreeWalk(root.Path, root.Policy).Run(ct);
        var repos = await new RepoDiscovery(root.Path, git).ResolveAsync(walk.CheckoutRoots, maxParallel, ct)
            .ConfigureAwait(false);
        var repoStates = await new RepoStateReader(git).ReadAsync(repos, maxParallel, ct).ConfigureAwait(false);

        var entries = new ConcurrentBag<FileEntry>();
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

                var chunks = new List<ChunkId>();
                foreach (var (offset, length) in FastCdc.Split(data))
                {
                    var span = data.AsSpan(offset, length);
                    var id = ChunkId.Of(span);

                    if (!store.Contains(id))
                    {
                        store.Put(span);
                        Interlocked.Increment(ref newChunks);
                        Interlocked.Add(ref newBytes, length);
                    }

                    chunks.Add(id);
                }

                entries.Add(new FileEntry(rel, data.Length, info.LastWriteTimeUtc, IsExecutable(info), chunks));
            }).ConfigureAwait(false);

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
        var reposUnchanged = parentId is not null && SameRepos(LoadSnapshot(parentId.Value).Repos, repoStates);
        if (diff.IsEmpty && reposUnchanged)
        {
            return new SnapshotResult
            {
                Id = parentId!.Value,
                Snapshot = LoadSnapshot(parentId.Value),
                Diff = diff,
                NewChunks = 0,
                NewBytes = 0,
                ManifestBytes = 0,
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
            Parents = parentId is null ? [] : [parentId.Value],
            MachineId = machine.Id,
            MachineName = machine.Name,
            CreatedUtc = DateTimeOffset.UtcNow,
            Repos = repoStates,
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
            FilesRead = entries.Count,
            BytesRead = bytesRead,
            ElapsedMs = sw.ElapsedMilliseconds,
            NoChange = false,
        };
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
