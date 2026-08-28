# weft

Keep a monorepo of many git repositories in step across machines.

A weft is the thread carried *across* the warp to bind cloth together. This one
is carried across machines.

> Status: early. The scanner and the ignore engine work; sync is not implemented
> yet. See [DESIGN.md](DESIGN.md) for the model and [Roadmap](#roadmap) for what
> exists today.

---

## The problem

You have one directory holding dozens of independent git repositories, plus the
loose files between them: notes, runbooks, plans, scratch work. You work on it
from more than one machine.

Neither tool you already have covers this.

**git** manages each repository and knows nothing about the ones beside it, or
about the files between them.

**File synchronisers** move bytes, but treat `.git/` as ordinary bytes. Two
machines running `git gc` at the same time produce a corrupt repository, and a
packfile copied mid-write is garbage. When they hit a conflict they drop a second
copy next to the first, which is not a resolution: ignore rules usually end up
having to exclude those copies to stop them replicating.

The failure this is built to prevent is simple: **work that exists in one place
and nothing knows it.** A branch on one disk with no remote. A feature that the
notes describe as shipped and that a failed drive would erase.

## What weft does differently

**It asks git instead of guessing.** Inside a repository, git already knows what
matters: it honours that repository's `.gitignore`, its worktrees, its sparse
checkout, your hooks, your credential helper, your SSH agent. weft shells out to
the git binary rather than linking a git library, because reimplementations drift
on exactly the edge cases that matter.

**It separates three kinds of state that generic sync conflates.**

| | how it is treated |
|---|---|
| Loose files, outside every repository | content, merged three-way |
| Repository membership and remotes | converged across machines |
| Checked-out branch and HEAD | per machine, never merged |
| Uncommitted work | captured, moved only on request |

Two machines on two different branches is not a conflict. Merging them would be
wrong.

**It never writes into a working tree on its own.** Snapshotting is always safe
to run. Moving uncommitted work is an explicit command, because silently applying
a patch to a working tree is how you destroy someone else's session.

**It stops at every repository during the walk.** On the tree it was built
against, that turns 1.8 million filesystem entries into 1 188 visited, in 9 ms.

## Install

Requires [.NET 10](https://dotnet.microsoft.com/download) to build, and `git` on
`PATH` to run. The published binary is native and needs no runtime installed.

```
git clone https://github.com/puwapi/weft && cd weft
dotnet publish src/Weft.Cli -c Release -o ./publish
./publish/weft --help
```

## Use

```
weft init                                   # create a workspace, generate its key
weft scan                                   # report what weft sees, changing nothing
weft snapshot                               # record the state of the workspace
weft remote add https://weft.example --join <secret>
weft push                                   # send the latest snapshot
weft pull                                   # fetch what other machines recorded
weft merge                                  # reconcile with another machine
```

On a second machine, carry the key over first:

```
weft init
weft key set weft-XXXXXXXX-XXXXXXXX-...     # from 'weft key show --reveal' on the first
weft remote add https://weft.example --join <secret>
weft pull
```

`weft scan` reports repositories, working trees, loose files, and anything it
refuses as confidential. It also flags trees that are **at risk**: worktrees
living in a temp directory the OS may reclaim, or outside the workspace where
weft cannot reach them.

`weft snapshot` records the state and reports what a push would cost. Appending a
section to a 377 KB document sends 9.3 KB. Adding a line at the very top of it,
which shifts every byte in the file, sends 3.6 KB. It also lists every repository
holding uncommitted work, every time, because a branch that lives on one disk is
invisible precisely because nobody thought to look.

## The server

One container, one volume, no database to run.

```
cp .env.example .env      # set WEFT_JOIN_SECRET; the server will not start without one
docker compose up -d
```

**The server cannot read your files.** Content is encrypted on the machine that
holds it, with a key generated at `weft init` and carried to your other machines
by hand. The server sees ciphertext filed under keyed names it cannot invert. It
is somewhere to put bytes, not something to trust.

Deduplication survives that, which is the part that usually does not: the nonce
is derived from each chunk's own hash, so identical content encrypts to identical
bytes and is stored once. The honest cost of that construction is that anyone
holding the server and a candidate file can confirm whether that exact file is
stored. They learn presence, never content.

Two guarantees the server does hold, and a script that checks them:

- **A machine can only move its own pointer.** The machine comes from the token,
  and no request names another.
- **Objects are immutable.** The server cannot verify ciphertext against its name,
  so the first writer of a name wins and nothing can replace it.

```
scripts/probe-server.sh <url> <join-secret> <token-a> <token-b>
```

Those are the behaviours a well-behaved client never exercises, which is why they
are probed over HTTP rather than left to unit tests.

## Merging

`weft pull` fetches; `weft merge` reconciles. Keeping them apart means a merge
cannot half-finish because a connection dropped, and it can be run on a train.

Most differences never reach you. weft holds the snapshot both machines started
from, so it can tell "they added a line" from "I deleted one", which a two-way
synchroniser cannot. Changes to different parts of a file are both applied.
The same change made twice is not a disagreement.

Two cases resolve that a line merge alone would refuse, and weft says so rather
than resolving them silently:

- **Both machines appended to the same document.** As lines that is a conflict,
  because the order of two insertions at one point cannot be inferred. As an
  edit it is not: neither side touched what the other relies on, so both are
  kept, ordered by content so that **both machines produce the same file**.
  Ordering by "mine first" would give each machine a different result and they
  would conflict again forever.
- **Both added different keys to the same JSON object.** Conflicts as text,
  merges exactly as data.

A line merge always runs first, even on JSON, because it keeps the file as it was
written: its key order, its indentation, its comments. Structure is the fallback,
reached only when the line merge fails, which is precisely when it helps.

### When it genuinely cannot decide

**No conflict markers are ever written into your file.** A file carrying `<<<<<<<`
is broken for every tool that reads it, and if nobody notices it stays broken.

Instead the file is left exactly as it was, still holding your version and still
working, and the other version is written beside it:

```
notes.md               your version, untouched
notes.md.weft-theirs   the other machine's
notes.md.weft-base     what you both started from
```

Edit until you are happy, delete the `.weft-theirs` companion to say so, then:

```
weft merge --continue
```

That records a snapshot with **both** heads as parents, which makes it the new
common ancestor. Without it the next merge would find the same ancestor, see the
same two versions, and ask you the same question forever.

### What merging does not touch

Repository state is reported and never merged. Two machines on two branches is
not a disagreement, and cloning a repository that exists only on the other machine
is a decision, not a consequence.

The merge cannot reach into a git working tree at all: the walk stops at every
repository, so no checkout is ever in a manifest, and there is no code path that
could write into one.

## Rules

Two files, for two unrelated reasons. Merging them is a mistake.

**`.weftignore`** is cleanliness: build output, dependency trees, caches.
Overridable. Only governs loose files, since anything inside a repository is
decided by git.

**`.weftnever`** is confidentiality: keys, certificates, environment files.
**Not overridable, not by `--force`.** Negation (`!`) is a parse error here.
`=name` exempts one exact literal name, so `.env.example` survives a rule that
refuses every other `.env.*`, without opening a hole a pattern could widen.

If a `.stignore` is present, `weft init` imports it. Rules that look confidential
are routed to `.weftnever`, including rules whose only clue is the comment above
them, and every reclassification is reported so you can move it back.

## Roadmap

- [x] Ignore engine, with the cleanliness / confidentiality split
- [x] Single-pass tree walk that stops at every repository
- [x] Repository and worktree discovery, including trees outside the workspace
- [x] Machine identity
- [x] `.stignore` import
- [x] Content-addressed store with content-defined chunking
- [x] Snapshots and incremental manifests
- [x] Client-side encryption that keeps deduplication
- [x] Server, sync protocol, push and pull
- [x] Three-way merge with format-aware drivers
- [ ] Uncommitted work capture and transfer
- [ ] TUI
- [ ] Self-update

## Contributing

Read [DESIGN.md](DESIGN.md) first. Section 9 lists the invariants a change must
not break.

One convention worth knowing: a guard is not trusted here until it has been seen
to fail. When you add a test that protects an invariant, break the invariant on
purpose and confirm the test goes red. A guard whose scope has drifted looks
exactly like one that works.

## Licence

Apache-2.0.
