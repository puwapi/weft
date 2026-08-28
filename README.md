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

**macOS and Linux**

```
curl -fsSL https://raw.githubusercontent.com/puwapi/weft/main/install.sh | sh
```

**Windows**

```
irm https://raw.githubusercontent.com/puwapi/weft/main/install.ps1 | iex
```

Both verify the download against the checksums published with the release. Or
take a binary from [the releases page](https://github.com/puwapi/weft/releases).

| | x64 | arm64 |
|---|---|---|
| macOS | ✓ | ✓ |
| Linux (glibc) | ✓ | ✓ |
| Windows | ✓ | ✓ |

The binary carries its own runtime and needs nothing installed. It does need
**git** on `PATH`, which it never bundles: every repository operation is handed to
your git, so hooks, credential helpers and config behave exactly as they already
do. Alpine and other musl systems need a build from source.

**[Read the tutorial →](docs/getting-started.md)** Two machines and a server, in
about fifteen minutes.

### Building

```
git clone https://github.com/puwapi/weft && cd weft
dotnet publish src/Weft.Cli -c Release -o ./publish
./publish/weft --help
```

Requires [.NET 10](https://dotnet.microsoft.com/download). NativeAOT cannot cross
compile between operating systems, so a binary must be built on the OS it runs on.
It does cross architectures within one: an arm64 Mac builds a working x86_64
binary.

## Use

```
weft init                                   # create a workspace, generate its key
weft scan                                   # report what weft sees, changing nothing
weft snapshot                               # record the state of the workspace
weft remote add https://weft.example --join <secret>
weft push                                   # send the latest snapshot
weft pull                                   # fetch what other machines recorded
weft merge                                  # reconcile with another machine
weft carry                                  # what exists on this disk and nowhere else
weft land                                   # apply another machine's uncommitted work
weft tui                                    # full screen: what wants attention, settle conflicts
weft up                                     # update weft itself
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

### Reaching it

`weft remote add` refuses plain HTTP to anything but a loopback address. Your
files would still be encrypted, so this is not about content: the join secret and
the machine's token are not, and either one buys enrolment and a full download of
every object. `--insecure` allows it for a host that is only reachable over a
private network, and says so on every push and pull afterwards rather than once,
months ago, on a machine somebody else set up.

### Losing a machine

```
weft remote machines                        # who is enrolled, and since when
weft remote revoke <id> --join <secret>
```

Revoking withdraws that machine's token and **keeps everything it recorded**. The
machine you are revoking is usually the one that was lost, which is exactly when
the work it pushed matters most; its snapshot stays where `weft pull` can see it.

It asks for the join secret rather than using this machine's token, and that is
the point. The lost machine holds a token. It does not hold the join secret,
because the client trades that for a token at enrolment and never writes it down.
Accepting a token here would let whoever took the machine revoke everyone else
first.

**Revoking does not undo what that machine can read.** The workspace key is on its
disk, along with every object it already fetched. If it is in someone else's
hands, the answer is a new key on the machines you keep and a fresh server:
revocation closes the door, it does not empty the room.

## Uncommitted work

The failure this tool was built for is a branch that lives on one disk, that the
notes describe as shipped, and that a dead drive would erase with nothing to show
it existed.

`weft snapshot` captures the uncommitted state of every checkout, by default,
because a safety net you have to switch on is one nobody has switched on when the
drive fails. `weft carry` answers the question directly:

```
Checkout   Branch    Files   Where it exists
proj       main          3   this disk only
```

It captures **tracked changes and untracked files together**, honouring each
repository's `.gitignore`, by staging into a throwaway index. Your own index is
never touched: restaging your work while taking a snapshot would silently change
what your next commit contains, which is worse than missing a file.

Credentials are looked for before anything is recorded, and a hit **blocks** the
snapshot rather than warning. Ignore rules govern paths, and no path-based rule
helps here: a key pasted into a source file while debugging sits in a file nobody
would ever have listed. A blocked snapshot is fixed in seconds; a key that reached
the server is not.

### Putting it down elsewhere

Never automatic. `weft land` is a command you type, because silently applying a
patch to a working tree is exactly the gesture that destroys whatever a parallel
session was doing there.

```
weft land --dry-run          # say what would happen, write nothing
weft land                    # apply it
weft land --3way             # let git reconcile a different base commit
```

It refuses, with the remedy, when:

- **this checkout already has uncommitted changes** (`--force` to override),
- **it is on a different commit** than the work was taken on (`--3way` to reconcile),
- **the patch does not apply**, checked before anything is written.

## The full-screen view

```
weft tui
```

Opens on whatever wants attention, and straight on the conflicts when there are
any: they are the only thing here that blocks work, and a screen that makes you
navigate to the problem buries it.

Its reason to exist is the conflict view. Two versions side by side, aligned line
for line, **framed on the difference**:

```
   #  ours (here)                     #  theirs
 ──────────────────────────────────────────────────────
   3  ## Architecture                 3  ## Architecture
   4  …erriere nginx.                 4  …erriere Traefik.
   5                                  5
   7  …avec un bind mount.            7  …avec un volume nomme.
```

Two long lines that differ near their end would otherwise truncate to the same
visible prefix, and you would be shown two identical strings and asked to choose
between them. Both sides shift by the same amount, and both columns are pinned to
the same width, so neither version is shown more fully than the other.

`o` keeps yours, `t` takes theirs. Both do exactly what deleting the
`.weft-theirs` companion by hand does, so resolving here and resolving in an
editor cannot mean different things.

The state machine behind it is a pure function of state and key, kept apart from
the drawing and the key loop. That is why the navigation rules, the clamps and the
resolutions are unit tests rather than something verified by looking at it once,
which is exactly what could not be done for the widget library this was chosen
over.

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

Behind both, a **secret scanner** reads what a snapshot is about to record, in
loose files and in uncommitted work alike, and refuses the whole snapshot rather
than warning. A path rule cannot catch a key pasted into a source file while
debugging, because that path is one nobody would ever have thought to list. It
runs only on content the store does not already hold, so an unchanged tree is not
re-read, and it stays deliberately narrow: a scanner that fires on ordinary source
teaches people to reach for a bypass, and then it protects nothing.

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
- [x] Uncommitted work capture and transfer
- [x] TUI
- [x] Self-update

## Contributing

Read [DESIGN.md](DESIGN.md) first. Section 9 lists the invariants a change must
not break.

One convention worth knowing: a guard is not trusted here until it has been seen
to fail. When you add a test that protects an invariant, break the invariant on
purpose and confirm the test goes red. A guard whose scope has drifted looks
exactly like one that works.

## Licence

Copyright 2026 Puwapi. Licensed under the [Apache License, Version 2.0](LICENSE).

The licence text is left verbatim, as Apache asks; the attribution lives here
rather than inside it.
