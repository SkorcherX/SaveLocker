# Cross-OS round-trip: prove a Windows save and a Proton save are actually interchangeable.
#
# This is the test that matters. Everything else in the suite is a proxy for it.
#
# GitHub Actions runners cannot share a network, so "one server, two agents" is realized by
# carrying the SERVER'S OWN STATE between jobs as an artifact: the SQLite DB plus the archive
# store are the entirety of what the server knows. The same server binary is restarted on top
# of it in the next job, on the other OS. A Windows agent pushes into it; a Linux agent pulls
# out of it; then back again.
#
# Legs (one per CI job, run in order, state handed along):
#   author    (Windows) author the fixture tree, push it, record the head hash
#   roundtrip (Linux)   pull it, byte-compare, assert the hash, then amend and push
#   confirm   (Windows) pull the amended tree, byte-compare, assert the hash
#
# Usage: powershell -File tests\cross-os\crossos.ps1 -Leg author     (Windows PowerShell 5.1)
#        pwsh tests/cross-os/crossos.ps1 -Leg roundtrip              (PowerShell Core)
#
# Deliberately ASCII-only, and 5.1-compatible. Windows PowerShell reads a BOM-less .ps1 as ANSI,
# so a literal non-ASCII character in this file would be decoded differently depending on the
# host. The non-ASCII SAVE FILENAME the test needs is therefore built from explicit code points
# below -- the fixture must be identical on both OSes, and it must not depend on how this script
# file happened to be decoded.

param(
    [Parameter(Mandatory)]
    [ValidateSet("author", "roundtrip", "confirm")]
    [string]$Leg,

    [int]$Port = 5179
)

$ErrorActionPreference = "Stop"

# $IsWindows is auto-defined by PowerShell Core but NOT by Windows PowerShell 5.1,
# where its absence is itself the signal that we are on Windows.
$onWindows  = if ($null -eq $IsWindows) { $true } else { $IsWindows }
$root       = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$serverUrl  = "http://127.0.0.1:$Port"

# Carried between jobs (the artifact). Not a dot-directory: upload-artifact skips hidden files
# by default, and a silently half-empty artifact would make the next leg fail for a reason that
# has nothing to do with the code under test.
$work       = Join-Path $root "crossos-work"
$state      = Join-Path $work "state"
$expected   = Join-Path $work "expected"      # the tree the pushing machine had
$hashFile   = Join-Path $work "head-hash.txt" # the content hash IT computed, on ITS OS

# Local to this leg (never carried -- absolute paths differ per OS, which is the whole point).
$localSave  = Join-Path $work "local-save"
$config     = Join-Path $work "agent-config.json"

$gameName   = "CrossOsGame"

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else       { Write-Host "FAIL: $name"; $script:fail++ }
}

# UTF-8 without a BOM, on both hosts. Set-Content -Encoding utf8 writes a BOM under 5.1 and no
# BOM under Core; a BOM would then ride along inside the hash file and break the comparison.
$utf8 = New-Object System.Text.UTF8Encoding($false)
function Write-Text($path, $text) { [IO.File]::WriteAllText($path, $text, $utf8) }
function Read-Text($path)         { [IO.File]::ReadAllText($path, $utf8).Trim() }

# ---- The agent under test: the host binary for THIS OS, same Agent.Core inside ---------------
if ($onWindows) {
    $agentDll  = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
    $agentName = "PC-Windows"
} else {
    # AssemblyName is 'savelocker' (the command users type), not the project name.
    $agentDll  = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
    $agentName = "Deck-Linux"
}
if (-not (Test-Path $agentDll)) { throw "Agent not built for this OS: $agentDll" }

function Agent { & dotnet $agentDll @args 2>&1 }

# Take the LAST line: the agent may log a line before the value we want.
function Get-AgentHash($dir) {
    return (Agent hash --config $config --dir $dir | Select-Object -Last 1).ToString().Trim()
}

