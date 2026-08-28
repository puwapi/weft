using System.Text;
using Weft.Core.Store;

namespace Weft.Core.Tests.Store;

/// <summary>
/// Guards on what a push sends and a pull fetches.
/// </summary>
/// <remarks>
/// This set used to be computed twice, once on each side. Carried patches were
/// added to the snapshot and neither copy learned about them: both sides
/// succeeded, the snapshot looked complete because it NAMED its patches, and
/// landing on the other machine failed looking for an object nobody had sent.
/// </remarks>
public class ReachabilityTests
{
    private static ChunkId Cid(string s) => ChunkId.Of(Encoding.UTF8.GetBytes(s));

    private static Snapshot Sample(IReadOnlyList<CarriedWork>? carried = null) => new()
    {
        ManifestId = Cid("manifest"),
        ManifestChunks = [Cid("m1"), Cid("m2")],
        Parents = [Cid("parent")],
        MachineId = "m",
        MachineName = "m",
        CreatedUtc = DateTimeOffset.UnixEpoch,
        Repos = [],
        Carried = carried ?? [],
        FileCount = 1,
        TotalBytes = 1,
    };

    private static Manifest Files(params string[] chunks) =>
        new([new FileEntry("a.txt", 1, DateTimeOffset.UnixEpoch, false, chunks.Select(Cid).ToList())]);

    [Fact]
    public void The_snapshot_its_manifest_and_its_files_are_all_reachable()
    {
        var id = Cid("snapshot");
        var reachable = Reachability.ChunksOf(id, Sample(), Files("f1", "f2"));

        Assert.Contains(id, reachable);
        Assert.Contains(Cid("m1"), reachable);
        Assert.Contains(Cid("m2"), reachable);
        Assert.Contains(Cid("f1"), reachable);
        Assert.Contains(Cid("f2"), reachable);
    }

    [Fact]
    public void Carried_patches_are_reachable()
    {
        // The regression. A snapshot names its patches, so it looks complete
        // without them, and the failure only shows up on another machine.
        var carried = new CarriedWork("proj", "abc123", "main", [Cid("p1"), Cid("p2")], 100, 3, 1);
        var reachable = Reachability.ChunksOf(Cid("snapshot"), Sample([carried]), Files("f1"));

        Assert.Contains(Cid("p1"), reachable);
        Assert.Contains(Cid("p2"), reachable);
    }

    [Fact]
    public void Every_chunk_a_snapshot_names_anywhere_is_reachable()
    {
        // Stated as a property rather than as a list, so a field added to Snapshot
        // that carries chunks and is forgotten here fails this test rather than
        // failing on somebody else's machine.
        var carried = new CarriedWork("proj", "abc", "main", [Cid("p1")], 10, 1, 0);
        var snapshot = Sample([carried]);
        var manifest = Files("f1", "f2");

        var named = new HashSet<ChunkId>(snapshot.ManifestChunks);
        foreach (var c in manifest.Entries.SelectMany(e => e.Chunks)) named.Add(c);
        foreach (var c in snapshot.Carried.SelectMany(w => w.PatchChunks)) named.Add(c);

        var reachable = Reachability.ChunksOf(Cid("snapshot"), snapshot, manifest);

        Assert.All(named, c => Assert.Contains(c, reachable));
    }

    [Fact]
    public void Parents_are_deliberately_not_followed()
    {
        // A push sends what this snapshot needs to be readable, not the whole
        // history. Following the chain would make every push cost proportional to
        // the workspace's age.
        Assert.DoesNotContain(Cid("parent"), Reachability.ChunksOf(Cid("snapshot"), Sample(), Files("f1")));
    }
}
