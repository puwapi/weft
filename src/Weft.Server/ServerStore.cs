using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Weft.Core.Protocol;

namespace Weft.Server;

/// <summary>A machine the server knows about.</summary>
public sealed record Machine(
    string Id, string Name, string Platform,
    DateTimeOffset EnrolledUtc, DateTimeOffset LastSeenUtc,
    string? Head, DateTimeOffset? HeadUpdatedUtc,
    DateTimeOffset? RevokedUtc = null);

/// <summary>
/// Everything the server keeps: a small metadata database, and opaque blobs.
/// </summary>
/// <remarks>
/// <para>SQLite and the filesystem rather than a database server. weft is meant to
/// be self-hosted by whoever runs it, and requiring a Postgres instance for a
/// handful of machines and some blobs turns a five-minute deployment into an
/// afternoon. The workload is a few rows and a lot of small immutable files,
/// which is exactly what this is good at.</para>
///
/// <para>The server never holds the workspace key, so every blob here is opaque
/// to it. It cannot verify that a blob matches the name it was filed under, which
/// is why objects are immutable: the first writer of a name wins and no later
/// request can replace it. A client that receives a wrong object detects it on
/// decryption, because the name is authenticated and the content re-hashed.</para>
/// </remarks>
public sealed class ServerStore
{
    private readonly string _connectionString;
    private readonly string _objectsDir;
    private readonly string _tempDir;

    public ServerStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _objectsDir = Path.Combine(dataDir, "objects");
        _tempDir = Path.Combine(dataDir, "tmp");
        Directory.CreateDirectory(_objectsDir);
        Directory.CreateDirectory(_tempDir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDir, "weft.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();

        Migrate();
    }

    private SqliteConnection Open()
    {
        var cx = new SqliteConnection(_connectionString);
        cx.Open();

        using var pragma = cx.CreateCommand();
        // WAL lets reads proceed while a write is in flight, which matters as soon
        // as two machines push at once. busy_timeout turns the resulting lock
        // contention into a short wait instead of an immediate error.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();

        return cx;
    }