# ---- Server: restarted on top of the carried state -------------------------------------------
# A server left listening from an earlier run is the worst possible failure here: the next leg
# would silently talk to a server holding STALE state, and the resulting conflict looks like a
# cross-OS hash bug. Refuse to start rather than produce a misleading result.
function Assert-PortFree {
    $inUse = $false
    try {
        $probe = New-Object Net.Sockets.TcpClient
        $probe.Connect("127.0.0.1", $Port)
        $inUse = $true
        $probe.Close()
    } catch { $inUse = $false }

    if ($inUse) {
        throw ("Port $Port is already in use -- something is listening there (a leftover test " +
               "server, or the dev server). Stop it first: this test must own the server, or it " +
               "would be asserting against another server's state.")
    }
}

function Start-TestServer {
    $serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
    if (-not (Test-Path $serverDll)) { throw "Server not built: $serverDll" }

    Assert-PortFree
    New-Item -ItemType Directory -Force (Join-Path $state "archives") | Out-Null

    $env:ASPNETCORE_URLS      = $serverUrl
    $env:Storage__DbPath      = Join-Path $state "savelocker.db"
    $env:Storage__ArchiveRoot = Join-Path $state "archives"

    $proc = Start-Process -FilePath "dotnet" -ArgumentList @($serverDll) -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $work "server.out.log") `
        -RedirectStandardError  (Join-Path $work "server.err.log")

    # /api/admin/status is the one route with no auth filter on it. /api/games looks like the
    # obvious probe but is an AGENT route: unauthenticated it answers 401, so a readiness loop
    # built on it never sees success and just burns its whole timeout.
    foreach ($i in 1..60) {
        try {
            Invoke-RestMethod -Uri "$serverUrl/api/admin/status" -TimeoutSec 2 | Out-Null
            Write-Host "Server up on $serverUrl (state: $state)"
            return $proc
        } catch { Start-Sleep -Seconds 1 }
    }

    # Kill what we spawned before giving up. The caller's `finally` cannot do it: it never
    # received the handle, so a leaked server would keep holding the port AND the state dir,
    # and every later run would quietly talk to it instead.
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Get-Content (Join-Path $work "server.err.log") -ErrorAction SilentlyContinue | Select-Object -Last 30
    throw "Server did not come up on $serverUrl"
}

# ---- The fixture tree: shapes that break naive cross-OS hashing -------------------------------
# Deliberately excludes *.log / *.tmp / *.bak / Thumbs.db / desktop.ini -- those are the server's
# default exclude globs, and a file that is archived on one machine but filtered on another would
# fail the byte-compare for a reason that has nothing to do with cross-OS interchange.
function New-FixtureTree($dir) {
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dir | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $dir "nested/deep/deeper") | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $dir "Profiles/Player One") | Out-Null

    # Byte-exact writes. Text cmdlets would "helpfully" normalise line endings per-platform and
    # the test would then be measuring PowerShell, not SaveArchive.
    function Put($rel, $text) {
        [IO.File]::WriteAllBytes((Join-Path $dir $rel), $utf8.GetBytes($text))
    }

    Put "slot1.sav"                     "level=1`n"                 # LF
    Put "crlf.sav"                      "level=1`r`nlives=3`r`n"    # CRLF must survive verbatim
    Put "no-trailing-newline.sav"       "checkpoint=42"
    Put "nested/deep/deeper/quest.sav"  "quest=open`n"              # depth
    Put "save with spaces.sav"          "spaces=yes`n"              # spaces in the name
    Put "Profiles/Player One/prefs.cfg" "volume=0.8`n"              # spaces in a directory

    # A non-ASCII filename, built from code points so it cannot be altered by this script's own
    # encoding: "unicode-name.sav" with u-diaeresis, i-diaeresis and e-acute.
    $unicodeName = [string]([char]0xFC) + "n" + [char]0xEF + "code-nam" + [char]0xE9 + ".sav"
    Put $unicodeName "unicode=filename`n"

    # Raw bytes, including NULs and every high byte -- nothing may be re-encoded in transit.
    [IO.File]::WriteAllBytes((Join-Path $dir "binary.dat"), [byte[]](0..255))
}

