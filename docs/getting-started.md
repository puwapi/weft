# Getting started

Walk this once, on two machines, and you will have a monorepo that stays in step.
It takes about fifteen minutes, most of which is the server.

---

## What you need

- **git**, on every machine. weft never bundles it and never will: it hands every
  repository operation to your git, so hooks, credential helpers and config all
  behave exactly as they already do.
- **A place to run the server.** Anything that runs Docker and that all your
  machines can reach. It holds encrypted blobs and never sees your key.
- Nothing else. The binary carries its own runtime.

---

## 1. Install

### macOS and Linux

```bash
curl -fsSL https://raw.githubusercontent.com/puwapi/weft/main/install.sh | sh
```

It works out your system and architecture, verifies the download against the
checksums published with the release, and puts `weft` in `/usr/local/bin` if that
is writable or `~/.local/bin` otherwise.

To choose for yourself:

```bash
curl -fsSL https://raw.githubusercontent.com/puwapi/weft/main/install.sh \
  | WEFT_BIN_DIR=~/bin WEFT_VERSION=v0.3.0 sh
```

### Windows

```powershell
irm https://raw.githubusercontent.com/puwapi/weft/main/install.ps1 | iex
```

Installs to `%LOCALAPPDATA%\weft\bin` and adds it to your user PATH. No elevation,
and nothing changes for anyone else on the machine. Open a new terminal
afterwards so the PATH change takes effect.

### By hand

