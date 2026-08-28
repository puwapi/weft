# weft: design

> A weft is the thread carried *across* the warp to bind cloth together.
> This one is carried across machines.

This document is the reasoning behind the code. If an implementation choice here
looks arbitrary, the justification is in this file. Read it before changing the
storage format, the sync protocol, or the merge engine.

---

## 1. The problem, stated precisely

A monorepo that contains many independent git repositories has state that no
single tool handles well:

- **git** manages each repository, but knows nothing about the ones next to it,
  and nothing about the files that live between them.
- **File synchronisers** (Syncthing, Dropbox, rsync) move bytes, but treat
  `.git/` as ordinary bytes. That is not safe: two machines running `git gc`
  concurrently produce a corrupt repository, and a packfile copied mid-write is
  garbage. They also resolve conflicts by dropping a second copy next to the
  first, which is not a resolution.

The reference workload this was built against: **49 git repositories, 54 GB on
disk, 1.8 million files, of which 43 000 files (~1.5 GB) are actual content.**
97% of the tree is regenerable (`node_modules`, build output, package caches).

Two failure modes motivated it, both observed in the wild:

1. A complete feature branch, thousands of lines across dozens of files, living
   on exactly one disk with no remote. The notes described it as shipped. A dead
   drive would have erased it with nothing to show it had ever existed.
2. A deployment that ships a *working tree* rather than a commit, so uncommitted
   local edits reach a server without ever having been reviewed, and without the
   history recording that they went out.

Both are the same failure: **work that exists in one place and nothing knows it.**

---

## 2. The core insight: three kinds of state

Conflating these is what makes generic file sync dangerous. weft separates them
and treats each differently.

### A. Loose files (not inside any git repository)

Root-level documents, notes, plans, scratch directories. Pure content. This is
the only category where content merging applies.

### B. Git repository *state*

Which repositories exist, their remotes, and per machine which branch is checked
out at which commit.

**This is not content to be merged.** Two machines on two different branches is
not a conflict, it is Tuesday. Merging them would be actively wrong.

- Repository membership and remotes: **converged**. A repository cloned on one
  machine appears on the others.
- Checked-out branch and HEAD: **per machine, informational**. Never merged.
- Missing git objects: transported as **incremental `git bundle`**. Native git
  deltas, nothing reinvented.

### C. Uncommitted work inside repositories

The gap that cost 6 475 lines. Content, but anchored to a git base commit.

Captured continuously, **never applied automatically**. `weft carry` on one
machine, `weft land` on the other. Silently applying a patch to a working tree is
precisely the gesture that destroys a colleague's or a parallel session's work.

> **Invariant: weft never writes into a git repository's working tree without an
> explicit command.** Snapshotting is always safe to run.

---

## 3. Storage: only the changes, at two levels

The request was "like git but without a full reference at every commit". Worth
being precise: git already stores deltas in packfiles and deduplicates trees by
hash. What git *does* rewrite per commit is the tree listing every path.

weft avoids re-sending anything at two levels.

### Level 1: content-defined chunking

Files are split on **content-defined boundaries** (FastCDC, 8 KB target, 2 KB
min, 64 KB max) rather than fixed offsets. Chunks are addressed by BLAKE3 and
stored once, compressed with zstd.

Why it matters, measured on the 377 KB document that this workspace's owner
edits several times a day:

| edit | bytes to send | share of the file |
|---|---|---|
| a section appended at the end | 9.3 KB | 2.45% |
| a paragraph inserted mid-file | 8.4 KB | 2.24% |
| **a line added at the very top** | **3.6 KB** | **0.95%** |

The last row is the case that justifies the whole mechanism: it shifts every byte
in the file. Fixed-size blocks would re-send all 377 KB.

Across whole repositories, five commits apart: 12.8 MB of source costs 800 KB to
bring up to date (6.1%); 1.7 MB costs 83 KB (4.7%).

### Level 2: incremental manifests, with no delta format

A snapshot does **not** re-send the whole file listing. But there is no delta
format either, and that is a deliberate simplification made during
implementation.

The manifest is stored **as an ordinary file**: serialised with entries sorted by
path, then split by the same content-defined chunker and put in the same store.
Adding one file changes the one chunk its line lands in and leaves every other
chunk identical, so the incremental property falls out of the storage layer.

