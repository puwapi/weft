namespace Weft.Core.Store;

/// <summary>What changed between two manifests.</summary>
public sealed record ManifestDiff
{
    public required IReadOnlyList<FileEntry> Added { get; init; }
    public required IReadOnlyList<(FileEntry From, FileEntry To)> Changed { get; init; }
    public required IReadOnlyList<FileEntry> Removed { get; init; }

    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0;
    public int Count => Added.Count + Changed.Count + Removed.Count;

    /// <summary>
    /// Compares two manifests.
    /// </summary>
    /// <remarks>
    /// A file counts as changed when its <em>content</em> differs, that is, when
    /// its chunk list differs. Size and timestamp are recorded but are not the
    /// test: a rebuild that rewrites a file byte for byte moves its timestamp
    /// without changing anything, and treating that as a change would make every
    /// build produce a snapshot full of files that did not move.
    /// </remarks>
    public static ManifestDiff Between(Manifest before, Manifest after)
    {
        var old = before.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);

        var added = new List<FileEntry>();
        var changed = new List<(FileEntry, FileEntry)>();

        foreach (var e in after.Entries)
        {
            if (!old.Remove(e.Path, out var prev)) { added.Add(e); continue; }
            if (!SameContent(prev, e)) changed.Add((prev, e));
        }

        return new ManifestDiff
        {
            Added = added,
            Changed = changed,
            Removed = old.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList(),
        };
    }

    private static bool SameContent(FileEntry a, FileEntry b)
    {
        if (a.Executable != b.Executable) return false;
        if (a.Chunks.Count != b.Chunks.Count) return false;

        for (var i = 0; i < a.Chunks.Count; i++)
            if (a.Chunks[i] != b.Chunks[i]) return false;

        return true;
    }

    /// <summary>
    /// Chunks present in <paramref name="after"/> that <paramref name="before"/>
    /// did not have. This is what a push actually has to send.
    /// </summary>
    public static IReadOnlySet<ChunkId> NewChunks(Manifest before, Manifest after)
    {
        var had = before.Entries.SelectMany(e => e.Chunks).ToHashSet();
        return after.Entries.SelectMany(e => e.Chunks).Where(c => !had.Contains(c)).ToHashSet();
    }
}
