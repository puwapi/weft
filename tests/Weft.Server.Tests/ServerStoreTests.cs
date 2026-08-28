using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Weft.Server;

namespace Weft.Server.Tests;

public sealed class ServerStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "weft-srv-" + Guid.NewGuid().ToString("n"));
    private readonly ServerStore _store;

    public ServerStoreTests() => _store = new ServerStore(_dir);

    public void Dispose()
    {
        // The pool keeps a handle on the database file. On Unix that does not stop
        // a delete; on Windows it does, and the temp directory would leak from
        // every test in the class.
        SqliteConnection.ClearAllPools();

        // Both exception types: Windows raises UnauthorizedAccessException on a
        // read-only file where Unix raises nothing at all, so catching only
        // IOException leaves a directory per test behind on one platform.
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static Stream Body(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));
    private const long Big = 1024 * 1024;

    // ---------- machines ----------

    [Fact]
    public void An_enrolled_machine_can_authenticate_with_its_token()
    {
        var (machine, token) = _store.Enrol("m1", "laptop", "linux");

        var back = _store.Authenticate(token);
        Assert.NotNull(back);
        Assert.Equal(machine.Id, back.Id);
        Assert.Equal("laptop", back.Name);
    }

    [Fact]
    public void A_revoked_machine_can_no_longer_authenticate()
    {
        var (_, token) = _store.Enrol("m1", "laptop", "linux");
        Assert.NotNull(_store.Authenticate(token));

        Assert.True(_store.Revoke("m1"));
        Assert.Null(_store.Authenticate(token));
    }

    [Fact]
    public void Revoking_keeps_the_machine_and_everything_it_recorded()
    {
        // The machine being revoked is usually the one that was lost, which is
        // exactly when the work it pushed matters most. Dropping the row would
        // hide its last snapshot from every other machine.
        _store.Enrol("m1", "laptop", "linux");
        _store.SetHead("m1", new string('a', 64));

        _store.Revoke("m1");

        var m = _store.Find("m1");
        Assert.NotNull(m);
        Assert.Equal(new string('a', 64), m.Head);
        Assert.NotNull(m.RevokedUtc);
        Assert.Contains(_store.AllMachines(), x => x.Id == "m1");
    }

    [Fact]
    public void Revoking_one_machine_leaves_the_others_alone()
    {
        var (_, keep) = _store.Enrol("m1", "desktop", "linux");
        var (_, lose) = _store.Enrol("m2", "laptop", "macos");

        _store.Revoke("m2");

        Assert.NotNull(_store.Authenticate(keep));
        Assert.Null(_store.Authenticate(lose));
    }

    [Fact]
    public void Revoking_twice_reports_that_nothing_changed()
    {
        // The caller turns this into a 404, so a typo in a machine id says so
        // rather than reporting a revocation that never happened.
        _store.Enrol("m1", "laptop", "linux");

        Assert.True(_store.Revoke("m1"));
        Assert.False(_store.Revoke("m1"));
        Assert.False(_store.Revoke("never-enrolled"));
    }

    [Fact]
    public void The_dead_token_hash_is_not_the_hash_of_any_token()
    {
        // Revocation replaces the stored hash rather than nulling it. If it left
        // anything derivable behind, the old token would still resolve.
        var (_, token) = _store.Enrol("m1", "laptop", "linux");
        _store.Revoke("m1");

        using var cx = new SqliteConnection($"Data Source={Path.Combine(_dir, "weft.db")}");
        cx.Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = "SELECT token_hash FROM machines WHERE id = 'm1'";
        var stored = (byte[])cmd.ExecuteScalar()!;

        var real = SHA256.HashData(Encoding.UTF8.GetBytes("weft/token/v1/" + token));
        Assert.NotEqual(real, stored);
        Assert.Equal(32, stored.Length);
    }

    [Fact]
    public void Re_enrolling_lifts_a_revocation()
    {
        // It takes the join secret to reach Enrol, which is the operator's own
        // credential, and this is the only way back from revoking by mistake.
        _store.Enrol("m1", "laptop", "linux");
        _store.Revoke("m1");

        var (_, fresh) = _store.Enrol("m1", "laptop", "linux");

        Assert.NotNull(_store.Authenticate(fresh));
        Assert.Null(_store.Find("m1")!.RevokedUtc);
    }

    [Fact]
    public void Re_enrolling_does_not_bring_the_revoked_token_back()
    {
        // The point of the previous test is recovery; the point of this one is
        // that recovery does not undo the revocation. Whoever holds the old token
        // must not get back in because the owner re-enrolled the same machine.
        var (_, stolen) = _store.Enrol("m1", "laptop", "linux");
        _store.Revoke("m1");

        var (_, fresh) = _store.Enrol("m1", "laptop", "linux");

        Assert.NotNull(_store.Authenticate(fresh));
        Assert.Null(_store.Authenticate(stolen));
    }

    [Fact]
    public void A_store_opened_twice_does_not_fail_on_the_added_column()
    {
        // SQLite has no 'ADD COLUMN IF NOT EXISTS'. Running the ALTER blindly
        // throws on every start after the first, which is every restart.
        _store.Enrol("m1", "laptop", "linux");

        var again = new ServerStore(_dir);
        Assert.NotNull(again.Find("m1"));
    }

    [Fact]
    public void An_unknown_token_authenticates_nobody()
        => Assert.Null(_store.Authenticate("wsk_never_issued"));

    [Fact]
    public void The_token_is_not_stored_in_a_form_that_could_be_read_back()
    {
        // A leaked database must not hand over working credentials for every
        // machine on the workspace.
        var (_, token) = _store.Enrol("m1", "laptop", "linux");

        // Opened sharing the file rather than with File.ReadAllText. SQLite holds
        // it open, and Windows refuses a second exclusive handle where Unix does
        // not: reading it the easy way passes everywhere except the one platform
        // where it matters.
        using var stream = new FileStream(
            Path.Combine(_dir, "weft.db"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.Latin1);
        var db = reader.ReadToEnd();

        Assert.DoesNotContain(token, db, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_enrolling_replaces_the_token_and_keeps_the_identity()
    {
        // Recovering from a lost token must not cost a machine its identity: a
        // new id would orphan everything it had already pushed.
        var (first, tokenA) = _store.Enrol("m1", "laptop", "linux");
        var (second, tokenB) = _store.Enrol("m1", "laptop-renamed", "linux");

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(tokenA, tokenB);
        Assert.Null(_store.Authenticate(tokenA));
        Assert.NotNull(_store.Authenticate(tokenB));
        Assert.Single(_store.AllMachines());
    }

    [Fact]
    public void Two_machines_hold_separate_pointers()
    {
        _store.Enrol("m1", "a", "linux");
        _store.Enrol("m2", "b", "mac");

        _store.SetHead("m1", new string('a', 64));

        Assert.Equal(new string('a', 64), _store.Find("m1")!.Head);
        Assert.Null(_store.Find("m2")!.Head);
    }

    // ---------- workspace fingerprint ----------

    [Fact]
    public void The_first_fingerprint_is_claimed_and_a_different_one_refused()
    {
        // Catches a machine holding the wrong key at enrolment, rather than after
        // it has uploaded objects nobody else can decrypt.
        Assert.True(_store.ClaimOrMatchFingerprint("aaaaaaaaaaaa"));
        Assert.True(_store.ClaimOrMatchFingerprint("aaaaaaaaaaaa"));
        Assert.False(_store.ClaimOrMatchFingerprint("bbbbbbbbbbbb"));
        Assert.Equal("aaaaaaaaaaaa", _store.Fingerprint());
    }

    // ---------- objects ----------

    [Fact]
    public async Task An_object_round_trips()
    {
        var id = new string('a', 64);
        Assert.True(await _store.PutObjectAsync(id, Body("some blob"), Big, default));

        using var r = _store.OpenObject(id);
        Assert.NotNull(r);
        Assert.Equal("some blob", new StreamReader(r).ReadToEnd());
    }

    [Fact]
    public async Task An_existing_object_is_never_replaced()
    {
        // The server holds ciphertext and cannot check that a blob matches the
        // name it is filed under. Allowing a rewrite would let any enrolled
        // machine swap content every other machine depends on.
        var id = new string('b', 64);
        await _store.PutObjectAsync(id, Body("original"), Big, default);

        Assert.False(await _store.PutObjectAsync(id, Body("replacement"), Big, default));

        using var r = _store.OpenObject(id)!;
        Assert.Equal("original", new StreamReader(r).ReadToEnd());
    }

    [Fact]
    public async Task An_oversized_object_is_refused_and_leaves_nothing_behind()
    {
        var id = new string('c', 64);
        var payload = new string('x', 5000);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.PutObjectAsync(id, Body(payload), maxBytes: 100, default));

        Assert.False(_store.HasObject(id));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tmp")));
    }

    [Fact]
    public void An_object_that_was_never_stored_reads_as_absent()
    {
        Assert.False(_store.HasObject(new string('d', 64)));
        Assert.Null(_store.OpenObject(new string('d', 64)));
    }

    [Fact]
    public async Task Concurrent_writers_of_the_same_name_all_complete()
    {
        // Two machines pushing the same content at once is the normal case.
        var id = new string('e', 64);

        var results = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => _store.PutObjectAsync(id, Body("contended"), Big, default))));

        Assert.Equal(1, results.Count(written => written));
        Assert.True(_store.HasObject(id));
    }

    [Fact]
    public async Task Objects_are_sharded_across_directories()
    {
        for (var i = 0; i < 120; i++)
            await _store.PutObjectAsync(i.ToString("x2").PadLeft(2, '0') + new string('f', 62),
                Body($"blob {i}"), Big, default);

        Assert.True(Directory.GetDirectories(Path.Combine(_dir, "objects")).Length > 20);
    }

    [Fact]
    public void A_store_reopened_on_the_same_directory_keeps_everything()
    {
        _store.Enrol("m1", "laptop", "linux");
        _store.SetHead("m1", new string('a', 64));

        var reopened = new ServerStore(_dir);
        Assert.Equal(new string('a', 64), reopened.Find("m1")!.Head);
    }
}
