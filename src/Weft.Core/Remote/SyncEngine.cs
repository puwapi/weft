using System.Diagnostics;
using Weft.Core.Crypto;
using Weft.Core.Protocol;
using Weft.Core.Store;
using Weft.Core.Workspace;

namespace Weft.Core.Remote;

public sealed record PushResult
{
    public required ChunkId Snapshot { get; init; }
    public required int ObjectsConsidered { get; init; }
    public required int ObjectsUploaded { get; init; }
    public required long BytesUploaded { get; init; }
    public required long ElapsedMs { get; init; }
    public required bool AlreadyCurrent { get; init; }
}

public sealed record PullResult
{
    public required IReadOnlyList<HeadEntry> Heads { get; init; }
    public required int ObjectsFetched { get; init; }
    public required long BytesFetched { get; init; }
    /// <summary>
    /// What was fetched, with the id that snapshot goes by LOCALLY.
    /// </summary>
    /// <remarks>
    /// The local id is carried deliberately. A snapshot has two names: the one it
    /// has here, and the keyed one the server files it under. Showing the server's
    /// name to a person makes the snapshot they just pushed look like a different
    /// one when another machine pulls it.
    /// </remarks>
    public required IReadOnlyList<(HeadEntry Machine, ChunkId LocalId, Snapshot Snapshot)> Fetched { get; init; }
    public required long ElapsedMs { get; init; }
}

