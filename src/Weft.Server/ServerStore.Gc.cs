using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Weft.Core.Crypto;

namespace Weft.Server;

/// <summary>Why a head commit was refused, or that it went through.</summary>
public enum CommitStatus
{
    Committed,
    NoSuchSession,
    SnapshotNotInSession,
    ObjectsMissing,
}

public sealed record CommitOutcome(CommitStatus Status, int Missing = 0);

/// <summary>How a sweep decides, and whether it acts.</summary>
public sealed record GcOptions
{
    /// <summary>False reports what would go and moves nothing.</summary>
    public bool Apply { get; init; }

    /// <summary>How long a superseded head keeps what it pinned.</summary>
    public TimeSpan Retain { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Objects written or confirmed more recently than this are never candidates.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long an unfinished push keeps the objects it named.</summary>
    public TimeSpan SessionTtl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long a quarantined object waits in trash before it is deleted.</summary>
    public TimeSpan TrashKeep { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The clock the sweep reads. Only tests set it.</summary>
    public DateTimeOffset? Now { get; init; }
}

/// <summary>What a sweep found, and what it did.</summary>
/// <param name="Unpinned">Machines whose head declares no pins. Any at all and nothing is collected.</param>
/// <param name="UnretainableHeads">
/// Superseded heads inside the retention window that were set without pins. Their
/// objects cannot be told apart from garbage, so the window cannot hold them.
/// </param>
public sealed record GcReport(
    IReadOnlyList<Machine> Unpinned,
    int LiveHeads,
    int UnretainableHeads,
    int LiveObjects,
    int KeptYoung,
    int Candidates,
    long CandidateBytes,
    bool Applied,
    string? Stamp,
    int PurgedStamps)
{
    public bool Refused => Unpinned.Count > 0;
}

public sealed partial class ServerStore
{
    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    // ---------- push sessions ----------

    /// <summary>Opens a push. The ids it is told about become the new head's pins.</summary>
    public string BeginPush(string machineId)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        using var cx = Open();
        using var cmd = cx.CreateCommand();
        cmd.CommandText = "INSERT INTO push_sessions (id, machine_id, created_utc) VALUES ($id, $m, $now)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$m", machineId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        return id;
    }

    /// <summary>
    /// Records ids the pushed head needs, and answers which of them are absent.
    /// </summary>
    /// <returns>Null when the session is not this machine's, or does not exist.</returns>
    /// <remarks>
    /// The ids are recorded BEFORE presence is checked, and both happen under the
    /// gate. From the moment this answers "present", the object is named by a live
    /// session and no sweep can take it.
    /// </remarks>
    public IReadOnlyList<string>? AddToSession(string machineId, string session, IReadOnlyCollection<string> ids)
    {
        _gate.Wait();
        try
        {
            using var cx = Open();
            if (!OwnsSession(cx, null, machineId, session)) return null;

            using (var tx = cx.BeginTransaction())
            {
                using var insert = cx.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT OR IGNORE INTO session_ids (session, object) VALUES ($s, $o)";
                var s = insert.Parameters.Add("$s", SqliteType.Text);
                var o = insert.Parameters.Add("$o", SqliteType.Text);
                s.Value = session;

                foreach (var id in ids)
                {
                    o.Value = id;
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
            }

            return ids.Where(id => !HasObject(id)).ToList();
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Moves the pointer to a head whose every object is on disk, and pins them.
    /// </summary>
    /// <remarks>
    /// Checking every object, not just the snapshot root, is what makes "never
    /// move a pointer into nothing" complete. With collection in the picture it is
    /// also the last line: an object that disappeared between the presence answer
    /// and this call is caught here and the push fails instead of publishing a
    /// head nobody can read.
    /// </remarks>
    public CommitOutcome CommitHead(string machineId, string session, string snapshot)
    {
        _gate.Wait();
        try
        {
            using var cx = Open();
            using var tx = cx.BeginTransaction();

            if (!OwnsSession(cx, tx, machineId, session)) return new(CommitStatus.NoSuchSession);

            var ids = new List<string>();
            using (var read = cx.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT object FROM session_ids WHERE session = $s";
                read.Parameters.AddWithValue("$s", session);
                using var r = read.ExecuteReader();
                while (r.Read()) ids.Add(r.GetString(0));
            }

            if (!ids.Contains(snapshot, StringComparer.Ordinal)) return new(CommitStatus.SnapshotNotInSession);

            var missing = ids.Count(id => !HasObject(id));
            if (missing > 0) return new(CommitStatus.ObjectsMissing, missing);

            using (var pin = cx.CreateCommand())
            {
                pin.Transaction = tx;
                pin.CommandText = """
                    INSERT OR IGNORE INTO pins (head, object)
                    SELECT $head, object FROM session_ids WHERE session = $s
                    """;
                pin.Parameters.AddWithValue("$head", snapshot);
                pin.Parameters.AddWithValue("$s", session);
                pin.ExecuteNonQuery();
            }

            MoveHead(cx, tx, machineId, snapshot, DateTimeOffset.UtcNow);
            DeleteSession(cx, tx, session);

            tx.Commit();
            return new(CommitStatus.Committed);
        }
        finally { _gate.Release(); }
    }

    private static bool OwnsSession(SqliteConnection cx, SqliteTransaction? tx, string machineId, string session)
    {
        using var cmd = cx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM push_sessions WHERE id = $s AND machine_id = $m";
        cmd.Parameters.AddWithValue("$s", session);
        cmd.Parameters.AddWithValue("$m", machineId);
        return cmd.ExecuteScalar() is not null;
    }

    private static void DeleteSession(SqliteConnection cx, SqliteTransaction tx, string session)
    {
        using var cmd = cx.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM session_ids WHERE session = $s;
            DELETE FROM push_sessions WHERE id = $s;
            """;
        cmd.Parameters.AddWithValue("$s", session);
        cmd.ExecuteNonQuery();
    }

    // ---------- garbage collection ----------

    /// <summary>
    /// Moves objects that nothing wants into trash, or reports what it would move.
    /// </summary>
    /// <remarks>
    /// <para>The server cannot read a snapshot, so it cannot work out on its own
    /// what a head needs. Each head's needs were declared by the machine that
    /// pushed it, and that declaration is all this reads. A head without one means
    /// "needs something we cannot know", which is not the same as "needs nothing",
    /// so a single unpinned head stops the whole sweep.</para>
    ///
    /// <para>Nothing is deleted here, only renamed into a stamped directory under
    /// trash/, on the same filesystem. Only stamps older than the trash window are
    /// removed for good.</para>
    /// </remarks>
    public GcReport CollectGarbage(GcOptions options)
    {
        var now = options.Now ?? DateTimeOffset.UtcNow;
        var retainCutoff = (now - options.Retain).ToUnixTimeMilliseconds();
        var sessionCutoff = (now - options.SessionTtl).ToUnixTimeMilliseconds();
        var youngCutoff = (now - options.Grace).UtcDateTime;

        _gate.Wait();
        try
        {
            using var cx = Open();

            var unpinned = AllMachines().Where(m => m.Head is not null && !m.Pinned).ToList();
            if (unpinned.Count > 0)
                return new GcReport(unpinned, 0, 0, 0, 0, 0, 0, false, null, 0);

            const string liveHeadsSql = """
                SELECT head FROM machines WHERE head IS NOT NULL
                UNION
                SELECT head FROM head_history WHERE superseded_utc IS NULL OR superseded_utc >= $retain
                """;

            var liveHeads = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = cx.CreateCommand())
            {
                cmd.CommandText = liveHeadsSql;
                cmd.Parameters.AddWithValue("$retain", retainCutoff);
                using var r = cmd.ExecuteReader();
                while (r.Read()) liveHeads.Add(r.GetString(0));
            }

            var pinnedHeads = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = cx.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT head FROM pins";
                using var r = cmd.ExecuteReader();
                while (r.Read()) pinnedHeads.Add(r.GetString(0));
            }

            var live = new HashSet<string>(StringComparer.Ordinal);
            using (var cmd = cx.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT object FROM pins WHERE head IN ({liveHeadsSql})
                    UNION
                    SELECT s.object FROM session_ids s
                    JOIN push_sessions p ON p.id = s.session
                    WHERE p.created_utc >= $sessions
                    """;
                cmd.Parameters.AddWithValue("$retain", retainCutoff);
                cmd.Parameters.AddWithValue("$sessions", sessionCutoff);
                using var r = cmd.ExecuteReader();
                while (r.Read()) live.Add(r.GetString(0));
            }

            var candidates = new List<(string Id, string Path, long Bytes)>();
            var young = 0;

            foreach (var file in Directory.EnumerateFiles(_objectsDir, "*", SearchOption.AllDirectories))
            {
                var id = Path.GetFileName(Path.GetDirectoryName(file)!) + Path.GetFileName(file);

                // Anything that is not named like an object was not put here by a
                // push, and a sweep has no business guessing what it is.
                if (!RemoteId.TryParse(id, out _)) continue;
                if (live.Contains(id)) continue;

                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc >= youngCutoff) { young++; continue; }

                candidates.Add((id, file, info.Length));
            }

            var report = new GcReport(
                [],
                liveHeads.Count,
                liveHeads.Count(h => !pinnedHeads.Contains(h)),
                live.Count,
                young,
                candidates.Count,
                candidates.Sum(c => c.Bytes),
                false, null, 0);

            if (!options.Apply) return report;

            var stamp = now.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
            var stampDir = Path.Combine(_trashDir, stamp);
            for (var n = 2; Directory.Exists(stampDir); n++) stampDir = Path.Combine(_trashDir, $"{stamp}-{n}");

            var moved = 0;
            foreach (var (id, path, _) in candidates)
            {
                var dest = Path.Combine(stampDir, id[..2], id[2..]);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                try { File.Move(path, dest); moved++; }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }

            using (var tx = cx.BeginTransaction())
            using (var cmd = cx.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    DELETE FROM pins WHERE head NOT IN ({liveHeadsSql});
                    DELETE FROM head_history WHERE superseded_utc IS NOT NULL AND superseded_utc < $retain;
                    DELETE FROM session_ids WHERE session IN
                      (SELECT id FROM push_sessions WHERE created_utc < $sessions);
                    DELETE FROM push_sessions WHERE created_utc < $sessions;
                    """;
                cmd.Parameters.AddWithValue("$retain", retainCutoff);
                cmd.Parameters.AddWithValue("$sessions", sessionCutoff);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }

            return report with
            {
                Applied = true,
                Candidates = moved,
                Stamp = moved > 0 ? Path.GetFileName(stampDir) : null,
                PurgedStamps = PurgeTrash(now - options.TrashKeep),
            };
        }
        finally { _gate.Release(); }
    }

    /// <summary>Deletes trash stamps older than the cutoff. Returns how many went.</summary>
    private int PurgeTrash(DateTimeOffset cutoff)
    {
        if (!Directory.Exists(_trashDir)) return 0;

        var purged = 0;
        foreach (var dir in Directory.EnumerateDirectories(_trashDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Length < 16) continue;

            // A directory whose name is not a stamp was not made by a sweep.
            if (!DateTime.TryParseExact(name[..16], StampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                continue;

            if (at >= cutoff.UtcDateTime) continue;

            try { Directory.Delete(dir, recursive: true); purged++; }
            catch (IOException) { }
        }
        return purged;
    }
}
