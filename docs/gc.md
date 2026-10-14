# Server-side garbage collection: design

Status: **implemented** (`ServerStore.Gc.cs`, `weft remote gc`), except the
operator command of §5 for revoked machines. Read with `DESIGN.md`; this document
changes invariant 2 and the server half of "Level 0", and says how.

---

## 1. Why

The server's object store only ever grows. `ServerStore` has no deletion path
other than undoing a half-written object, so anything a machine pushed once stays
for good, even after every snapshot that named it has been superseded.

Observed on the reference deployment: a Remotion project that was not a git
repository was snapshotted as loose files on 2026-09-29. ~88 MB of rendered MP4
and WAV entered the store in one push, and the nightly backup of the server went
from 15 MB to 112 MB. Adding the render directory to `.weftignore` and pushing
a new head on 2026-10-14 removed it from the head and from nothing else. Every
nightly archive still carries it, and will forever.

## 2. The constraint, and the observation that makes it tractable

**The server cannot compute reachability.** Every object is ciphertext filed
under `HMAC(identifier key, chunk hash)`. The server cannot open a snapshot, so
it cannot follow a snapshot to its manifest or a manifest to its chunks. A
classic mark-and-sweep on the server is impossible by construction, and making
it possible would mean giving the server something it must not have.

**The protocol only ever reads current heads.** Checked in the code:

- `SyncEngine.PullAsync` fetches the snapshot each machine's pointer names now,
  and what `Reachability.ChunksOf` says it needs. Nothing else.