# ---- Byte-compare two trees -------------------------------------------------------------------
# Relative paths (normalised to '/') AND file bytes. Not timestamps: the archive does not carry
# them as content and the hash does not cover them, so comparing them would fail for the wrong
# reason.
function Get-TreeMap($dir) {
    $full = (Resolve-Path $dir).Path
    $map  = @{}
    foreach ($f in Get-ChildItem -Path $full -Recurse -File -Force) {
        $rel = $f.FullName.Substring($full.Length).TrimStart('\', '/').Replace('\', '/')
        $map[$rel] = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
    }
    return $map
}

function Compare-Tree($a, $b, $label) {
    $ma = Get-TreeMap $a
    $mb = Get-TreeMap $b

    $onlyA = @($ma.Keys | Where-Object { -not $mb.ContainsKey($_) } | Sort-Object)
    $onlyB = @($mb.Keys | Where-Object { -not $ma.ContainsKey($_) } | Sort-Object)
    $diff  = @($ma.Keys | Where-Object { $mb.ContainsKey($_) -and $ma[$_] -ne $mb[$_] } | Sort-Object)

    if ($onlyA) { Write-Host "  missing after round-trip: $($onlyA -join ', ')" }
    if ($onlyB) { Write-Host "  unexpected extra files:   $($onlyB -join ', ')" }
    if ($diff)  { Write-Host "  content differs:          $($diff  -join ', ')" }

    $ok = ($ma.Count -gt 0) -and -not $onlyA -and -not $onlyB -and -not $diff
    Check "$label ($($ma.Count) files, byte-identical)" $ok
}

# ---- Head hash as the SERVER recorded it (i.e. as the pushing OS computed it) ------------------
function Get-ServerHeadHash {
    $overview = Invoke-RestMethod -Uri "$serverUrl/api/overview" -TimeoutSec 10
    $entry = $overview | Where-Object { $_.game.name -eq $gameName }
    return $entry.head.contentHash
}

function New-AgentConfig {
    @{
        ServerUrl         = $serverUrl
        ManifestCachePath = (Join-Path $root "tests/manifest.yaml")
        Games             = @()
    } | ConvertTo-Json | Set-Content -Path $config -Encoding utf8
}

# ===============================================================================================
$osLabel = if ($onWindows) { "Windows" } else { "Linux" }
Write-Host "==== CROSS-OS LEG: $Leg  (on $osLabel, agent '$agentName') ===="

New-Item -ItemType Directory -Force $work | Out-Null
$server = $null

try {
    switch ($Leg) {

        # ---- Leg 1 (Windows): author the save and push it -------------------------------------
        "author" {
            # The author leg defines the baseline, so it MUST start from an empty server. If the
            # old state survives (a file still locked, say), the push lands against a stale head
            # and reports a conflict that has nothing to do with the code under test.
            Remove-Item -Recurse -Force $state -ErrorAction SilentlyContinue
            if (Test-Path $state) {
                throw "Could not clear $state -- is a server still running against it?"
            }
            $server = Start-TestServer

            New-FixtureTree $expected

            # Clear the target FIRST. `Copy-Item -Recurse src dst` copies src *into* dst when dst
            # already exists, rather than over it -- so a leftover local-save from an earlier run
            # would end up containing a nested copy of the fixture, and we would push that.
            Remove-Item -Recurse -Force $localSave -ErrorAction SilentlyContinue
            Copy-Item -Recurse -Force $expected $localSave

            New-AgentConfig
            $reg = Agent register --config $config --name $agentName
            Check "Windows agent registered" (($reg -join "`n") -like "*Registered '$agentName'*")

            Agent add-game --config $config --name $gameName --dir $localSave | Out-Null
            $push = Agent push --config $config $gameName
            Check "Windows agent pushed the fixture save" (($push -join "`n") -like "*pushed new version*")

            $localHash = Get-AgentHash $localSave
            $headHash  = Get-ServerHeadHash
            Check "server head hash == hash the Windows agent computed" ($localHash -eq $headHash)

            Write-Text $hashFile $headHash
            Write-Host "Head hash (computed on Windows): $headHash"
        }

        # ---- Leg 2 (Linux): pull what Windows pushed, prove it is identical, then push back ----
        "roundtrip" {
            $server = Start-TestServer
            $windowsHash = Read-Text $hashFile

            Remove-Item -Recurse -Force $localSave -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $localSave | Out-Null

            New-AgentConfig
            $reg = Agent register --config $config --name $agentName
            Check "Linux agent registered as a second machine" (($reg -join "`n") -like "*Registered '$agentName'*")

            # add-game matches the existing game by name (case-insensitive), so this maps the Linux
            # machine's own save directory onto the game Windows already pushed.
            Agent add-game --config $config --name $gameName --dir $localSave | Out-Null
            $pull = Agent pull --config $config $gameName
            Check "Linux agent pulled the Windows save" (($pull -join "`n") -like "*restored latest save*")

            # THE test: the tree Windows archived and the tree Linux restored are byte-identical.
            Compare-Tree $expected $localSave "Windows -> Linux round-trip"

            # Step 3 of the phase: the hash must agree ACROSS OSes. Relative paths are normalised
            # to '/', sorted Ordinal, and only content bytes are hashed -- so it should. If it does
            # not, divergence here silently manufactures conflicts between a PC and a Deck.
            $linuxHash = Get-AgentHash $localSave
            Check "content hash identical cross-OS (Linux recomputed == Windows)" ($linuxHash -eq $windowsHash)
            if ($linuxHash -ne $windowsHash) {
                Write-Host "  windows: $windowsHash"
                Write-Host "  linux:   $linuxHash"
            }

            # The same equality, now enforced by the real code path rather than by the test: a pull
            # stores the head hash (computed on Windows) as LastSyncedHash, so a push that finds no
            # local change is the agent itself confirming the two OSes hashed the bytes the same.
            # Any disagreement shows up here as a spurious upload or a CONFLICT.
            $noop = Agent push --config $config $gameName
            Check "push after cross-OS pull is a no-op (no phantom conflict)" `
                (($noop -join "`n") -like "*no local changes since last sync*")

            # Now Linux advances the save, and the tree it pushes becomes the next expectation.
            [IO.File]::WriteAllBytes((Join-Path $localSave "slot1.sav"), $utf8.GetBytes("level=2`n"))
            [IO.File]::WriteAllBytes((Join-Path $localSave "nested/deep/deeper/linux-only.sav"), $utf8.GetBytes("written=on-linux`n"))

            $push2 = Agent push --config $config $gameName
            Check "Linux agent pushed an amended save (v2)" (($push2 -join "`n") -like "*pushed new version*")

            $linuxHead = Get-ServerHeadHash
            Write-Text $hashFile $linuxHead

            Remove-Item -Recurse -Force $expected -ErrorAction SilentlyContinue
            Copy-Item -Recurse -Force $localSave $expected
            Write-Host "Head hash (computed on Linux): $linuxHead"
        }

        # ---- Leg 3 (Windows): pull what Linux pushed -- closing the round-trip -----------------
        "confirm" {
            $server = Start-TestServer
            $linuxHash = Read-Text $hashFile

            Remove-Item -Recurse -Force $localSave -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $localSave | Out-Null

            New-AgentConfig
            Agent register --config $config --name "PC-Windows-2" | Out-Null
            Agent add-game --config $config --name $gameName --dir $localSave | Out-Null

            $pull = Agent pull --config $config $gameName
            Check "Windows agent pulled the Linux save" (($pull -join "`n") -like "*restored latest save*")

            Compare-Tree $expected $localSave "Linux -> Windows round-trip"

            $winHash = Get-AgentHash $localSave
            Check "content hash identical cross-OS (Windows recomputed == Linux)" ($winHash -eq $linuxHash)
            if ($winHash -ne $linuxHash) {
                Write-Host "  linux:   $linuxHash"
                Write-Host "  windows: $winHash"
            }

            $noop = Agent push --config $config $gameName
            Check "push after cross-OS pull is a no-op (no phantom conflict)" `
                (($noop -join "`n") -like "*no local changes since last sync*")
        }
    }
}
finally {
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host "==== CROSS-OS ($Leg): $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