That removes everything a delta chain drags along: no compaction schedule, no
"materialise a full manifest every N snapshots", and no chain to walk before a
manifest can be read.

Measured: 9 113 files produce a manifest of roughly 1.4 MB, so a snapshot
touching five files rewrites about five of its chunks.

### Hashing choices, and why two of them

| Purpose | Algorithm | Measured | Why |
|---|---|---|---|
| Change detection during scan | XxHash128 | 14.2 GB/s | Only decides "did this file change", after a `stat` mismatch already gated it. Non-cryptographic is fine. |
| Content addressing | SHA-256 | 3.05 GB/s | Chunk identity is a security boundary: a collision means serving the wrong content, so it must be cryptographic. |
| Storage compression | Deflate, `Optimal` | 30.8% ratio | Beat Brotli on ratio **and** speed on real source. |

SHA-256 rather than BLAKE3, and Deflate rather than zstd, for one shared reason:
both are in the standard library. A native binding would be faster on paper and
would make the tool AOT-hostile, add a supply-chain dependency to a security
boundary, and complicate every cross-platform build. 3 GB/s is not the
constraint, because the cryptographic hash only ever runs on content that
changed.

### The scan must never enter ignored directories

With 1.8 M files present and 43 k relevant, ignore rules are applied **during**
traversal, pruning at the directory boundary. Descending into `node_modules` to
then discard the results costs 40x the work for nothing.

---

## 4. Machine identity

- **Stable ID**: UUIDv7, generated at `weft init`, stored in `~/.weft/machine.json`.
  Never derived from hostname. A hostname changes, and two machines can share one.
- **Human name**: freely renameable (`weft machine rename`). The ID never moves.
- The server records id, name, OS, last contact, last snapshot. The TUI can then
  say *"laptop is 3 snapshots behind, desktop last seen 2 h ago"* rather than
  showing opaque device identifiers.

---

## 5. The remote

A small ASP.NET Core service. It is the **authority**: the ordering of snapshots
is whatever the server accepted, and a client that disagrees is wrong.

**Each machine writes only within its own namespace.** Convergence is computed by
reading every machine head. This removes the need for a distributed lock, which
is the usual source of corruption in sync tools.

In practice that rule has one enforcement point: the machine is taken from the
bearer token, and no request carries a machine identifier at all. There is no
parameter that could name someone else's pointer.

**Objects are immutable.** The server holds ciphertext and cannot check that a
blob matches the name it is filed under, so allowing a rewrite would let any
enrolled machine replace content every other machine depends on. First writer of
a name wins.

> The name is claimed with an exclusive create (`O_CREAT|O_EXCL`), not with a
> rename. `File.Move(overwrite: false)` on Unix tests for the destination and then
> calls `rename(2)`, which silently replaces it: measured, 5 of 12 concurrent
> writers all "created" the same object. A sequential test cannot see this.

### Client version enforcement

The requirement is that the CLI is current before it modifies anything.

- Every response carries `Weft-Min-Client`.
- A client below the floor is **refused writes but allowed reads**.

  That asymmetry is deliberate. Refusing reads too would strand an outdated
  client with no way to fetch what it needs, including its own work. A stranded
  machine is a worse failure than a stale one.
- The **protocol** version is separate from the **binary** version: an old build
  that still speaks the current protocol is not blocked for no reason.

---

## 6. The merge model

Five levels, from the guarantee everything rests on to the cases where no
heuristic is honest.

### Level 0: nothing is ever lost

Every version that has been snapshotted is in the object store, addressable and
restorable. A bad merge is always recoverable.

This is the difference from `.sync-conflict-*` files, which are so unhelpful that
ignore rules typically have to exclude them to stop them replicating.

### Level 1: the common ancestor

weft holds the snapshot DAG, so for any path it can find the version at the
**common ancestor** of two branches. That is what makes a real three-way merge
possible. A generic file synchroniser has only two versions and therefore cannot
decide anything.

### Level 2: unambiguous cases resolve silently

- changed on one side only → take that side
- changed identically on both → not a conflict
- added on one side only → take it
- deleted on one side, untouched on the other → take the deletion
- text with non-overlapping hunks → three-way merge (diff3)

