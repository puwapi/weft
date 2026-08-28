using Weft.Core.Store;

namespace Weft.Core.Merge;

/// <summary>What weft intends to do about one path.</summary>
public enum MergeAction
{
    /// <summary>Neither machine touched it.</summary>
    Unchanged,

    /// <summary>Only this machine changed it. Already right on disk.</summary>
    KeepOurs,

    /// <summary>Only the other machine changed it, or it is new to us.</summary>
    TakeTheirs,

    /// <summary>One machine removed it and the other did not bring it back.</summary>
    Delete,

    /// <summary>Both changed it, and the two versions were reconciled.</summary>
    Write,

    /// <summary>Both changed it and no honest single version exists.</summary>
    Conflict,
}

/// <summary>One path's outcome.</summary>
public sealed record MergeItem
{
    public required string Path { get; init; }
    public required MergeAction Action { get; init; }

    /// <summary>Content to write, for <see cref="MergeAction.TakeTheirs"/> and <see cref="MergeAction.Write"/>.</summary>
    public byte[]? Content { get; init; }

    /// <summary>Set when the resolution was not obvious, so it never happens silently.</summary>
    public string? Note { get; init; }

    /// <summary>Why a person has to look.</summary>
    public string? ConflictReason { get; init; }

    /// <summary>The two versions, kept so a conflict can be shown and resolved.</summary>
    public IReadOnlyList<Diff3Conflict> Regions { get; init; } = [];
}

/// <summary>A repository the two machines see differently. Reported, never acted on.</summary>
/// <param name="Path">Checkout path.</param>
/// <param name="Ours">This machine's state, null when the repository is absent here.</param>
/// <param name="Theirs">The other machine's state.</param>
public sealed record RepoDivergence(string Path, RepoState? Ours, RepoState? Theirs)
{
    /// <summary>The repository exists there and not here. The one case worth acting on.</summary>
    public bool MissingHere => Ours is null && Theirs is not null;

    public bool DifferentBranch =>
        Ours is not null && Theirs is not null && Ours.Branch != Theirs.Branch;

    public bool DifferentHead =>
        Ours is not null && Theirs is not null && Ours.Head != Theirs.Head;
}

/// <summary>The whole merge, decided but not yet applied.</summary>
public sealed record MergeOutcome
{
    public required ChunkId? Base { get; init; }
    public required ChunkId Ours { get; init; }
    public required ChunkId Theirs { get; init; }

    /// <summary>True when one side already contains the other: nothing to merge.</summary>
    public required bool FastForward { get; init; }

    public required IReadOnlyList<MergeItem> Items { get; init; }
    public required IReadOnlyList<RepoDivergence> Repos { get; init; }

    public IEnumerable<MergeItem> Conflicts => Items.Where(i => i.Action == MergeAction.Conflict);
    public IEnumerable<MergeItem> Changes => Items.Where(i => i.Action is not MergeAction.Unchanged and not MergeAction.KeepOurs);
    public bool Clean => !Conflicts.Any();
}

/// <summary>
/// Decides what a merge should do. Decides only: nothing here touches disk.
/// </summary>
/// <remarks>
/// <para>Computing the whole plan before applying any of it is what makes the
/// conflict rule enforceable. A merge that wrote as it went would leave half a
/// merge on disk the moment it met a conflict, and half a merge is worse than
/// none because it looks finished.</para>
///
/// <para>Repository state is reported and never acted on. Two machines on two
/// branches is not a disagreement to resolve; cloning a repository that exists
/// only on the other machine is a decision, not a consequence.</para>
/// </remarks>
public sealed class MergeEngine(ObjectStore store)
{
    private readonly SnapshotGraph _graph = new(store);

    public MergeOutcome Compute(ChunkId ours, ChunkId theirs)
    {
        var baseId = _graph.MergeBase(ours, theirs);

        // One side already contains the other. Nothing to reconcile: their work is
        // already ours, or ours already includes theirs.
        if (_graph.IsAncestorOf(theirs, ours) || _graph.IsAncestorOf(ours, theirs))
            return new MergeOutcome
            {
                Base = baseId,
                Ours = ours,
                Theirs = theirs,
                FastForward = true,
                Items = FastForwardItems(ours, theirs),
                Repos = CompareRepos(_graph.Load(ours), _graph.Load(theirs)),
            };

        var ourSnapshot = _graph.Load(ours);
        var theirSnapshot = _graph.Load(theirs);

        var ourFiles = Index(ReadManifest(ourSnapshot));
        var theirFiles = Index(ReadManifest(theirSnapshot));
        var baseFiles = baseId is null ? [] : Index(ReadManifest(_graph.Load(baseId.Value)));

        var paths = ourFiles.Keys.Concat(theirFiles.Keys).Concat(baseFiles.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal);

        var items = new List<MergeItem>();
        foreach (var path in paths)
        {
            baseFiles.TryGetValue(path, out var b);
            ourFiles.TryGetValue(path, out var o);
            theirFiles.TryGetValue(path, out var t);
            items.Add(Decide(path, b, o, t));
        }

        return new MergeOutcome
        {
            Base = baseId,
            Ours = ours,
            Theirs = theirs,
            FastForward = false,
            Items = items,
            Repos = CompareRepos(ourSnapshot, theirSnapshot),
        };
    }

