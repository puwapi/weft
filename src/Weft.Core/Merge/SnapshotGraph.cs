using Weft.Core.Store;

namespace Weft.Core.Merge;

/// <summary>Walks the snapshot ancestry.</summary>
/// <remarks>
/// The common ancestor is what separates weft from a byte-level synchroniser.
/// With two versions there is no way to tell "they added a line" from "I deleted
/// one"; with the version both machines started from, most differences settle
/// without asking anyone.
/// </remarks>
public sealed class SnapshotGraph(ObjectStore store)
{
    private readonly Dictionary<ChunkId, Snapshot> _cache = [];

    public Snapshot Load(ChunkId id)
    {
        if (_cache.TryGetValue(id, out var cached)) return cached;

        var snapshot = Snapshot.Parse(store.Get(id));
        _cache[id] = snapshot;
        return snapshot;
    }

    /// <summary>True when <paramref name="ancestor"/> is reachable from <paramref name="from"/>.</summary>
    public bool IsAncestorOf(ChunkId ancestor, ChunkId from)
    {
        if (ancestor == from) return true;

        var seen = new HashSet<ChunkId>();
        var queue = new Queue<ChunkId>([from]);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (id == ancestor) return true;

            foreach (var p in Parents(id)) queue.Enqueue(p);
        }

        return false;
    }

    /// <summary>
    /// The snapshot both sides last shared, or null when they share nothing.
    /// </summary>
    /// <remarks>
    /// Two machines that never met have no common ancestor. That is a legitimate
    /// state, not an error: a workspace set up independently on each machine and
    /// only then pointed at the same server. Everything then reads as "added on
    /// one side", which is exactly right, and nothing is lost.
    /// </remarks>
    public ChunkId? MergeBase(ChunkId ours, ChunkId theirs)
    {
        if (ours == theirs) return ours;

        var ourAncestry = new HashSet<ChunkId>();
        var stack = new Stack<ChunkId>([ours]);

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!ourAncestry.Add(id)) continue;
            foreach (var p in Parents(id)) stack.Push(p);
        }

        // Breadth-first from their side, so the first hit is the nearest common
        // ancestor rather than an arbitrary one. A distant ancestor would still
        // produce a correct merge, but it would report differences both machines
        // had already agreed on long ago.
        var seen = new HashSet<ChunkId>();
        var queue = new Queue<ChunkId>([theirs]);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (ourAncestry.Contains(id)) return id;

            foreach (var p in Parents(id)) queue.Enqueue(p);
        }

        return null;
    }

    private IEnumerable<ChunkId> Parents(ChunkId id)
    {
        Snapshot snapshot;
        try { snapshot = Load(id); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // History that was never fetched, or was pruned. Treated as a root
            // rather than as an error: a missing ancestor makes the merge more
            // conservative, never less.
            yield break;
        }

        foreach (var p in snapshot.Parents) yield return p;
    }
}