Download the binary for your platform from
[the releases page](https://github.com/puwapi/weft/releases), make it executable,
and put it somewhere on your PATH. The names are `weft-<os>-<arch>`.

### Check it

```bash
weft --version
```

---

## 2. Your first machine

Go to the directory that **holds** your repositories. Not into one of them:
weft manages what is between and around them.

```bash
cd ~/projects/mymonorepo
weft init
```

Three things happen.

**A workspace is created.** `.weft/` holds the object store and this machine's
bookkeeping.

**Two rule files appear.** `.weftignore` is cleanliness, `.weftnever` is
confidentiality. They are described in the [README](../README.md#rules); the
defaults are sensible and you can edit them later.

> If a `.stignore` is already there, weft imports it, including rules whose only
> clue that they are sensitive is the comment above them. Every reclassification
> is printed so you can move one back.

**A workspace key is printed, once.**

```
┌─ Workspace key: write this down ──────────────────────────────────┐
│ weft-QKVD6PTC-G75P1ZRM-NY8VV59T-HXQ3Q5E7-PV44SJ2T-35PQNDJ4-184G   │
└───────────────────────────────────────────────────────────────────┘
```

**Write it down now.** Your files are encrypted with it before they reach the
server, so the server cannot read them and cannot help you recover it. Every
other machine needs this exact key. You can print it again with
`weft key show --reveal`, but only from a machine that already has it.

### See what weft sees

```bash
weft scan
```

Repositories, working trees, loose files, and anything refused as confidential.
It also flags trees that are **at risk**: worktrees in a temp directory the OS
may reclaim, or outside the workspace where weft cannot reach them.

### Record it

```bash
weft snapshot
```

This captures the loose files and, by default, the **uncommitted work** in every
checkout. That is the safety net: a branch that lives on one disk stops being
invisible everywhere else.

```bash
weft carry
```

Answers the question directly: does this work exist anywhere but here?

---

## 3. Put up a server

One container, one volume, no database to run.

First, a join secret. Machines present it once, when they enrol:

```bash
openssl rand -hex 32
```

Then either one command:

```bash
docker run -d --name weft-server \
  -v weft-data:/data \
  -e Weft__JoinSecret=<the-secret> \
  -p 8080:8080 \
  ghcr.io/puwapi/weft-server:latest
```

If that says `denied` or `not found`, the package is private: make it public once
from the repository's Packages page, or use the compose file below, which builds
from source when it cannot pull.

Or, if you prefer a compose file:

```bash
curl -fsSLO https://raw.githubusercontent.com/puwapi/weft/main/docker-compose.yml
echo "WEFT_JOIN_SECRET=<the-secret>" > .env
docker compose up -d
```

The server refuses to start without a join secret, rather than running as
something nobody can join.

Everything it holds is in `/data`: back that volume up and you have backed up the
server. It cannot read any of it.

Put it behind whatever gives you TLS. It speaks plain HTTP on 8080 and expects a
reverse proxy in front.

**HTTPS is not optional here, and weft enforces it.** Your files are encrypted
either way, but the join secret and each machine's token are not: anyone on the
path could take one, enrol, and download every object. `weft remote add` refuses a
plain-HTTP address unless the host is local:

```
Refusing to send credentials over plain HTTP to weft.example.com.
Your files would still be encrypted, but the join secret and this machine's
token would not.
```

`--insecure` exists for a server on a private network you already trust. It says
so again on every push and pull, because a warning shown once during setup is a
warning nobody has seen.

This is not left to good intentions. `weft remote add` refuses an `http://` URL
for anything but your own machine. If the server is only reachable over a private
network and you accept the trade, `--insecure` allows it and repeats the warning
on every push and pull.

Check it:

```bash
curl https://weft.example.com/v1/info
```

---

## 4. Connect the first machine

```bash
weft remote add https://weft.example.com --join <the-secret>
weft push
```

`remote add` exchanges the join secret for a token belonging to this machine
alone. That token, not the secret, is what every later request carries, so losing
one machine does not mean rotating everything:

```bash
weft remote machines                        # who is enrolled
weft remote revoke <id> --join <the-secret>
```

That withdraws one machine's token and keeps everything it recorded, because the
machine you are revoking is usually the one you lost, and its last snapshot is
what you are trying to get back. It asks for the join secret rather than using
this machine's token: the lost machine has a token, and does not have the secret.

One thing it cannot do, said plainly because the opposite belief is dangerous:
that machine still has the workspace key on its disk and every object it already
fetched. If it is in someone else's hands, you want a new key and a fresh server.
Revocation closes the door, it does not empty the room.

---

## 5. Your second machine

Install weft, then:

```bash
cd ~/projects
mkdir mymonorepo && cd mymonorepo

weft init
weft key set weft-QKVD6PTC-...        # the key from the first machine
weft remote add https://weft.example.com --join <the-secret>
weft pull
weft merge
```

`weft init` on this machine generates its own key, which is the wrong one.
`weft key set` replaces it. If you skip that step, `remote add` refuses:

```
That server holds a different workspace.
Server: a23c4821b650   here: 5341b0471f3f
```

Caught at enrolment rather than after the first push, because a machine with the
wrong key uploads objects nobody else can read and the mistake surfaces much
later, on somebody else's machine.

After `weft merge`, your files are there.

---

## 6. Day to day

On the machine you have been working on:

```bash
weft snapshot && weft push
```

On the other one:

```bash
weft pull && weft merge
```

`pull` fetches; `merge` reconciles. They are separate so a merge cannot
half-finish because a connection dropped, and so you can merge on a train.

Most differences never reach you. When one does:

```bash
weft tui
```

opens on the conflicts, shows both versions side by side, and settles them with
one key. Or handle it in your editor: the file is untouched and still working,
your version is in it, and theirs is beside it as `<file>.weft-theirs`. Delete
that companion to say you are done, then `weft merge --continue`.

### Carrying work in progress

```bash
# where you were working
weft snapshot && weft push

# on the other machine
weft pull
weft land --dry-run     # what would happen
weft land               # apply it
```

`land` refuses a checkout that already has uncommitted changes, refuses one on a
different commit unless you pass `--3way`, and checks the patch before writing
anything. It is never automatic: applying a patch to a working tree on its own is
how you destroy whatever was going on there.

---

## Keeping weft current

```bash
weft up --check     # what is published, changes nothing
weft up             # install it
```

The download is **always** checked against the checksum published beside it, and
there is no flag to skip that. This replaces the binary you run; a download nobody
checked is a way to hand somebody else's code the same trust.

Nothing happens if you are already current, and nothing happens if your build is
newer than what is published, which is what a build from source usually is.
`--force` overrides both.

The server refuses **writes** from a build below its floor, and says so:

```
This server accepts writes from weft 0.3.0 or later; you are running 0.2.1.
Run 'weft up'.
```

Reads are never blocked. Stranding an outdated machine with no way to fetch its
own work is a worse problem than letting it run stale.

> On Windows the previous binary cannot be deleted while it is running, so it is
> moved aside and removed the next time weft starts. That is why an update there
> leaves a file behind for a moment.

`weft up` exists from **0.4.0** onward. A 0.3.x build has no such command and does
not grow one by itself: run the installer again to get past it, once.

## Platform notes

Everything works the same on all three, with four differences worth knowing.

**Case.** macOS and Windows cannot tell `README.md` from `readme.md`; Linux can.
If a Linux machine holds both, weft reports them as a conflict on the others
rather than writing one over the other. It probes the filesystem rather than
trusting the OS name, because macOS can be case-sensitive and a mounted share can
be either.

**The executable bit** travels; read and write permissions do not. They belong to
the account that will hold the file, and copying them between machines with
different users is how a synchroniser makes files unreadable on arrival. On
Windows the bit is simply absent, which is correct.

**Line endings.** For loose files, weft preserves what it found: a CRLF file
stays CRLF, and a merge does not rewrite every line of it.

Inside a repository, git decides, as it should. With `core.autocrlf` on, which is
the Windows default, work carried from a Mac lands with CRLF endings. That is git
honouring your configuration, and weft hands it the patch precisely so that it
can.

**The workspace key file.** On macOS and Linux it is written `0600`. On Windows it
inherits the ACL of the directory it is in, which under your user profile is
private to you. If you put a workspace somewhere shared, the key is only as
protected as that directory.

**Alpine and other musl systems** need a build from source: the published Linux
binaries link against glibc. The installer detects this and says so rather than
letting the download fail at exec time.

---

## Where things live

| | |
|---|---|
| `.weft/` | object store, this machine's HEAD, the key, the server token |
| `.weftignore` | what regenerates. Overridable. |
| `.weftnever` | what is confidential. Not overridable, not by `--force`. |
| `~/.weft/machine.json` | this machine's identity, shared by every workspace on it |

The key and the server token are written owner-only. The identity is a random
UUIDv7, never derived from the hostname: a hostname changes, two machines can
share one, and cloned virtual machines share both.

---

## When something goes wrong

**`weft push` says the build is too old.** The server refuses writes below a
version floor. Reads are never blocked, so you can still fetch your own work.
Update and try again.

**`weft merge` says objects are missing.** Run `weft pull` first: merge
reconciles what is already here and never goes to the network.

**A snapshot is refused for a credential.** Something weft was about to record
looks like a key. The message names the file and the line, with the secret masked,
and says whether it found it in a file or in uncommitted work. Take it out; add
the path to `.weftnever` if the file itself is the secret; or, when everything
listed is uncommitted work, `weft snapshot --no-carry` records the rest without
it. It blocks rather than warns because a blocked snapshot is fixed in seconds and
a key that reached the server is not.

**`weft tui` says it needs a terminal.** Its input or output is redirected. Every
other command works in a pipe.

**Nothing is ever lost.** Every version that has been snapshotted is in the object
store, addressable and restorable. A bad merge, a wrong resolution, a file taken
from the wrong machine: all recoverable.