    private void Migrate()
    {
        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS machines (
              id               TEXT PRIMARY KEY,
              name             TEXT NOT NULL,
              platform         TEXT NOT NULL,
              token_hash       BLOB NOT NULL,
              enrolled_utc     INTEGER NOT NULL,
              last_seen_utc    INTEGER NOT NULL,
              head             TEXT,
              head_updated_utc INTEGER,
              revoked_utc      INTEGER
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_machines_token ON machines(token_hash);

            CREATE TABLE IF NOT EXISTS workspace (
              key         TEXT PRIMARY KEY,
              value       TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        AddColumnIfMissing(cx, "machines", "revoked_utc", "INTEGER");
    }

    /// <summary>Adds a column to a table that predates it.</summary>
    /// <remarks>
    /// SQLite has no 'ADD COLUMN IF NOT EXISTS', and running the ALTER blindly
    /// throws on every start after the first. Asking the schema first is the only
    /// way this stays idempotent, which is what a server that restarts needs.
    /// </remarks>
    private static void AddColumnIfMissing(SqliteConnection cx, string table, string column, string type)
    {
        using var probe = cx.CreateCommand();
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        probe.Parameters.AddWithValue("$c", column);
        if (Convert.ToInt64(probe.ExecuteScalar()) > 0) return;

        using var alter = cx.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    // ---------- machines ----------

    /// <summary>
    /// Registers a machine, or returns the existing one with a fresh token.
    /// </summary>
    /// <remarks>
    /// Re-enrolling an existing machine id replaces its token rather than
    /// creating a duplicate. That is what makes recovery from a lost token
    /// possible without the machine losing its identity and orphaning everything
    /// it has already pushed.
    /// </remarks>
    public (Machine Machine, string Token) Enrol(string id, string name, string platform)
    {
        var token = "wsk_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var now = DateTimeOffset.UtcNow;

        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = """
            INSERT INTO machines (id, name, platform, token_hash, enrolled_utc, last_seen_utc)
            VALUES ($id, $name, $platform, $hash, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
              name = $name, platform = $platform, token_hash = $hash, last_seen_utc = $now,
              -- Re-enrolling lifts a revocation, because it took the join secret
              -- to get here and that is the operator's own credential. It is also
              -- the only way back for a machine revoked by mistake.
              revoked_utc = NULL;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$platform", platform);
        cmd.Parameters.AddWithValue("$hash", HashToken(token));
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();

        return (Find(id)!, token);
    }

    /// <summary>Resolves a bearer token, and records that the machine was seen.</summary>
    public Machine? Authenticate(string token)
    {
        using var cx = Open();

        using var find = cx.CreateCommand();
        // The revoked check is belt and braces: revocation already replaces the
        // hash with bytes no token maps to, so no lookup can match. Stating it in
        // the query as well means a future change to how revocation is stored
        // cannot quietly turn every revoked machine back on.
        find.CommandText = "SELECT id FROM machines WHERE token_hash = $hash AND revoked_utc IS NULL";
        find.Parameters.AddWithValue("$hash", HashToken(token));

        // Looked up by hash rather than compared row by row: the index does the
        // work, and there is no loop whose duration depends on the secret.
        if (find.ExecuteScalar() is not string id) return null;

        using var touch = cx.CreateCommand();
        touch.CommandText = "UPDATE machines SET last_seen_utc = $now WHERE id = $id";
        touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        touch.Parameters.AddWithValue("$id", id);
        touch.ExecuteNonQuery();

        return Find(id);
    }

    public Machine? Find(string id)
    {
        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, platform, enrolled_utc, last_seen_utc, head, head_updated_utc, revoked_utc
            FROM machines WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);

        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public IReadOnlyList<Machine> AllMachines()
    {
        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, platform, enrolled_utc, last_seen_utc, head, head_updated_utc, revoked_utc
            FROM machines ORDER BY name
            """;

        var list = new List<Machine>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    /// <summary>
    /// Withdraws a machine's token, keeping everything it recorded.
    /// </summary>
    /// <returns>False when no such machine, or it was already revoked.</returns>
    /// <remarks>
    /// <para>The row survives, and so does its pointer. A revoked machine is
    /// usually one that was lost, which is precisely when the work it pushed
    /// matters most: deleting the row would hide the last snapshot from every
    /// other machine and leave its objects unreachable through the heads listing.
    /// Nothing recorded is ever lost, revocation included.</para>
    ///
    /// <para>The stored hash is replaced with random bytes rather than nulled.
    /// The column is NOT NULL and uniquely indexed, and, more to the point, a
    /// value that is not the hash of anything cannot be matched by presenting a
    /// token: reversing it would take a preimage of SHA-256.</para>
    /// </remarks>
    public bool Revoke(string machineId)
    {
        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = """
            UPDATE machines SET token_hash = $dead, revoked_utc = $now
            WHERE id = $id AND revoked_utc IS NULL
            """;
        cmd.Parameters.AddWithValue("$dead", RandomNumberGenerator.GetBytes(32));
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", machineId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Moves one machine's pointer. Callers must already have checked it is their own.</summary>
    public void SetHead(string machineId, string snapshot)
    {
        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = "UPDATE machines SET head = $head, head_updated_utc = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$head", snapshot);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", machineId);
        cmd.ExecuteNonQuery();
    }

    // ---------- workspace ----------

    /// <summary>
    /// Records the workspace fingerprint the first machine presented, and
    /// afterwards refuses one that disagrees.
    /// </summary>
    /// <remarks>
    /// Without this, a machine holding a different key enrols happily, uploads
    /// objects nobody else can decrypt, and the mistake surfaces much later as
    /// unexplained authentication failures on someone else's machine.
    /// </remarks>
    public bool ClaimOrMatchFingerprint(string fingerprint)
    {
        using var cx = Open();

        using var read = cx.CreateCommand();
        read.CommandText = "SELECT value FROM workspace WHERE key = 'fingerprint'";
        if (read.ExecuteScalar() is string existing)
            return string.Equals(existing, fingerprint, StringComparison.Ordinal);

        using var write = cx.CreateCommand();
        write.CommandText = "INSERT OR IGNORE INTO workspace (key, value) VALUES ('fingerprint', $v)";
        write.Parameters.AddWithValue("$v", fingerprint);
        write.ExecuteNonQuery();
        return true;
    }

    public string? Fingerprint()
    {
        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = "SELECT value FROM workspace WHERE key = 'fingerprint'";
        return cmd.ExecuteScalar() as string;
    }

    // ---------- objects ----------

    private string PathOf(string id) => Path.Combine(_objectsDir, id[..2], id[2..]);

    public bool HasObject(string id) => File.Exists(PathOf(id));

    /// <summary>
    /// Stores a blob under a name, if that name is free.
    /// </summary>
    /// <returns>True when written, false when the name was already taken.</returns>
    /// <remarks>
    /// Objects are immutable. The server cannot check that a blob matches its
    /// name, so allowing a rewrite would let any enrolled machine replace content
    /// every other machine depends on. First writer wins, and a wrong object is
    /// caught by the reader on decryption.
    /// </remarks>
    public async Task<bool> PutObjectAsync(string id, Stream body, long maxBytes, CancellationToken ct)
    {
        var final = PathOf(id);
        if (File.Exists(final)) return false;   // cheap early out; the real check is below

        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("n"));

        try
        {
            // Receive the whole body into a temp file first, so the network
            // transfer happens before the name is claimed.
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;

                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        throw new InvalidOperationException($"object exceeds the {maxBytes} byte limit");

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            // Claim the name with an exclusive create, NOT with File.Move.
            //
            // File.Move(overwrite: false) is not atomic on Unix: it tests for the
            // destination and then calls rename(2), which silently replaces it, so
            // two writers racing both succeed and the later one wins. Measured:
            // 5 of 12 concurrent writers "created" the same object. That makes the
            // immutability this server promises false exactly when it matters,
            // and a sequential test cannot see it.
            //
            // FileMode.CreateNew maps to O_CREAT|O_EXCL, which the kernel resolves
            // atomically: exactly one writer creates the file and the rest get an
            // IOException.
            FileStream claimed;
            try
            {
                claimed = new FileStream(final, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException)
            {
                // Another machine got there first. The normal outcome of two
                // machines pushing the same content, not an error.
                return false;
            }

            try
            {
                await using (claimed)
                await using (var source = File.OpenRead(temp))
                    await source.CopyToAsync(claimed, ct).ConfigureAwait(false);

                return true;
            }
            catch
            {
                // A partial object under a real name is worse than none: it would
                // be indistinguishable from corruption for good.
                try { File.Delete(final); } catch (IOException) { }
                throw;
            }
        }
        finally
        {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch (IOException) { } }
        }
    }

    public Stream? OpenObject(string id)
    {
        var p = PathOf(id);
        return File.Exists(p) ? File.OpenRead(p) : null;
    }

    public (int Objects, long Bytes) MeasureObjects()
    {
        var n = 0;
        long bytes = 0;
        foreach (var f in Directory.EnumerateFiles(_objectsDir, "*", SearchOption.AllDirectories))
        {
            n++;
            bytes += new FileInfo(f).Length;
        }
        return (n, bytes);
    }

    private static byte[] HashToken(string token)
        => SHA256.HashData(Encoding.UTF8.GetBytes("weft/token/v1/" + token));

    private static Machine Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)),
        r.IsDBNull(7) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)));

    public HeadEntry ToHead(Machine m) => new(
        m.Id, m.Name, m.Platform, m.Head, m.HeadUpdatedUtc, m.LastSeenUtc, m.RevokedUtc);
}
