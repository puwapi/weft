using System.Text;
using Weft.Core.Store;

namespace Weft.Core.Tests.Store;

public class SnapshotTests
{
    private static ChunkId Cid(string s) => ChunkId.Of(Encoding.UTF8.GetBytes(s));

    private static Snapshot Sample(params RepoState[] repos) => new()
    {
        ManifestId = Cid("manifest"),
        ManifestChunks = [Cid("m1"), Cid("m2")],
        Parents = [Cid("parent")],
        MachineId = "01a04787e8a0789d8a48c8cdf923c080",
        MachineName = "mac-studio",
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_756_000_000_000),
        Repos = repos,
        FileCount = 9113,
        TotalBytes = 179_400_000,
    };

    [Fact]
    public void A_snapshot_survives_a_round_trip()
    {
        var s = Sample(new RepoState("hub", "git@github.com:puwapi/hub.git", "main", "abc123", true, 4));
        var back = Snapshot.Parse(s.Serialise());

        Assert.Equal(s.ManifestId, back.ManifestId);
        Assert.Equal(s.ManifestChunks, back.ManifestChunks);
        Assert.Equal(s.Parents, back.Parents);
        Assert.Equal(s.MachineId, back.MachineId);
        Assert.Equal(s.MachineName, back.MachineName);
        Assert.Equal(s.CreatedUtc, back.CreatedUtc);
        Assert.Equal(s.FileCount, back.FileCount);
        Assert.Equal(s.TotalBytes, back.TotalBytes);
        Assert.Equal(s.Repos, back.Repos);
    }

    [Fact]
    public void The_id_is_the_hash_of_the_snapshot_itself()
    {
        var s = Sample();
        Assert.Equal(ChunkId.Of(s.Serialise()), s.Id());

        // Any field change moves the id, so a snapshot cannot be edited after the
        // fact without becoming a different snapshot.
        Assert.NotEqual(s.Id(), (s with { FileCount = 9114 }).Id());
    }

    [Fact]
    public void The_first_snapshot_has_no_parent_and_a_merge_has_two()
    {
        Assert.Empty(Snapshot.Parse((Sample() with { Parents = [] }).Serialise()).Parents);
        Assert.Equal(2, Snapshot.Parse(
            (Sample() with { Parents = [Cid("a"), Cid("b")] }).Serialise()).Parents.Count);
    }

    [Fact]
    public void A_repo_with_no_remote_and_a_detached_head_round_trips()
    {
        // Both are normal states, and both are empty strings. If they were not
        // preserved they would be indistinguishable from a parse failure.
        var s = Sample(new RepoState("orphan", "", "", "def456", true, 0));
        var back = Assert.Single(Snapshot.Parse(s.Serialise()).Repos);

        Assert.Equal("", back.Remote);
        Assert.Equal("", back.Branch);
    }

    [Fact]
    public void A_linked_worktree_is_recorded_as_such()
    {
        var s = Sample(
            new RepoState("hub", "git@github.com:puwapi/hub.git", "main", "a", true, 0),
            new RepoState(".wt-hub", "git@github.com:puwapi/hub.git", "feat/x", "b", false, 12));

        var back = Snapshot.Parse(s.Serialise()).Repos.OrderBy(r => r.Path, StringComparer.Ordinal).ToList();

        Assert.False(back[0].IsPrimary);       // ".wt-hub"
        Assert.Equal(12, back[0].DirtyFiles);
        Assert.True(back[1].IsPrimary);        // "hub"
    }

    [Fact]
    public void A_key_this_build_does_not_know_is_ignored_rather_than_rejected()
    {
        // Forward compatibility. Refusing an unknown key would strand this
        // machine the moment another one upgrades and writes a field it adds.
        var text = Encoding.UTF8.GetString(Sample().Serialise())
            + "somethingNewer a value from a later version\n";

        var back = Snapshot.Parse(Encoding.UTF8.GetBytes(text));
        Assert.Equal("mac-studio", back.MachineName);
    }

    [Fact]
    public void A_snapshot_with_no_manifest_is_refused()
        => Assert.Throws<InvalidDataException>(
            () => Snapshot.Parse("weft-snapshot 1\nmachine abc\n"u8));

    [Fact]
    public void A_foreign_format_is_refused()
        => Assert.Throws<InvalidDataException>(
            () => Snapshot.Parse("weft-snapshot 99\nmanifest x\n"u8));

    [Fact]
    public void A_machine_name_containing_a_tab_cannot_break_the_format()
    {
        var s = Sample() with { MachineName = "my\tmachine\nwith breaks" };
        var back = Snapshot.Parse(s.Serialise());

        Assert.DoesNotContain('\t', back.MachineName);
        Assert.DoesNotContain('\n', back.MachineName);
    }
}

public class CarriedWorkTests
{
    private static ChunkId Cid(string s) => ChunkId.Of(System.Text.Encoding.UTF8.GetBytes(s));

    private static Snapshot WithCarried(params CarriedWork[] carried) => new()
    {
        ManifestId = Cid("m"),
        ManifestChunks = [Cid("m1")],
        Parents = [],
        MachineId = "id",
        MachineName = "mac-studio",
        CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(1_756_000_000_000),
        Repos = [],
        Carried = carried,
        FileCount = 0,
        TotalBytes = 0,
    };

    [Fact]
    public void Carried_work_survives_a_round_trip()
    {
        var w = new CarriedWork("hub", "abc123def456", "feat/x", [Cid("p1"), Cid("p2")], 4096, 7, 3);
        var back = Assert.Single(Snapshot.Parse(WithCarried(w).Serialise()).Carried);

        Assert.Equal("hub", back.RepoPath);
        Assert.Equal("abc123def456", back.BaseCommit);
        Assert.Equal("feat/x", back.Branch);
        Assert.Equal([Cid("p1"), Cid("p2")], back.PatchChunks);
        Assert.Equal(4096, back.PatchBytes);
        Assert.Equal(7, back.ChangedFiles);
        Assert.Equal(3, back.StagedFiles);
    }

    [Fact]
    public void A_snapshot_carrying_nothing_round_trips()
        => Assert.Empty(Snapshot.Parse(WithCarried().Serialise()).Carried);

    [Fact]
    public void Work_on_a_detached_head_round_trips()
    {
        // An empty branch is a real state, not a parse failure.
        var w = new CarriedWork("hub", "abc", "", [Cid("p")], 1, 1, 0);
        Assert.Equal("", Assert.Single(Snapshot.Parse(WithCarried(w).Serialise()).Carried).Branch);
    }

    [Fact]
    public void A_snapshot_from_an_older_build_that_carries_nothing_still_parses()
    {
        // Forward and backward compatibility: 'carry' lines simply are not there.
        var text = System.Text.Encoding.UTF8.GetString(WithCarried().Serialise());
        Assert.DoesNotContain("carry ", text, StringComparison.Ordinal);
        Assert.Empty(Snapshot.Parse(System.Text.Encoding.UTF8.GetBytes(text)).Carried);
    }
}