For a single user across several machines, this covers the overwhelming majority.

### Level 3: format-aware merge drivers

Where line-based diff3 is bad and structure does better:

| Format | Strategy | Why |
|---|---|---|
| JSON / JSONC | recursive key merge | Two machines adding different dependencies to a `package.json` is a clean merge structurally and a brace conflict textually. |
| YAML | recursive key merge | compose files, CI workflows |
| `.env`, `.properties` | key merge | Configuration keys diverge constantly and independently. |
| i18n message files | key merge | The motivating case: 98 keys from one feature interleaved with 33 from another required hand-surgery with `git reset --mixed`. A key-aware driver merges it outright. |
| Markdown | section-level diff3, split on headings | Two machines each appending a new section produce a textual conflict at EOF and no semantic conflict at all. |
| Append-only files | ordered concatenation when one side is a prefix of the other | Logs, and long documents whose real edit pattern is appending. |

### Level 4: real conflicts

Genuinely overlapping hunks, diverging binaries, delete-versus-modify. No
heuristic is honest here, so weft asks. Both versions are materialised, the TUI
shows them side by side.

> **The working tree is not touched until the conflict is resolved.** A
> half-merged file on disk is worse than no file: it looks finished.

### Level 5: git state does not merge like content

Restating section 2B, because it is the rule most likely to be violated by
someone adding a feature: repository membership converges, checked-out branches
do not merge, and uncommitted work moves only on request.

---

## 7. Confidentiality

Ignore rules serve two unrelated purposes, and merging them is a mistake.

- **`ignore`**: cleanliness. Regenerable output. Overridable.
- **`never`**: confidentiality. Private keys, certificates, `.env`, audits.
  **Not overridable, not by `--force`.**

Additionally:

- A **secret scanner** runs on candidate content (private key headers, live API
  key prefixes, connection strings). A hit **blocks the snapshot** and names the
  file rather than warning and continuing.
### Encryption

The object store is **encrypted client-side**. The remote is infrastructure, not a
trust boundary.

A 256-bit key is generated at `weft init` and carried to each other machine by
hand. There is no passphrase and no key-derivation function: a generated key
already has full entropy, and a passphrase only adds a way to choose a weak one.
Two sub-keys are derived from it, one to encrypt and one to name, so that no
single key serves two purposes.

**Deduplication has to survive encryption, and normally does not.** A fresh random
nonce makes identical content encrypt differently every time, so the server would
keep one copy per machine per snapshot and the entire content-addressed design
would stop paying for itself. The nonce is therefore derived from the chunk's own
hash: identical content encrypts to identical bytes, and different content yields
a different nonce, so a (key, nonce) pair is never reused across different
plaintexts, which is the one thing that breaks AES-GCM outright.

The name the server files an object under is `HMAC(identifier key, chunk hash)`,
never the hash itself. Using the hash would let anyone holding the server ask
"do you have this file I already have?" and get an answer.

**The honest cost**: someone holding both the server and a candidate file can
still confirm whether that exact file is stored, because they could compute the
same name if they had the key. They learn presence, never content. That is the
standard trade of convergent encryption, and the alternative is storing every
machine's copy of every file separately.

Two checks happen on every read, and the server can perform neither: the
authentication tag proves the bytes are ours and unaltered, and re-hashing the
plaintext proves it is the chunk that was actually asked for.

---

## 8. Non-goals

- **Not a git replacement.** git owns history inside a repository. weft owns what
  is between and around repositories.
- **Not a backup tool.** Snapshots serve synchronisation. Retention is bounded.
- **Not real-time collaborative editing.** The unit is a file, not a keystroke.
- **Not a general file synchroniser.** It is tuned for source trees: many small
  text files, huge regenerable subtrees, and git repositories that must be
  handled as repositories.

---

## 9. Invariants a change must not break

1. weft never writes into a git working tree without an explicit command.
2. No snapshotted content is ever unreachable.
3. A `never` rule cannot be overridden.
4. Ignored directories are never descended into.
5. A conflicted file is never written half-merged to disk.
6. An outdated client may read; only writes are refused.
7. A machine writes only within its own namespace on the remote.
