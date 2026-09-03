<#
.SYNOPSIS
  Installs weft on Windows.

.EXAMPLE
  irm https://raw.githubusercontent.com/puwapi/weft/main/install.ps1 | iex

.PARAMETER Version
  Which release to install. Defaults to the latest. Piped into iex there is no
  way to pass a parameter, so set $env:WEFT_VERSION instead.

.PARAMETER BinDir
  Where to put it. Defaults to %LOCALAPPDATA%\weft\bin, which needs no elevation.
  Piped into iex, set $env:WEFT_BIN_DIR.
#>
# No [CmdletBinding()] on purpose. Piped into iex this is invoked as a plain
# script block in your session, where an advanced-function attribute buys nothing
# (nobody passes -Verbose to a pipe) and is one more thing that has to behave
# identically in Windows PowerShell 5.1, which is the shell this has to work in.
param(
    [string] $Version = $env:WEFT_VERSION,
    [string] $BinDir  = $env:WEFT_BIN_DIR
)

$repo = 'puwapi/weft'

# iex runs this in YOUR session rather than in a child one, so anything it sets
# is still set when it returns. Both preferences are put back at the end, and
# every failure below is a `throw` rather than `exit`: at the top level of an
# interactive session, which is exactly where iex puts us, `exit` closes the
# window and takes the error message with it.
$priorErrorAction = $ErrorActionPreference
$priorProgress    = $ProgressPreference
$ErrorActionPreference = 'Stop'

# Windows PowerShell renders a progress bar for every block it reads and spends
# longer drawing it than fetching: ten megabytes can take minutes with this on,
# which reads as a hang rather than as a download.
$ProgressPreference = 'SilentlyContinue'