/// <summary>Moves objects between the local store and a server.</summary>
/// <remarks>
/// Content never leaves this process in the clear. Everything is encrypted here,
/// named by a keyed identifier the server cannot invert, and decrypted here on
/// the way back. The server is a place to put bytes, not a party to trust.
/// </remarks>
public sealed class SyncEngine(
    WeftRoot root, ObjectStore store, WorkspaceKey key, RemoteClient client)
{
    private readonly ChunkCipher _cipher = new(key);

    /// <summary>
    /// Sends everything this machine's HEAD depends on, then moves the pointer.
    /// </summary>
    /// <remarks>
    /// The pointer moves last, and only after every object it needs is on the
    /// server. Moving it first would leave other machines following a pointer
    /// into objects that do not exist, and they would have no way to tell that
    /// from corruption.
    /// </remarks>
    public async Task<PushResult> PushAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var head = new RefStore(root.MetaPath).ReadHead()
            ?? throw new InvalidOperationException("Nothing to push: this workspace has no snapshot yet. Run 'weft snapshot'.");

        var needed = Reachable(head);
        var byRemote = needed.ToDictionary(c => RemoteId.Of(key, c), c => c);

        var missing = await client.MissingAsync(byRemote.Keys, ct).ConfigureAwait(false);

        var uploaded = 0;
        long bytes = 0;

        foreach (var remoteId in missing)
        {
            ct.ThrowIfCancellationRequested();

            var chunkId = byRemote[remoteId];
            var plaintext = store.Get(chunkId);
            var (sealedId, blob) = _cipher.Seal(plaintext, chunkId);

            await client.PutObjectAsync(sealedId, blob, ct).ConfigureAwait(false);
            uploaded++;
            bytes += blob.Length;
        }

        await client.SetHeadAsync(RemoteId.Of(key, head), ct).ConfigureAwait(false);

        // Written only after the pointer moved, so it records what the server
        // really has rather than what we intended to send.
        new RefStore(root.MetaPath).WritePushed(head);

        return new PushResult
        {
            Snapshot = head,
            ObjectsConsidered = needed.Count,
            ObjectsUploaded = uploaded,
            BytesUploaded = bytes,
            ElapsedMs = sw.ElapsedMilliseconds,
            AlreadyCurrent = uploaded == 0,
        };
    }

    /// <summary>
    /// Fetches what other machines have recorded, into the local store.
    /// </summary>
    /// <remarks>
    /// Deliberately stops at the store. Nothing is written into the working tree,
    /// because deciding what a file should contain when two machines disagree is
    /// the merge engine's job, and a pull that quietly overwrote local edits would
    /// be the single most destructive thing this tool could do.
    /// </remarks>
    public async Task<PullResult> PullAsync(string thisMachineId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var heads = await client.HeadsAsync(ct).ConfigureAwait(false);
        var fetched = new List<(HeadEntry, ChunkId, Snapshot)>();
        var objects = 0;
        long bytes = 0;

        foreach (var h in heads)
        {
            if (h.Snapshot is null) continue;
            if (h.MachineId == thisMachineId) continue;
            if (!RemoteId.TryParse(h.Snapshot, out var remoteSnapshot)) continue;

            var (snapshot, localId, n, b) = await FetchSnapshotAsync(remoteSnapshot, ct).ConfigureAwait(false);
            objects += n;
            bytes += b;
            fetched.Add((h, localId, snapshot));
        }

        // Recorded now, while both names of each snapshot are in hand. A merge
        // afterwards needs no network.
        new RefStore(root.MetaPath).WriteRemoteHeads(
            fetched.Select(f => new RemoteHead(
                f.Item1.MachineId, f.Item1.MachineName, f.Item2.ToString(), DateTimeOffset.UtcNow)).ToList());

        return new PullResult
        {
            Heads = heads,
            ObjectsFetched = objects,
            BytesFetched = bytes,
            Fetched = fetched,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private async Task<(Snapshot Snapshot, ChunkId LocalId, int Objects, long Bytes)> FetchSnapshotAsync(
        RemoteId remoteSnapshot, CancellationToken ct)
    {
        var objects = 0;
        long bytes = 0;

        // The snapshot object is the one fetch whose plaintext id is not yet
        // known, so it cannot be verified against a name the caller chose. It is
        // authenticated by its tag and by the name it was filed under, and any
        // mismatch fails to decrypt rather than parsing into something plausible.
        var snapshotBlob = await client.GetObjectAsync(remoteSnapshot, ct).ConfigureAwait(false);
        objects++;
        bytes += snapshotBlob.Length;

        var snapshotBytes = OpenUnknown(snapshotBlob, remoteSnapshot);
        var snapshot = Snapshot.Parse(snapshotBytes);
        var localId = ChunkId.Of(snapshotBytes);

        if (!store.Contains(localId)) store.Put(snapshotBytes);

        foreach (var chunk in snapshot.ManifestChunks)
        {
            var (n, b) = await EnsureLocalAsync(chunk, ct).ConfigureAwait(false);
            objects += n;
            bytes += b;
        }

        // The manifest names every content chunk, so it has to arrive before the
        // rest can be asked for at all. From there the same reachability function
        // the push uses decides what is needed, which is what keeps the two sides
        // from drifting apart again.
        var manifest = ReadManifest(snapshot);

        foreach (var chunk in Reachability.ChunksOf(localId, snapshot, manifest))
        {
            var (n, b) = await EnsureLocalAsync(chunk, ct).ConfigureAwait(false);
            objects += n;
            bytes += b;
        }

        return (snapshot, localId, objects, bytes);
    }

    private async Task<(int Objects, long Bytes)> EnsureLocalAsync(ChunkId chunk, CancellationToken ct)
    {
        if (store.Contains(chunk)) return (0, 0);

        var blob = await client.GetObjectAsync(RemoteId.Of(key, chunk), ct).ConfigureAwait(false);
        store.Put(_cipher.Open(blob, chunk));
        return (1, blob.Length);
    }

    private Manifest ReadManifest(Snapshot snapshot)
    {
        var parts = snapshot.ManifestChunks.Select(store.Get).ToList();
        var buffer = new byte[parts.Sum(p => p.Length)];

        var offset = 0;
        foreach (var p in parts) { p.CopyTo(buffer, offset); offset += p.Length; }

        var actual = ChunkId.Of(buffer);
        if (actual != snapshot.ManifestId) throw new CorruptObjectException(snapshot.ManifestId, actual);

        return Manifest.Parse(buffer);
    }

    /// <summary>
    /// Decrypts an object whose plaintext id is not known in advance.
    /// </summary>
    /// <remarks>
    /// Only used for the snapshot at the root of a fetch. It re-derives the id
    /// from the plaintext and checks that the object really was filed under the
    /// name it was asked for, which is the same guarantee the ordinary path gets
    /// for free.
    /// </remarks>
    private byte[] OpenUnknown(byte[] blob, RemoteId expected)
    {
        // Try the ordinary path first: decrypt, then confirm the name matches.
        // Deriving the plaintext id requires decrypting, and decrypting requires
        // the name as associated data, so the check is done after the fact.
        var probe = TryOpenAnyName(blob, expected);
        if (probe is null)
            throw new DecryptionFailedException(
                "the snapshot object failed authentication: it was altered, or it belongs to a different workspace");

        if (RemoteId.Of(key, ChunkId.Of(probe)) != expected)
            throw new DecryptionFailedException("the server returned an object filed under the wrong name");

        return probe;
    }

    private byte[]? TryOpenAnyName(byte[] blob, RemoteId name)
    {
        try { return ChunkCipher.OpenWithName(key, blob, name); }
        catch (DecryptionFailedException) { return null; }
    }

    private HashSet<ChunkId> Reachable(ChunkId snapshotId)
    {
        var snapshot = Snapshot.Parse(store.Get(snapshotId));
        return Reachability.ChunksOf(snapshotId, snapshot, ReadManifest(snapshot));
    }
}
