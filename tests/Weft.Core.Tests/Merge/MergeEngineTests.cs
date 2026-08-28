using System.Text;
using Weft.Core.Merge;
using Weft.Core.Store;

namespace Weft.Core.Tests.Merge;

public sealed class MergeEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weft-merge-" + Guid.NewGuid().ToString("n"));
    private readonly ObjectStore _store;
    private readonly MergeEngine _engine;

    public MergeEngineTests()
    {
        _store = new ObjectStore(_dir);
        _engine = new MergeEngine(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Builds a snapshot holding these files, descending from these parents.</summary>
    private ChunkId Snap(Dictionary<string, string> files, params ChunkId[] parents)
        => Snap(files, [], parents);

    private ChunkId Snap(Dictionary<string, string> files, IReadOnlyList<RepoState> repos, params ChunkId[] parents)
    {
        var entries = files.Select(kv =>
        {
            var bytes = Encoding.UTF8.GetBytes(kv.Value);
            var chunks = FastCdc.Split(bytes)
                .Select(c => _store.Put(bytes.AsSpan(c.Offset, c.Length)))
                .ToList();
            return new FileEntry(kv.Key, bytes.Length, DateTimeOffset.UnixEpoch, false, chunks);
        }).ToList();

        var manifest = new Manifest(entries);
        var manifestBytes = manifest.Serialise();
        var manifestChunks = FastCdc.Split(manifestBytes)
            .Select(c => _store.Put(manifestBytes.AsSpan(c.Offset, c.Length)))
            .ToList();

        var snapshot = new Snapshot
        {
            ManifestId = ChunkId.Of(manifestBytes),
            ManifestChunks = manifestChunks,
            Parents = parents,
            MachineId = "test",
            MachineName = "test",

            // Distinct per snapshot so two snapshots holding the same files still
            // get different ids, as they would in life.
            CreatedUtc = DateTimeOffset.UnixEpoch.AddSeconds(_clock++),
            Repos = repos,
            FileCount = entries.Count,
            TotalBytes = entries.Sum(e => e.Size),
        };

        return _store.Put(snapshot.Serialise());
    }

    private int _clock;

    private static Dictionary<string, string> Files(params (string Path, string Content)[] files)
        => files.ToDictionary(f => f.Path, f => f.Content);

    private static MergeItem ItemFor(MergeOutcome o, string path) => Assert.Single(o.Items, i => i.Path == path);
    private static string Text(MergeItem i) => Encoding.UTF8.GetString(i.Content ?? []);

    // ================= the table, row by row =================

    [Fact]
    public void Nobody_touched_it()
    {
        var b = Snap(Files(("a.txt", "same\n")));
        var o = Snap(Files(("a.txt", "same\n")), b);
        var t = Snap(Files(("a.txt", "same\n")), b);

        Assert.Equal(MergeAction.Unchanged, ItemFor(_engine.Compute(o, t), "a.txt").Action);
    }

    [Fact]
    public void We_added_it()
    {
        var b = Snap(Files(("keep.txt", "x\n")));
        var o = Snap(Files(("keep.txt", "x\n"), ("new.txt", "ours\n")), b);
        var t = Snap(Files(("keep.txt", "x\n")), b);

        Assert.Equal(MergeAction.KeepOurs, ItemFor(_engine.Compute(o, t), "new.txt").Action);
    }

    [Fact]
    public void They_added_it()
    {
        var b = Snap(Files(("keep.txt", "x\n")));
        var o = Snap(Files(("keep.txt", "x\n")), b);
        var t = Snap(Files(("keep.txt", "x\n"), ("new.txt", "theirs\n")), b);

        var i = ItemFor(_engine.Compute(o, t), "new.txt");
        Assert.Equal(MergeAction.TakeTheirs, i.Action);
        Assert.Equal("theirs\n", Text(i));
    }

    [Fact]
    public void Both_added_the_same_file()
    {
        var b = Snap(Files(("keep.txt", "x\n")));
        var o = Snap(Files(("keep.txt", "x\n"), ("new.txt", "identical\n")), b);
        var t = Snap(Files(("keep.txt", "x\n"), ("new.txt", "identical\n")), b);

        Assert.Equal(MergeAction.Unchanged, ItemFor(_engine.Compute(o, t), "new.txt").Action);
    }

    [Fact]
    public void Both_added_it_with_different_content()
    {
        // No ancestor for this path, so the content merge runs against an empty
        // base. Anything the two happen to share still merges.
        var b = Snap(Files(("keep.txt", "x\n")));
        var o = Snap(Files(("keep.txt", "x\n"), ("new.txt", "ours\n")), b);
        var t = Snap(Files(("keep.txt", "x\n"), ("new.txt", "theirs\n")), b);

        Assert.Equal(MergeAction.Conflict, ItemFor(_engine.Compute(o, t), "new.txt").Action);
    }

    [Fact]
    public void Only_we_changed_it()
    {
        var b = Snap(Files(("a.txt", "base\n")));
        var o = Snap(Files(("a.txt", "ours\n")), b);
        var t = Snap(Files(("a.txt", "base\n")), b);

        Assert.Equal(MergeAction.KeepOurs, ItemFor(_engine.Compute(o, t), "a.txt").Action);
    }

    [Fact]
    public void Only_they_changed_it()
    {
        var b = Snap(Files(("a.txt", "base\n")));
        var o = Snap(Files(("a.txt", "base\n")), b);
        var t = Snap(Files(("a.txt", "theirs\n")), b);

        var i = ItemFor(_engine.Compute(o, t), "a.txt");
        Assert.Equal(MergeAction.TakeTheirs, i.Action);
        Assert.Equal("theirs\n", Text(i));
    }

    [Fact]
    public void Both_made_the_same_change()
    {
        var b = Snap(Files(("a.txt", "base\n")));
        var o = Snap(Files(("a.txt", "fixed\n")), b);
        var t = Snap(Files(("a.txt", "fixed\n")), b);

        Assert.Equal(MergeAction.Unchanged, ItemFor(_engine.Compute(o, t), "a.txt").Action);
    }

    [Fact]
    public void Both_changed_it_in_different_places()
    {
        var b = Snap(Files(("a.txt", "one\ntwo\nthree\nfour\nfive\n")));
        var o = Snap(Files(("a.txt", "ONE\ntwo\nthree\nfour\nfive\n")), b);
        var t = Snap(Files(("a.txt", "one\ntwo\nthree\nfour\nFIVE\n")), b);

        var i = ItemFor(_engine.Compute(o, t), "a.txt");
        Assert.Equal(MergeAction.Write, i.Action);
        Assert.Equal("ONE\ntwo\nthree\nfour\nFIVE\n", Text(i));
    }

    [Fact]
    public void Both_changed_the_same_line()
    {
        var b = Snap(Files(("a.txt", "a\nb\nc\n")));
        var o = Snap(Files(("a.txt", "a\nOURS\nc\n")), b);
        var t = Snap(Files(("a.txt", "a\nTHEIRS\nc\n")), b);

        var i = ItemFor(_engine.Compute(o, t), "a.txt");
        Assert.Equal(MergeAction.Conflict, i.Action);
        Assert.Null(i.Content);   // nothing is proposed for a conflict
        Assert.NotEmpty(i.Regions);
    }

    [Fact]
    public void Both_deleted_it()
    {
        var b = Snap(Files(("gone.txt", "x\n"), ("keep.txt", "y\n")));
        var o = Snap(Files(("keep.txt", "y\n")), b);
        var t = Snap(Files(("keep.txt", "y\n")), b);

        Assert.Equal(MergeAction.Delete, ItemFor(_engine.Compute(o, t), "gone.txt").Action);
    }

    [Fact]
    public void We_deleted_it_and_they_left_it_alone()
    {
        var b = Snap(Files(("gone.txt", "x\n")));
        var o = Snap(Files(), b);
        var t = Snap(Files(("gone.txt", "x\n")), b);

        Assert.Equal(MergeAction.Delete, ItemFor(_engine.Compute(o, t), "gone.txt").Action);
    }

    [Fact]
    public void They_deleted_it_and_we_left_it_alone()
    {
        var b = Snap(Files(("gone.txt", "x\n")));
        var o = Snap(Files(("gone.txt", "x\n")), b);
        var t = Snap(Files(), b);

        Assert.Equal(MergeAction.Delete, ItemFor(_engine.Compute(o, t), "gone.txt").Action);
    }

    [Fact]
    public void We_deleted_it_and_they_changed_it()
    {
        // Not resolvable by any rule. Keeping the file overrides a deliberate
        // deletion; dropping it discards work. A person decides.
        var b = Snap(Files(("f.txt", "base\n")));
        var o = Snap(Files(), b);
        var t = Snap(Files(("f.txt", "changed\n")), b);

        var i = ItemFor(_engine.Compute(o, t), "f.txt");
        Assert.Equal(MergeAction.Conflict, i.Action);
        Assert.Contains("deleted", i.ConflictReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void They_deleted_it_and_we_changed_it()
    {
        var b = Snap(Files(("f.txt", "base\n")));
        var o = Snap(Files(("f.txt", "changed\n")), b);
        var t = Snap(Files(), b);

        Assert.Equal(MergeAction.Conflict, ItemFor(_engine.Compute(o, t), "f.txt").Action);
    }

    // ================= ancestry =================

    [Fact]
    public void The_nearest_common_ancestor_is_used()
    {
        var root = Snap(Files(("a.txt", "v1\n")));
        var mid = Snap(Files(("a.txt", "v2\n")), root);
        var o = Snap(Files(("a.txt", "v2\nours\n")), mid);
        var t = Snap(Files(("a.txt", "v2\ntheirs\n")), mid);

        Assert.Equal(mid, _engine.Compute(o, t).Base);
    }

    [Fact]
    public void One_side_already_containing_the_other_is_a_fast_forward()
    {
        var b = Snap(Files(("a.txt", "v1\n")));
        var ahead = Snap(Files(("a.txt", "v2\n")), b);

        var forward = _engine.Compute(b, ahead);
        Assert.True(forward.FastForward);
        Assert.Equal("v2\n", Text(ItemFor(forward, "a.txt")));

        // The other direction has nothing to do at all.
        var behind = _engine.Compute(ahead, b);
        Assert.True(behind.FastForward);
        Assert.Empty(behind.Items);
    }

    [Fact]
    public void Machines_that_never_met_have_no_common_ancestor_and_still_merge()
    {
        // A workspace set up separately on each machine and only then pointed at
        // the same server. Everything reads as "added on one side", which is
        // right, and nothing is lost.
        var o = Snap(Files(("ours.txt", "a\n")));
        var t = Snap(Files(("theirs.txt", "b\n")));

        var outcome = _engine.Compute(o, t);

        Assert.Null(outcome.Base);
        Assert.Equal(MergeAction.KeepOurs, ItemFor(outcome, "ours.txt").Action);
        Assert.Equal(MergeAction.TakeTheirs, ItemFor(outcome, "theirs.txt").Action);
        Assert.True(outcome.Clean);
    }

    // ================= repositories =================

    [Fact]
    public void Two_machines_on_two_branches_is_reported_and_never_acted_on()
    {
        // The rule the whole design turns on: repository state is not content.
        // Merging branch names would be actively wrong.
        var b = Snap(Files(("a.txt", "x\n")));
        var o = Snap(Files(("a.txt", "x\n")),
            [new RepoState("hub", "git@github.com:x/hub.git", "main", "aaa", true, 0)], b);
        var t = Snap(Files(("a.txt", "x\n")),
            [new RepoState("hub", "git@github.com:x/hub.git", "feat/x", "bbb", true, 3)], b);

        var outcome = _engine.Compute(o, t);

        var d = Assert.Single(outcome.Repos);
        Assert.True(d.DifferentBranch);
        Assert.False(d.MissingHere);

        // Nothing about the repository turns into an action.
        Assert.DoesNotContain(outcome.Items, i => i.Path.StartsWith("hub", StringComparison.Ordinal));
    }

    [Fact]
    public void A_repository_we_do_not_have_is_surfaced()
    {
        var b = Snap(Files(("a.txt", "x\n")));
        var o = Snap(Files(("a.txt", "x\n")), [], b);
        var t = Snap(Files(("a.txt", "x\n")),
            [new RepoState("newmodule", "git@github.com:x/newmodule.git", "main", "ccc", true, 0)], b);

        var d = Assert.Single(_engine.Compute(o, t).Repos);
        Assert.True(d.MissingHere);
        Assert.Equal("git@github.com:x/newmodule.git", d.Theirs!.Remote);
    }

    // ================= properties =================

    [Fact]
    public void A_conflict_never_carries_content_to_write()
    {
        // The invariant the whole plan-then-apply split exists to guarantee.
        var b = Snap(Files(("a.txt", "a\nb\nc\n"), ("bin.dat", "x\0y\n")));
        var o = Snap(Files(("a.txt", "a\nOURS\nc\n"), ("bin.dat", "x\0OURS\n")), b);
        var t = Snap(Files(("a.txt", "a\nTHEIRS\nc\n"), ("bin.dat", "x\0THEIRS\n")), b);

        var outcome = _engine.Compute(o, t);
        Assert.Equal(2, outcome.Conflicts.Count());

        foreach (var c in outcome.Conflicts)
        {
            Assert.Null(c.Content);
            Assert.NotNull(c.ConflictReason);
        }
    }

    [Fact]
    public void Merging_is_symmetric_in_what_it_finds()
    {
        // Which machine asks must not change whether something is a conflict, or
        // the two would never agree on when they are done.
        var b = Snap(Files(("clean.txt", "1\n2\n3\n4\n5\n"), ("clash.txt", "a\nb\nc\n")));
        var o = Snap(Files(("clean.txt", "X\n2\n3\n4\n5\n"), ("clash.txt", "a\nOURS\nc\n")), b);
        var t = Snap(Files(("clean.txt", "1\n2\n3\n4\nY\n"), ("clash.txt", "a\nTHEIRS\nc\n")), b);

        var forward = _engine.Compute(o, t);
        var backward = _engine.Compute(t, o);

        Assert.Equal(
            forward.Conflicts.Select(i => i.Path).OrderBy(p => p, StringComparer.Ordinal),
            backward.Conflicts.Select(i => i.Path).OrderBy(p => p, StringComparer.Ordinal));

        Assert.Equal(forward.Base, backward.Base);
    }

    [Fact]
    public void A_resolved_conflict_does_not_come_back()
    {
        // The property that separates a merge engine from a tool that asks the
        // same question every day. Recording the merge with BOTH heads as parents
        // makes it the new common ancestor, so the disagreement is settled rather
        // than merely answered once.
        var b = Snap(Files(("a.txt", "a\nb\nc\n")));
        var o = Snap(Files(("a.txt", "a\nOURS\nc\n")), b);
        var t = Snap(Files(("a.txt", "a\nTHEIRS\nc\n")), b);

        Assert.Equal(MergeAction.Conflict, ItemFor(_engine.Compute(o, t), "a.txt").Action);

        // Someone settles it and the merge is recorded against both heads.
        var resolved = Snap(Files(("a.txt", "a\nBOTH\nc\n")), o, t);

        // The other machine now merges that resolution.
        var second = _engine.Compute(t, resolved);
        Assert.True(second.FastForward);
        Assert.True(second.Clean);

        // And a third round has nothing left to do at all.
        Assert.Empty(_engine.Compute(resolved, t).Items);
    }

    [Fact]
    public void Without_a_merge_snapshot_the_same_conflict_WOULD_come_back()
    {
        // Shows what the two-parent snapshot buys, by leaving it out. The
        // resolution is recorded against one head only, so the merge base is
        // still the original ancestor and the two sides look as divergent as
        // before.
        var b = Snap(Files(("a.txt", "a\nb\nc\n")));
        var o = Snap(Files(("a.txt", "a\nOURS\nc\n")), b);
        var t = Snap(Files(("a.txt", "a\nTHEIRS\nc\n")), b);

        var resolvedBadly = Snap(Files(("a.txt", "a\nBOTH\nc\n")), o);   // one parent only

        var again = _engine.Compute(resolvedBadly, t);
        Assert.Equal(b, again.Base);
        Assert.Equal(MergeAction.Conflict, ItemFor(again, "a.txt").Action);
    }

    [Fact]
    public void Every_path_present_anywhere_gets_exactly_one_decision()
    {
        // Nothing may be quietly skipped: a path with no decision is a file that
        // silently stays whatever it happened to be.
        var b = Snap(Files(("base-only.txt", "1\n"), ("all.txt", "2\n")));
        var o = Snap(Files(("all.txt", "2\n"), ("ours-only.txt", "3\n")), b);
        var t = Snap(Files(("all.txt", "2\n"), ("theirs-only.txt", "4\n")), b);

        var outcome = _engine.Compute(o, t);
        var decided = outcome.Items.Select(i => i.Path).ToList();

        Assert.Equal(decided.Count, decided.Distinct(StringComparer.Ordinal).Count());
        foreach (var p in new[] { "base-only.txt", "all.txt", "ours-only.txt", "theirs-only.txt" })
            Assert.Contains(p, decided);
    }
}