    /// <summary>
    /// The complete table. Every combination of present-or-absent and
    /// changed-or-not on three sides, with nothing left to a default.
    /// </summary>
    private MergeItem Decide(string path, FileEntry? b, FileEntry? o, FileEntry? t)
    {
        var Item = (MergeAction a) => new MergeItem { Path = path, Action = a };

        // --- absent from the base: one or both machines created it ---
        if (b is null)
        {
            if (o is null && t is null) return Item(MergeAction.Unchanged);          // cannot happen
            if (t is null) return Item(MergeAction.KeepOurs);                        // we added it
            if (o is null) return Take(path, t);                                     // they added it
            if (Same(o, t)) return Item(MergeAction.Unchanged);                      // both added the same thing

            // Both created it, differently. There is no ancestor, so the content
            // merge runs against an empty base: anything the two share still
            // merges, and only the rest is a conflict.
            return MergeContent(path, [], o, t);
        }

        // --- both removed it ---
        if (o is null && t is null) return Item(MergeAction.Delete);

        // --- one removed it ---
        if (o is null)
        {
            // We removed it; did they change it?
            if (Same(b, t!)) return Item(MergeAction.Delete);

            return Conflict(path,
                "we deleted this file and the other machine changed it; keeping either is a decision, not a merge");
        }

        if (t is null)
        {
            if (Same(b, o)) return Item(MergeAction.Delete);

            return Conflict(path,
                "the other machine deleted this file and we changed it; keeping either is a decision, not a merge");
        }

        // --- present everywhere ---
        var weChanged = !Same(b, o);
        var theyChanged = !Same(b, t);

        if (!weChanged && !theyChanged) return Item(MergeAction.Unchanged);
        if (weChanged && !theyChanged) return Item(MergeAction.KeepOurs);
        if (!weChanged && theyChanged) return Take(path, t);
        if (Same(o, t)) return Item(MergeAction.Unchanged);   // both made the same change

        return MergeContent(path, Read(b), o, t);
    }

    private MergeItem MergeContent(string path, byte[] baseBytes, FileEntry o, FileEntry t)
    {
        var result = ContentMerge.Merge(path, baseBytes, Read(o), Read(t));

        return result switch
        {
            ContentResult.Merged m => new MergeItem
            {
                Path = path,
                Action = MergeAction.Write,
                Content = m.Content,
                Note = m.Note,
            },
            ContentResult.Conflict c => new MergeItem
            {
                Path = path,
                Action = MergeAction.Conflict,
                ConflictReason = c.Reason,
                Regions = c.Regions,
            },
            _ => throw new InvalidOperationException("unreachable"),
        };
    }

    private MergeItem Take(string path, FileEntry t) => new()
    {
        Path = path,
        Action = MergeAction.TakeTheirs,
        Content = Read(t),
    };

    private static MergeItem Conflict(string path, string reason) => new()
    {
        Path = path,
        Action = MergeAction.Conflict,
        ConflictReason = reason,
    };

    /// <summary>
    /// A fast-forward still has to bring their files onto disk, since the store
    /// holding an object is not the same as the working tree holding the file.
    /// </summary>
    private IReadOnlyList<MergeItem> FastForwardItems(ChunkId ours, ChunkId theirs)
    {
        if (_graph.IsAncestorOf(theirs, ours)) return [];   // we are already ahead

        var ourFiles = Index(ReadManifest(_graph.Load(ours)));
        var theirFiles = Index(ReadManifest(_graph.Load(theirs)));

        var items = new List<MergeItem>();

        foreach (var (path, t) in theirFiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (ourFiles.TryGetValue(path, out var o) && Same(o, t)) continue;
            items.Add(Take(path, t));
        }

        foreach (var path in ourFiles.Keys.Where(p => !theirFiles.ContainsKey(p)).OrderBy(p => p, StringComparer.Ordinal))
            items.Add(new MergeItem { Path = path, Action = MergeAction.Delete });

        return items;
    }

    private static IReadOnlyList<RepoDivergence> CompareRepos(Snapshot ours, Snapshot theirs)
    {
        var o = ours.Repos.ToDictionary(r => r.Path, StringComparer.Ordinal);
        var t = theirs.Repos.ToDictionary(r => r.Path, StringComparer.Ordinal);

        return o.Keys.Concat(t.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new RepoDivergence(p, o.GetValueOrDefault(p), t.GetValueOrDefault(p)))
            .Where(d => d.MissingHere || d.DifferentBranch || d.DifferentHead)
            .ToList();
    }

    private static Dictionary<string, FileEntry> Index(Manifest m)
        => m.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);

    /// <summary>Content is compared by chunk list, never by size or timestamp.</summary>
    /// <remarks>
    /// A rebuild rewrites files byte for byte and moves every timestamp. Comparing
    /// those would report a change on every file a build touched, and a merge full
    /// of files nobody edited trains people to stop reading it.
    /// </remarks>
    private static bool Same(FileEntry a, FileEntry b)
    {
        if (a.Executable != b.Executable) return false;
        if (a.Chunks.Count != b.Chunks.Count) return false;
        for (var i = 0; i < a.Chunks.Count; i++) if (a.Chunks[i] != b.Chunks[i]) return false;
        return true;
    }

    private byte[] Read(FileEntry e)
    {
        var parts = e.Chunks.Select(store.Get).ToList();
        var buffer = new byte[parts.Sum(p => p.Length)];

        var at = 0;
        foreach (var p in parts) { p.CopyTo(buffer, at); at += p.Length; }
        return buffer;
    }

    private Manifest ReadManifest(Snapshot snapshot)
    {
        var parts = snapshot.ManifestChunks.Select(store.Get).ToList();
        var buffer = new byte[parts.Sum(p => p.Length)];

        var at = 0;
        foreach (var p in parts) { p.CopyTo(buffer, at); at += p.Length; }

        var actual = ChunkId.Of(buffer);
        if (actual != snapshot.ManifestId) throw new CorruptObjectException(snapshot.ManifestId, actual);

        return Manifest.Parse(buffer);
    }
}
