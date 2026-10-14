using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Weft.Server;

namespace Weft.Server.Tests;

public sealed class GarbageCollectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weft-gc-" + Guid.NewGuid().ToString("n"));
    private readonly ServerStore _store;

    public GarbageCollectionTests() => _store = new ServerStore(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private string PathOf(string id) => Path.Combine(_dir, "objects", id[..2], id[2..]);

    /// <summary>Stores an object, backdated so the grace window does not hide it.</summary>
    private async Task<string> Old(string? id = null)
    {
        id ??= NewId();
        await _store.PutObjectAsync(id, new MemoryStream([1, 2, 3]), 1024, default);
        File.SetLastWriteTimeUtc(PathOf(id), DateTime.UtcNow.AddDays(-3));
        return id;
    }

    /// <summary>A full push through a session: every id declared, then the head committed.</summary>
    private async Task<string> Push(string machine, params string[] content)
    {
        var snapshot = await Old();
        foreach (var c in content) if (!_store.HasObject(c)) await Old(c);

        var session = _store.BeginPush(machine);
        Assert.NotNull(_store.AddToSession(machine, session, [snapshot, .. content]));
        Assert.Equal(CommitStatus.Committed, _store.CommitHead(machine, session, snapshot).Status);
        return snapshot;
    }

    private static GcOptions Apply(DateTimeOffset? now = null) => new() { Apply = true, Now = now };

    // ---------- refusing ----------

    [Fact]
    public async Task A_head_set_without_a_session_stops_the_sweep()
    {
        // Unknown is not "needs nothing". A head whose needs nobody declared could
        // point at any object on disk, so the only safe sweep is no sweep.
        _store.Enrol("m1", "laptop", "linux");
        var orphan = await Old();
        var snapshot = await Old();
        _store.SetHead("m1", snapshot);

        var report = _store.CollectGarbage(Apply());

        Assert.True(report.Refused);
        Assert.Equal("m1", Assert.Single(report.Unpinned).Id);
        Assert.True(_store.HasObject(orphan));
    }

    [Fact]
    public async Task Pushing_again_with_a_session_lifts_the_refusal()
    {
        _store.Enrol("m1", "laptop", "linux");
        _store.SetHead("m1", await Old());
        Assert.True(_store.CollectGarbage(new GcOptions()).Refused);

        await Push("m1");

        Assert.False(_store.CollectGarbage(new GcOptions()).Refused);
        Assert.True(_store.Find("m1")!.Pinned);
    }

    // ---------- what goes and what stays ----------

    [Fact]
    public async Task What_no_head_needs_goes_to_trash_and_what_one_needs_stays()
    {
        _store.Enrol("m1", "laptop", "linux");
        var kept = await Old();
        await Push("m1", kept);
        var orphan = await Old();

        var report = _store.CollectGarbage(Apply());

        Assert.Equal(1, report.Candidates);
        Assert.True(_store.HasObject(kept));
        Assert.False(_store.HasObject(orphan));
        Assert.True(File.Exists(Path.Combine(_dir, "trash", report.Stamp!, orphan[..2], orphan[2..])));
    }

    [Fact]
    public async Task A_dry_run_moves_nothing()
    {
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        var orphan = await Old();

        var report = _store.CollectGarbage(new GcOptions());

        Assert.Equal(1, report.Candidates);
        Assert.False(report.Applied);
        Assert.True(_store.HasObject(orphan));
        Assert.False(Directory.Exists(Path.Combine(_dir, "trash")));
    }

    [Fact]
    public async Task A_revoked_machines_head_is_never_collected()
    {
        // Revocation keeps everything recorded. The lost machine's last snapshot
        // is precisely what someone will want back.
        _store.Enrol("m1", "laptop", "linux");
        var content = await Old();
        await Push("m1", content);
        _store.Revoke("m1");

        _store.CollectGarbage(Apply(DateTimeOffset.UtcNow.AddDays(100)));

        Assert.True(_store.HasObject(content));
    }

    [Fact]
    public async Task A_superseded_head_is_kept_for_the_window_and_collected_after()
    {
        _store.Enrol("m1", "laptop", "linux");
        var before = await Old();
        await Push("m1", before);
        await Push("m1");

        _store.CollectGarbage(Apply(DateTimeOffset.UtcNow.AddDays(13)));
        Assert.True(_store.HasObject(before));

        _store.CollectGarbage(Apply(DateTimeOffset.UtcNow.AddDays(15)));
        Assert.False(_store.HasObject(before));
    }

    [Fact]
    public async Task A_head_set_before_history_existed_still_gets_the_window_when_superseded()
    {
        // The upgrade path: the head was set by an old client, so it has no
        // history row. Superseding it must not make it collectable at once.
        _store.Enrol("m1", "laptop", "linux");
        var legacy = await Old();
        _store.SetHead("m1", legacy);

        // What a database written by an older server looks like: a head, no history.
        using (var cx = new SqliteConnection($"Data Source={Path.Combine(_dir, "weft.db")}"))
        {
            cx.Open();
            using var wipe = cx.CreateCommand();
            wipe.CommandText = "DELETE FROM head_history";
            wipe.ExecuteNonQuery();
        }

        await Push("m1");

        var report = _store.CollectGarbage(new GcOptions());

        // It is inside the window but nobody declared what it needs.
        Assert.Equal(1, report.UnretainableHeads);
        Assert.Equal(2, report.LiveHeads);
    }

    [Fact]
    public async Task Objects_shared_by_two_machines_survive_one_of_them_moving_on()
    {
        _store.Enrol("m1", "laptop", "linux");
        _store.Enrol("m2", "desktop", "linux");
        var shared = await Old();
        await Push("m1", shared);
        await Push("m2", shared);
        await Push("m1");

        _store.CollectGarbage(Apply(DateTimeOffset.UtcNow.AddDays(30)));

        Assert.True(_store.HasObject(shared));
    }

    [Fact]
    public async Task Files_not_named_like_objects_are_left_alone()
    {
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        var stray = Path.Combine(_dir, "objects", "ab", "not-an-object");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        await File.WriteAllTextAsync(stray, "?");
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddDays(-30));

        _store.CollectGarbage(Apply());

        Assert.True(File.Exists(stray));
    }

    // ---------- pushes in flight ----------

    [Fact]
    public async Task A_push_told_present_before_a_sweep_lands_after_it()
    {
        // The race this whole design exists to close: the push asks, hears
        // "already there", the sweep runs, and only then does the head land.
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        var reused = await Old();      // nobody pins it any more
        var snapshot = await Old();

        var session = _store.BeginPush("m1");
        var missing = _store.AddToSession("m1", session, [snapshot, reused]);
        Assert.Empty(missing!);

        _store.CollectGarbage(Apply());

        Assert.Equal(CommitStatus.Committed, _store.CommitHead("m1", session, snapshot).Status);
        Assert.True(_store.HasObject(reused));
    }

    [Fact]
    public async Task An_old_client_told_present_keeps_its_objects_through_a_sweep()
    {
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        var reused = await Old();

        Assert.Empty(_store.MissingTouchingPresent([reused]));
        _store.CollectGarbage(Apply());

        Assert.True(_store.HasObject(reused));
    }

    [Fact]
    public async Task An_abandoned_push_stops_protecting_its_objects_once_it_expires()
    {
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        var named = await Old();
        _store.AddToSession("m1", _store.BeginPush("m1"), [named]);

        _store.CollectGarbage(Apply());
        Assert.True(_store.HasObject(named));

        _store.CollectGarbage(Apply(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.False(_store.HasObject(named));
    }

    [Fact]
    public async Task A_recent_unreferenced_object_is_spared()
    {
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        var fresh = NewId();
        await _store.PutObjectAsync(fresh, new MemoryStream([9]), 1024, default);

        var report = _store.CollectGarbage(Apply());

        Assert.Equal(1, report.KeptYoung);
        Assert.True(_store.HasObject(fresh));
    }

    // ---------- committing ----------

    [Fact]
    public async Task A_head_whose_objects_are_not_all_there_is_refused_and_the_pointer_stays()
    {
        _store.Enrol("m1", "laptop", "linux");
        var before = await Push("m1");
        var snapshot = await Old();

        var session = _store.BeginPush("m1");
        _store.AddToSession("m1", session, [snapshot, NewId()]);

        var outcome = _store.CommitHead("m1", session, snapshot);

        Assert.Equal(CommitStatus.ObjectsMissing, outcome.Status);
        Assert.Equal(1, outcome.Missing);
        Assert.Equal(before, _store.Find("m1")!.Head);
    }

    [Fact]
    public async Task A_session_belongs_to_the_machine_that_opened_it()
    {
        _store.Enrol("m1", "laptop", "linux");
        _store.Enrol("m2", "desktop", "linux");
        var snapshot = await Old();
        var session = _store.BeginPush("m1");

        Assert.Null(_store.AddToSession("m2", session, [snapshot]));
        Assert.Equal(CommitStatus.NoSuchSession, _store.CommitHead("m2", session, snapshot).Status);
    }

    [Fact]
    public async Task A_snapshot_the_session_never_named_is_refused()
    {
        _store.Enrol("m1", "laptop", "linux");
        var session = _store.BeginPush("m1");
        _store.AddToSession("m1", session, [await Old()]);

        Assert.Equal(CommitStatus.SnapshotNotInSession, _store.CommitHead("m1", session, await Old()).Status);
    }

    // ---------- trash ----------

    [Fact]
    public async Task An_object_pushed_again_after_quarantine_is_stored_afresh()
    {
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        var orphan = await Old();
        _store.CollectGarbage(Apply());
        Assert.False(_store.HasObject(orphan));

        var snapshot = await Old();
        var session = _store.BeginPush("m1");
        var missing = _store.AddToSession("m1", session, [snapshot, orphan]);
        Assert.Equal([orphan], missing);

        Assert.True(await _store.PutObjectAsync(orphan, new MemoryStream([1, 2, 3]), 1024, default));
        Assert.Equal(CommitStatus.Committed, _store.CommitHead("m1", session, snapshot).Status);
    }

    [Fact]
    public async Task Trash_is_deleted_for_good_only_after_its_window()
    {
        _store.Enrol("m1", "laptop", "linux");
        await Push("m1");
        await Old();
        var first = _store.CollectGarbage(Apply()).Stamp!;
        var stampDir = Path.Combine(_dir, "trash", first);

        Assert.Equal(0, _store.CollectGarbage(Apply(DateTimeOffset.UtcNow.AddDays(6))).PurgedStamps);
        Assert.True(Directory.Exists(stampDir));

        Assert.Equal(1, _store.CollectGarbage(Apply(DateTimeOffset.UtcNow.AddDays(8))).PurgedStamps);
        Assert.False(Directory.Exists(stampDir));
    }
}
