<#
.SYNOPSIS
  Installs weft on Windows.

.EXAMPLE
  irm https://raw.githubusercontent.com/puwapi/weft/main/install.ps1 | iex

.PARAMETER Version
  Which release to install. Defaults to the latest.

.PARAMETER BinDir
  Where to put it. Defaults to %LOCALAPPDATA%\weft\bin, which needs no elevation.
#>
[CmdletBinding()]
param(
    [string] $Version = $env:WEFT_VERSION,
    [string] $BinDir  = $env:WEFT_BIN_DIR
)

$ErrorActionPreference = 'Stop'
$repo = 'puwapi/weft'

function Fail($message) { Write-Error "weft install: $message"; exit 1 }

# --- what are we on ---

$arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64'   { 'x64' }
    'Arm64' { 'arm64' }
    default { Fail "unsupported architecture '$_'. Build from source: https://github.com/$repo" }
}
$asset = "weft-windows-$arch.exe"

# --- which version ---

if (-not $Version) {
    try {
        $Version = (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest").tag_name
    } catch {
        Fail 'could not work out the latest version. Pass -Version to pick one.'
    }
}
$base = "https://github.com/$repo/releases/download/$Version"

# --- where does it go ---

# Under LOCALAPPDATA rather than Program Files: no elevation, and it is already
# on the per-user PATH convention that other CLI tools use.
if (-not $BinDir) { $BinDir = Join-Path $env:LOCALAPPDATA 'weft\bin' }
New-Item -ItemType Directory -Force -Path $BinDir | Out-Null

# --- fetch, verify, install ---

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("weft-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null

try {
    Write-Host "weft $Version  ->  $BinDir"

    $downloaded = Join-Path $temp 'weft.exe'
    try { Invoke-WebRequest "$base/$asset" -OutFile $downloaded -UseBasicParsing }
    catch { Fail "download failed: $base/$asset" }

    # Verified against the checksums published with the release. Without this the
    # pipe into iex is a promise that nothing went wrong in transit, which is not
    # something a download can promise.
    try {
        $sums = (Invoke-WebRequest "$base/SHA256SUMS" -UseBasicParsing).Content
        $line = $sums -split "`n" | Where-Object { $_ -match "\s$([regex]::Escape($asset))\s*$" } | Select-Object -First 1

        if ($line) {
            $expected = ($line -split '\s+')[0]
            $actual = (Get-FileHash $downloaded -Algorithm SHA256).Hash.ToLowerInvariant()

            if ($actual -ne $expected.ToLowerInvariant()) {
                Fail "checksum mismatch. Expected $expected, got $actual. Not installing."
            }
            Write-Host '  checksum ok'
        }
    } catch {
        if ($_.Exception.Message -like '*checksum mismatch*') { throw }
        Write-Host "  (no SHA256SUMS published for $Version; checksum not verified)"
    }

    Move-Item $downloaded (Join-Path $BinDir 'weft.exe') -Force
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
& (Join-Path $BinDir 'weft.exe') --version

# git is not bundled and never will be: weft delegates every repository operation
# to it precisely so its behaviour matches yours, hooks and config included.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "`n  Note: git is not on PATH. weft needs it for every repository operation."
}

# Added to the USER path, not the machine one: no elevation, and it does not
# change anything for anyone else on this computer.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$BinDir*") {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$BinDir", 'User')
    Write-Host "`n  Added $BinDir to your PATH. Open a new terminal for it to take effect."
}

Write-Host "`nNext:  weft init      in the directory that holds your repositories"