try {
    # --- what are we on ---

    # Not [System.Runtime.InteropServices.RuntimeInformation]: that type lives in
    # a facade assembly Windows PowerShell 5.1 does not load, so asking it for the
    # architecture fails there with "Unable to find type" before anything is
    # downloaded at all. 5.1 is still what `powershell.exe` is on every Windows,
    # and it is what you get by pasting a command into the Start menu. These two
    # variables come from the operating system and exist in every shell.
    # ARCHITEW6432 is the one that tells the truth when a 32-bit shell runs on a
    # 64-bit Windows, where PROCESSOR_ARCHITECTURE only ever says x86.
    $machine = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 }
               else { $env:PROCESSOR_ARCHITECTURE }

    # An x64 shell on an arm64 Windows reports AMD64, so that machine gets the x64
    # build and runs it under emulation. Slower, and it works, which beats
    # refusing to install on a machine we cannot identify from inside a shell that
    # is itself emulated.
    $arch = switch ($machine) {
        'AMD64' { 'x64' }
        'ARM64' { 'arm64' }
        default { throw "weft install: unsupported architecture '$machine'. Build from source: https://github.com/$repo" }
    }
    $asset = "weft-windows-$arch.exe"

    # Windows PowerShell takes whatever the machine's .NET default is, which on
    # anything less than fully patched is still TLS 1.0. github.com answers 1.2
    # and above only, and refuses the rest as a connection reset, which says
    # nothing about the cause. -bor rather than assignment: keep what is enabled.
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    }

    # --- which version ---

    if (-not $Version) {
        try {
            $Version = (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing).tag_name
        } catch {
            throw "weft install: could not reach the GitHub API to work out the latest version ($($_.Exception.Message)). Set `$env:WEFT_VERSION to pick one."
        }
    }
    $base = "https://github.com/$repo/releases/download/$Version"

    # --- where does it go ---

    # Under LOCALAPPDATA rather than Program Files: no elevation, and it is
    # already the per-user convention other CLI tools follow.
    if (-not $BinDir) {
        if (-not $env:LOCALAPPDATA) { throw "weft install: LOCALAPPDATA is not set. Set `$env:WEFT_BIN_DIR to choose where weft goes." }
        $BinDir = Join-Path $env:LOCALAPPDATA 'weft\bin'
    }
    New-Item -ItemType Directory -Force -Path $BinDir | Out-Null
    $target = Join-Path $BinDir 'weft.exe'

    # --- fetch, verify, install ---

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("weft-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Force -Path $temp | Out-Null

    try {
        Write-Host "weft $Version  ->  $BinDir"

        $downloaded = Join-Path $temp 'weft.exe'
        try { Invoke-WebRequest "$base/$asset" -OutFile $downloaded -UseBasicParsing }
        catch { throw "weft install: download failed: $base/$asset ($($_.Exception.Message))" }

        # Verified against the checksums published with the release. Without this
        # the pipe into iex is a promise that nothing went wrong in transit, which
        # is not something a download can promise.
        #
        # Read from a file rather than from .Content: GitHub serves release assets
        # as application/octet-stream, and PowerShell 7 hands back a byte array
        # for those rather than a string. Splitting a byte array into lines
        # matches nothing, so the check passed silently over everything it exists
        # to catch, and said neither "checksum ok" nor "not verified".
        $sumsFile = Join-Path $temp 'SHA256SUMS'
        $expected = $null
        try {
            Invoke-WebRequest "$base/SHA256SUMS" -OutFile $sumsFile -UseBasicParsing
            $line = Get-Content $sumsFile |
                Where-Object { $_ -match "\s$([regex]::Escape($asset))\s*$" } |
                Select-Object -First 1
            if ($line) { $expected = ($line -split '\s+')[0] }
        } catch {
            # No checksums for this release. Warned about below rather than here,
            # so that a missing file and an unlisted asset say the same thing.
        }

        if ($expected) {
            $actual = (Get-FileHash $downloaded -Algorithm SHA256).Hash
            if ($actual.ToLowerInvariant() -ne $expected.ToLowerInvariant()) {
                throw "weft install: checksum mismatch for $asset. Expected $expected, got $actual. Not installing."
            }
            Write-Host '  checksum ok'
        } else {
            Write-Warning "no checksum published for $asset in $Version; this download was NOT verified."
        }

        # Windows will not let a running binary be replaced, and the error for it
        # names neither the process nor the reason.
        try { Move-Item $downloaded $target -Force }
        catch { throw "weft install: cannot write $target ($($_.Exception.Message)). If weft is running, close it and run this again." }
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # --- does it actually run ---

    Write-Host ''
    # Cleared first so that a code left behind by whatever you ran before this
    # cannot be mistaken for weft's own, and so that a binary which never starts
    # is distinguishable from one that started and failed.
    $global:LASTEXITCODE = $null
    try { & $target --version }
    catch { throw "weft install: $target was installed but will not start ($($_.Exception.Message))." }
    if ($LASTEXITCODE -ne 0) {
        $why = if ($null -eq $LASTEXITCODE) { 'it produced no exit code' } else { "it exited $LASTEXITCODE" }
        throw "weft install: $target does not run on this system ($why). Please report it: https://github.com/$repo/issues"
    }

    # git is not bundled and never will be: weft delegates every repository
    # operation to it precisely so its behaviour matches yours, hooks and config
    # included.
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Host "`n  Note: git is not on PATH. weft needs it for every repository operation."
    }

    # --- put it on PATH ---

    # The USER path, not the machine one: no elevation, and nothing changes for
    # anyone else on this computer.
    #
    # Through the registry rather than [Environment]::GetEnvironmentVariable,
    # which EXPANDS a REG_EXPAND_SZ value: a user path holding %USERPROFILE%\...
    # comes back with today's directory already substituted, and writing that
    # back bakes it in permanently for everything that reads the path afterwards.
    # Not fatal if it fails: weft is installed and working by this point, and
    # ending on a registry error would read as though the install had not
    # happened. Say what to do by hand instead.
    $key = $null
    try {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
        $current = [string] $key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
        $kind = if ($key.GetValueNames() -contains 'Path') { $key.GetValueKind('Path') }
                else { [Microsoft.Win32.RegistryValueKind]::ExpandString }

        # Whole entries, not a substring search: C:\tools\weft must not pass for
        # C:\tools\weft\bin, and a path holding a [ is a wildcard to -like.
        $entries = @($current -split ';' | Where-Object { $_ -ne '' })
        if ($entries -notcontains $BinDir) {
            $key.SetValue('Path', (($entries + $BinDir) -join ';'), $kind)
            Write-Host "`n  Added $BinDir to your PATH. Open a new terminal for it to take effect."
        }
    }
    catch {
        Write-Warning "weft is installed, but your PATH could not be updated ($($_.Exception.Message)). Add $BinDir to it by hand."
    }
    finally {
        if ($key) { $key.Close() }
    }

    # This session read its environment before that write, and iex means this IS
    # your session: without this line the very next command below does not resolve.
    if (($env:Path -split ';') -notcontains $BinDir) { $env:Path = "$env:Path;$BinDir" }

    Write-Host "`nNext:  weft init      in the directory that holds your repositories"
}
finally {
    $ErrorActionPreference = $priorErrorAction
    $ProgressPreference    = $priorProgress
}