- `Reachability.ChunksOf` deliberately does not follow parents.
- `SnapshotGraph.Parents` treats a missing ancestor as a root ("history that was
  never fetched, or was pruned"), so merge already runs on whatever ancestry is
  in the *local* store.

So an object that no current head reaches is never served to anyone through the
protocol. On the server it serves exactly one purpose, disaster recovery, and
the nightly backup (`backup.sh`, 14 days) covers that. Deleting it costs nothing
that the protocol can observe.

## 3. Mechanism: the client declares what its head pins

The machine that pushes a head already holds the key and already computes
`Reachability.ChunksOf(head)`. Today it sends that whole set to
`/v1/objects/missing` on every push and then throws it away. The design keeps it.

**A push becomes a session, and the session's id set becomes the head's pin set.**

```
POST /v1/push                      -> { session }
POST /v1/push/{session}/ids        { ids: [...] }  (<= 10 000 per call)
                                   -> { missing: [...] }   (replaces /objects/missing)
PUT  /v1/objects/{id}              unchanged
PUT  /v1/head                      { snapshot, session }
```

No protocol version bump: every change is additive. A new client facing an old
server gets a 404 on `POST /v1/push` and pushes the old way; an old client facing
a new server never opens a session and is handled as §5 describes.

At `PUT /v1/head` with a `session`, in one transaction, the server:

1. checks that the session belongs to the caller (the machine comes from the token,
   as today);
2. checks that `snapshot` is in the session's id set;
3. checks that **every** id in the session exists on disk. Today only the
   snapshot root is checked, so this also makes the existing "do not move a
   pointer into nothing" rule complete, which is worth having even without GC;
4. moves the pointer, records the old head in `head_history` with
   `superseded_utc = now`, and makes the session's ids the pin set of the new
   head;
5. deletes the session.

### Schema

```sql
CREATE TABLE pins (
  head    TEXT NOT NULL,          -- remote id of a snapshot
  object  TEXT NOT NULL,          -- remote id of an object it needs
  PRIMARY KEY (head, object)
) WITHOUT ROWID;

CREATE TABLE head_history (
  machine_id      TEXT NOT NULL,
  head            TEXT NOT NULL,
  set_utc         INTEGER NOT NULL,
  superseded_utc  INTEGER,        -- NULL while it is the machine's current head
  PRIMARY KEY (machine_id, head, set_utc)
);

CREATE TABLE push_sessions (
  id          TEXT PRIMARY KEY,
  machine_id  TEXT NOT NULL,
  created_utc INTEGER NOT NULL
);

CREATE TABLE session_ids (
  session  TEXT NOT NULL,
  object   TEXT NOT NULL,
  PRIMARY KEY (session, object)
) WITHOUT ROWID;
```

Pins are keyed by **head**, not by machine, so re-pushing an unchanged head changes
nothing. A machine counts as pinned when pins exist for its current head; there is
no separate flag to drift out of step. (Two machines never share a snapshot id in
practice: a snapshot records the machine that took it.)

### What this does not change

- **Confidentiality.** The server learns which opaque ids one push needs. It
  already learns exactly that from `/objects/missing`. Nothing new leaks.
- **Invariant 7.** A machine pins only through its own `PUT /v1/head`. Lying
  about its pin set can only expose *its own* head to collection: another
  machine's pins protect that machine's content whatever this one claims.
- **Immutability.** An object is never rewritten. It is either present or, after
  a sweep, absent, and a later push re-creates it under the same name.

## 4. What is live

An object survives a sweep when **any** of these holds:

| Rule | Why |
|---|---|
| Pinned by a machine's current head, **revoked machines included** | Invariant 10: revocation keeps everything recorded. |
| Pinned by a head superseded less than `Weft:RetainDays` ago (default 14) | A bad push (files excluded or deleted by mistake) stays recoverable from the server, not only from backups. 14 matches `KEEP_DAYS` in `backup.sh`. |
| Named by a push session younger than `Weft:SessionTtlHours` (default 24) | A push in flight: its `missing` answer said "already there", so it is counting on these objects. |
| File modified less than `Weft:GraceHours` ago (default 24) | Objects uploaded by a client that does not open sessions. See §5. |

Everything else is a candidate.

## 5. Safety rules

### Refuse rather than guess

The sweep **refuses to run** while any machine has a head without a pin set.
That is every machine before its first push with a GC-aware client, and any
machine that last pushed with an old one. The refusal names the machines, and
the fix is one `weft push` from each.

A head without pins is not "pins nothing": it is "pins something we cannot know".
The only correct reading of unknown is "keep everything".

A revoked machine that never pushed with a GC-aware client blocks GC for good.
The way out is an operator command, guarded by the join secret like `revoke`: a
client with the key pulls that head, computes `ChunksOf` for it, and submits the
pin set on the machine's behalf. It is guarded by the join secret and not a token
because, unlike a machine's own push, it writes into someone else's namespace.
**Not built yet**: no deployment has needed it.

### Old clients stay safe without a version floor

A client below the GC-aware version still calls `/v1/objects/missing` and then
`PUT /v1/head` without a session. Two changes keep that safe:

- `/v1/objects/missing` **touches** (sets the mtime of) every object it reports
  present. The grace window then protects exactly the objects the client was
  just told it need not send.
- `PUT /v1/head` without a session **clears** that machine's pin set, so the next
  sweep refuses (rule above) instead of trusting a stale set.

No `Weft:MinClient` bump is needed. Old clients keep working, and they only
prevent collection.

### One lock

One process-wide lock is held by every step that decides whether an object is
still wanted, and by the whole sweep (mark, then quarantine):

- recording a session's ids and answering which are missing, so an object is
  named by a live session *before* the push is told it is present;
- the legacy `/v1/objects/missing`, so the touch and the sweep's read of the
  modification time cannot interleave;
- both forms of `PUT /v1/head`.

Uploads (`PUT /v1/objects/{id}`) do not take it: a fresh object is young, and a
session already names it. A sweep moves a few thousand files at most, so pushes
wait for a second or two, never more.

### Quarantine, not deletion

A sweep **renames** candidates into `<data>/trash/<UTC stamp>/xx/yyyy`, on the
same filesystem, so the move is atomic. A later run deletes stamps older than
`Weft:TrashDays` (default 7). An operator can restore a stamp by moving it back.

`backup.sh` tars `objects/` and never `trash/`, so the nightly archive shrinks the
night after a sweep. Recovery stays possible from `trash/` for 7 days and from
the backups for 14.

## 6. Triggering it

```
POST /v1/gc   { joinSecret, apply: false, retainDays?: int }
-> { refused, unpinned: [{machineId, machineName}], retainDays, liveHeads,
     unretainableHeads, liveObjects, keptYoung, candidates, candidateBytes,
     applied, stamp, purgedStamps, trashDays }
```

- Guarded by the **join secret**, like `revoke`, for the same reason: a stolen
  laptop holds a token, not the operator's credential.
- **Dry run by default.** `apply: true` performs the quarantine.
- `retainDays` overrides the configured retention for one run. That is what a
  one-off cleanup like the MP4 case calls for.
- CLI: `weft remote gc [--apply] [--retain-days N]`, prompting for the join
  secret like `weft remote revoke`.

**Scheduling**: not automatic at first. Once trusted, run it from `backup.sh`
**after** the weft archive is written. The archive taken just before a sweep then
always holds everything the sweep removed.

## 7. Invariants, restated

Invariant 2 becomes:

> 2. No content reachable from a machine's current head, or from a head superseded
>    within the retention window, is ever removed from the server. Every local
>    store keeps everything it holds.

"Level 0: nothing is ever lost" keeps holding for the local store of every machine
that took the snapshot. On the server it holds for the retention window, and for
the backup window beyond it. The non-goal "Retention is bounded" becomes true.

## 8. Migration (reference deployment)

1. Deploy the server first. Old clients keep working against it (§5), and new
   clients keep working against an old server (§3).
2. Upgrade the MacBook and `vmi3365559`; each runs `weft push` once, so both
   heads get pin sets.
3. `weft remote gc` (dry run). Expect candidates around 70 MB: the MP4 chunks
   pinned only by the 2026-09-29 head, which was superseded on 2026-10-14.

   Caveat: `head_history` starts empty at migration. Heads superseded **before**
   the server upgrade left no trace, so the first sweep collects what only they
   needed, regardless of `RetainDays`. A head that was current at the upgrade is
   backfilled into history when it is superseded, so it gets the window; but it
   was pushed without pins, so the window cannot actually hold its objects. The
   report counts such heads as `unretainableHeads` and says so.
4. `weft remote gc --apply`. The archive taken that night should be back near
   15 MB plus the WAVs still pinned (~35 MB).

## 9. Tests the implementation must carry

Most of these are races. As `DESIGN.md` §5 notes about `File.Move`, a sequential
test cannot see a race, so each one needs a test that forces the interleaving.

All of these are in `tests/Weft.Server.Tests/GarbageCollectionTests.cs`, except
the `backup.sh` one, which belongs to the deployment.

- A push whose `missing` answer predates a sweep and whose `PUT /v1/head` follows
  it: the head lands with every object present.
- The same with an old client (no session): the touch plus the grace window keep
  its objects.
- `PUT /v1/head` naming a session with an id absent from disk: refused, pointer
  unchanged.
- A machine with an unpinned head: the sweep refuses and names it.
- A revoked machine's head: never collected.
- A head superseded within `RetainDays`: kept; past it: quarantined.
- An expired session: its objects become candidates after the grace window.
- Quarantine, then restore, then pull: content identical, decryption succeeds.
- An object re-pushed after quarantine: created afresh, the head commits.
- `backup.sh` after a sweep: the archive excludes `trash/`, and
  `restore.sh --verify` passes.

## 10. Out of scope

- **The local store.** The MacBook still holds the MP4 chunks in
  `.weft/store` (98 MB). Collecting there is a different problem: local history
  feeds merge bases, so the rule is not "current head only". It belongs in its
  own document.
- **Re-encryption or key rotation.**
