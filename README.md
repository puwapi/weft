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
weft init      # create a workspace, importing an existing .stignore if present
weft scan      # report what weft sees, changing nothing
weft snapshot  # record the state of the workspace
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
- [ ] Remote, and the sync protocol
- [ ] Three-way merge with format-aware drivers
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
