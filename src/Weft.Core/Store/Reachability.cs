namespace Weft.Core.Store;

/// <summary>
/// Everything a snapshot depends on.
/// </summary>
/// <remarks>
/// <para>Its own object, its manifest's chunks, every file's content, and every
/// carried patch. A push sends this set and a pull fetches it, so anything missing
/// here is content that a snapshot NAMES and that never travels.</para>
///
/// <para>Written as one function on purpose. It used to live twice, once in the
/// push and once in the pull, and carried patches were added to the snapshot
/// without either copy learning about them. Both sides succeeded, the snapshot
/// looked complete because it named its patches, and 'weft land' on the other
/// machine failed looking for an object nobody had ever sent.</para>
/// </remarks>
public static class Reachability
{
    /// <summary>Every chunk the snapshot needs, given its manifest.</summary>
    public static HashSet<ChunkId> ChunksOf(ChunkId snapshotId, Snapshot snapshot, Manifest manifest)
    {
        var needed = new HashSet<ChunkId> { snapshotId };

        foreach (var c in snapshot.ManifestChunks) needed.Add(c);
        foreach (var c in manifest.Entries.SelectMany(e => e.Chunks)) needed.Add(c);
        foreach (var c in snapshot.Carried.SelectMany(w => w.PatchChunks)) needed.Add(c);

        // Parents are deliberately NOT followed. A push sends what this snapshot
        // needs to be readable, not the whole history: an older snapshot is
        // already on the server if it was ever pushed, and re-walking the chain
        // would make every push cost proportional to the workspace's age.
        return needed;
    }
}
