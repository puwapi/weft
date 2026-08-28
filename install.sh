#!/bin/sh
# Installs weft on macOS or Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/puwapi/weft/main/install.sh | sh
#
# Reads, in order: WEFT_VERSION (default: the latest release) and WEFT_BIN_DIR
# (default: ~/.local/bin, or /usr/local/bin when it is writable).
set -eu

REPO=puwapi/weft

say()  { printf '%s\n' "$*"; }
die()  { printf 'weft install: %s\n' "$*" >&2; exit 1; }

# --- what are we on ---

os=$(uname -s)
case "$os" in
  Darwin) os=macos ;;
  Linux)  os=linux ;;
  *)      die "unsupported system '$os'. weft ships for macOS, Linux and Windows; for Windows use install.ps1." ;;
esac

arch=$(uname -m)
case "$arch" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) die "unsupported architecture '$arch'. Build from source: https://github.com/$REPO" ;;
esac

asset="weft-$os-$arch"

# The binaries link against glibc. Alpine and other musl systems need a build
# from source, and saying so beats a download that fails at exec time with
# "not found" for a file that is plainly there.
if [ "$os" = linux ] \
   && [ ! -e /lib/ld-linux-x86-64.so.2 ] && [ ! -e /lib/ld-linux-aarch64.so.1 ] \
   && { [ -e /lib/ld-musl-x86_64.so.1 ] || [ -e /lib/ld-musl-aarch64.so.1 ]; }; then
  die "this looks like a musl system (Alpine). The published binaries need glibc.
     Build from source instead: https://github.com/$REPO#building"
fi

command -v curl >/dev/null 2>&1 || die "curl is required"

# --- which version ---

version=${WEFT_VERSION:-}
if [ -z "$version" ]; then
  # Quiet on purpose: the message below says more than curl's status line does.
  version=$(curl -fsL -o - "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null \
    | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)
  [ -n "$version" ] || die "could not work out the latest version. Set WEFT_VERSION to pick one."
fi

base="https://github.com/$REPO/releases/download/$version"

# --- where does it go ---

bindir=${WEFT_BIN_DIR:-}
if [ -z "$bindir" ]; then
  if [ -w /usr/local/bin ] 2>/dev/null; then bindir=/usr/local/bin; else bindir="$HOME/.local/bin"; fi
fi
mkdir -p "$bindir" || die "cannot create $bindir"
[ -w "$bindir" ] || die "$bindir is not writable. Set WEFT_BIN_DIR to somewhere it is."

# --- fetch, verify, install ---

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT INT TERM

say "weft $version  ->  $bindir"
curl -fsSL "$base/$asset" -o "$tmp/weft" || die "download failed: $base/$asset"

# Verified against the checksums published with the release. Without this step
# the pipe above is a promise that nothing went wrong in transit, which is not
# something a download can promise.
if curl -fsSL "$base/SHA256SUMS" -o "$tmp/SHA256SUMS" 2>/dev/null; then
  expected=$(sed -n "s/^\([0-9a-f]*\)  *$asset\$/\1/p" "$tmp/SHA256SUMS" | head -1)
  if [ -n "$expected" ]; then
    if command -v sha256sum >/dev/null 2>&1; then actual=$(sha256sum "$tmp/weft" | cut -d' ' -f1)
    elif command -v shasum   >/dev/null 2>&1; then actual=$(shasum -a 256 "$tmp/weft" | cut -d' ' -f1)
    else actual=""; say "  (no sha256 tool found; checksum not verified)"; fi

    if [ -n "$actual" ] && [ "$actual" != "$expected" ]; then
      die "checksum mismatch. Expected $expected, got $actual. Not installing."
    fi
    [ -n "$actual" ] && say "  checksum ok"
  fi
else
  say "  (no SHA256SUMS published for $version; checksum not verified)"
fi

chmod +x "$tmp/weft"
mv "$tmp/weft" "$bindir/weft"

say ""
"$bindir/weft" --version || die "the binary does not run on this system"

# git is not bundled and never will be: weft delegates every repository operation
# to it precisely so that its behaviour matches yours, hooks and config included.
command -v git >/dev/null 2>&1 || say "
  Note: git is not on PATH. weft needs it for every repository operation."

case ":$PATH:" in
  *":$bindir:"*) ;;
  *) say "
  $bindir is not on your PATH. Add it:
      echo 'export PATH=\"$bindir:\$PATH\"' >> ~/.profile" ;;
esac

say "
Next:  weft init      in the directory that holds your repositories"
