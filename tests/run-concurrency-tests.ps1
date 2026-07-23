# Cross-process state safety - 17 checks. Runs on BOTH Windows and Linux.
#
# The agent is not one process. Autorun keeps the daemon alive while Steam starts a SECOND process
# (`savelocker run -- %command%`), and on Windows the tray runs alongside any CLI command. They share
# config.json, offline-queue.json, health-events.json and the temp archive directory, and their locks
# used to be in-process only - a SemaphoreSlim and a `lock` statement, both of which do nothing
# across a process boundary.
#
# Each check proves a CONCURRENT failure does not happen, so each must be able to fail against the
# old code:
#
#   1. Lost update      - two processes push the same game; the parent version one recorded must not
#                         be erased by the other's whole-file write. This is the damaging one: a lost
#                         LastKnownVersionId makes the NEXT push present a stale parent, and the
#                         server rejects it as a conflict. One machine, conflicting with itself.
#   2. Torn read        - config.json is sampled continuously during the storm. WriteAllText
#                         truncates before writing, so a reader can catch it empty; every sample must
#                         parse as JSON, or the agent loses its API key and game list on next load.
#   3. Queue merge      - with the server DOWN, two processes queue different games. A whole-collection
#                         write means the last writer erases the other's entry - dropping a save that
#                         was never uploaded, which is the one thing this queue exists to prevent.
#   4. Health merge     - same shape for pending events. The launch wrapper records and exits; the
#                         daemon delivers. If its write erases the wrapper's event, a Deck's failure
#                         never reaches the console - and the console is the Deck's only UI.
#   5. Temp archives    - two pushes of one game must not share a temp archive path, or one deletes
#                         the other's file mid-upload.
#   6. Stale reader     - the mirror of 1, and the half that was missing. A long-lived process must
#                         not PUSH a parent that another process has already superseded. Checks 1-5
#                         prove the daemon does not ERASE another's version; nothing proved it does
#                         not USE a dead one. It did, and the server correctly rejected every push
#                         forever after - 75 conflicts and 2.66 GB on a real Deck. See
#                         SaveLocker/logs/2026-07-23_conflict-storm.md.
#   7. Reconcile write  - the daemon's POLL path, which no check above ever exercised: checks 1-5
#                         only ever drive its PUSH path (SaveGameSyncState). ReconcileGamesAsync
#                         calls AgentConfig.Save(), which used to serialize the whole in-memory
#                         object and rewind a parent version another process had just recorded.
#
# Owns its server on :5183. Usage: .\tests\run-concurrency-tests.ps1 / pwsh tests/run-concurrency-tests.ps1

$ErrorActionPreference = "Continue"

$onWindows = if ($null -eq $IsWindows) { $true } else { $IsWindows }

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify-concurrency"
$server  = "http://localhost:5183"

if ($onWindows) {
    $inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    $dotnet = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
    $dll    = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
} else {
    $dotnet = "dotnet"
    $dll    = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
}
if (-not (Test-Path $dll)) { Write-Host "Agent not built: $dll"; exit 2 }

# `daemon` is a Linux-host command, but the state it contends over is Agent.Core's — the same code
# the Windows tray runs as its long-lived process. Driving the daemon on either OS exercises the
# shared paths, and on Windows it also proves the tray's binary and the daemon's agree about them.
$daemonDll = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
if (-not (Test-Path $daemonDll)) { Write-Host "Linux agent not built: $daemonDll"; exit 2 }

$serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
if (-not (Test-Path $serverDll)) { Write-Host "Server not built: $serverDll"; exit 2 }

Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $scratch | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $dll @args 2>&1 }

# Run several agent processes at once and wait for all of them. This is the whole point of the
# suite: they must be genuinely concurrent OS processes, not threads.
function Start-Agents($argSets) {
    $procs = @()
    foreach ($set in $argSets) {
        $all = @($dll) + $set
        $procs += if ($onWindows) {
            Start-Process -FilePath $dotnet -ArgumentList $all -PassThru -WindowStyle Hidden
        } else {
            Start-Process -FilePath $dotnet -ArgumentList $all -PassThru
        }
    }
    foreach ($p in $procs) { $p.WaitForExit() }
    return $procs
}

function Start-TestServer {
    $env:ASPNETCORE_URLS      = $server
    $env:Storage__DbPath      = Join-Path $scratch "state/savelocker.db"
    $env:Storage__ArchiveRoot = Join-Path $scratch "state/archives"
    $env:Backup__Enabled      = "false"
    $p = if ($onWindows) {
        Start-Process -FilePath $dotnet -ArgumentList $serverDll -PassThru -WindowStyle Hidden
    } else {
        Start-Process -FilePath $dotnet -ArgumentList $serverDll -PassThru
    }
    foreach ($i in 1..40) {
        Start-Sleep -Milliseconds 700
        try { Invoke-RestMethod "$server/api/admin/status" -TimeoutSec 3 | Out-Null; return $p } catch { }
    }
    return $p
}

New-Item -ItemType Directory -Force (Join-Path $scratch "state/archives") | Out-Null
$serverProc = Start-TestServer

try {
    $stamp = Get-Date -Format "HHmmss"
    $cfg   = Join-Path $scratch "agent.json"

    $saveA = Join-Path $scratch "saveA"; New-Item -ItemType Directory -Force $saveA | Out-Null
    $saveB = Join-Path $scratch "saveB"; New-Item -ItemType Directory -Force $saveB | Out-Null
    $saveC = Join-Path $scratch "saveC"; New-Item -ItemType Directory -Force $saveC | Out-Null
    "progress A" | Set-Content (Join-Path $saveA "slot1.sav") -Encoding utf8
    "progress B" | Set-Content (Join-Path $saveB "slot1.sav") -Encoding utf8
    "progress C" | Set-Content (Join-Path $saveC "slot1.sav") -Encoding utf8

    @{ ServerUrl = $server; Games = @() } | ConvertTo-Json | Set-Content -Path $cfg -Encoding utf8
    Agent register --name "Concurrent-$stamp" --config $cfg | Out-Null

    $gameA = "ConcGameA-$stamp"
    $gameB = "ConcGameB-$stamp"
    $gameC = "ConcGameC-$stamp"
    Agent add-game --name $gameA --dir $saveA --config $cfg | Out-Null
    Agent add-game --name $gameB --dir $saveB --config $cfg | Out-Null
    Agent add-game --name $gameC --dir $saveC --config $cfg | Out-Null

    # =================================================================================
    # 1. LOST UPDATE — a long-lived process holding a stale config vs. a short-lived one
    # =================================================================================
    # Racing N identical CLI pushes does NOT reproduce this: process startup dominates, so their
    # write windows never overlap and the test passes against the broken code too (verified). The
    # damage needs the real shape — a process that loaded config.json MINUTES ago and writes it back
    # from memory. That is exactly the daemon, and it is why the daemon (not a push storm) is the
    # second actor here. Ordering is enforced by waiting on observable state, not by luck.
    $daemonArgs = @($daemonDll, "daemon", "--port", "5189", "--config", $cfg)
    $daemon = if ($onWindows) {
        Start-Process -FilePath $dotnet -ArgumentList $daemonArgs -PassThru -WindowStyle Hidden
    } else {
        Start-Process -FilePath $dotnet -ArgumentList $daemonArgs -PassThru
    }
    foreach ($i in 1..40) {
        Start-Sleep -Milliseconds 700
        try { Invoke-WebRequest "http://localhost:5189/" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { }
    }
    Check "daemon is up and holding config in memory" ($daemon -and -not $daemon.HasExited)

    # The daemon now holds a config in which NEITHER game has a version. A separate process pushes
    # game A and records its parent version on disk.
    Agent push $gameA --force --config $cfg | Out-Null
    $afterPush = Get-Content $cfg -Raw | ConvertFrom-Json
    $versionA  = ($afterPush.Games | Where-Object { $_.Name -eq $gameA }).LastKnownVersionId
    Check "the CLI push recorded a parent version for A" (-not [string]::IsNullOrWhiteSpace($versionA))

    # Now make the daemon write config.json, by giving its folder watcher something to push in
    # game B. A whole-object Save() from the daemon's stale copy erases A's version here.
    "daemon-triggered change" | Set-Content (Join-Path $saveB "slot1.sav") -Encoding utf8

    $versionB = $null
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 1
        try {
            $now = Get-Content $cfg -Raw | ConvertFrom-Json
            $versionB = ($now.Games | Where-Object { $_.Name -eq $gameB }).LastKnownVersionId
            if (-not [string]::IsNullOrWhiteSpace($versionB)) { break }
        } catch { }
    }
    Check "the daemon pushed B and wrote config" (-not [string]::IsNullOrWhiteSpace($versionB))

    # THE ASSERTION. A's version must have survived the daemon's write.
    $final  = Get-Content $cfg -Raw | ConvertFrom-Json
    $stillA = ($final.Games | Where-Object { $_.Name -eq $gameA }).LastKnownVersionId
    Check "the daemon's write did NOT erase A's parent version" ($stillA -eq $versionA)

    # The consequence, end to end: a lost parent makes the next push present a stale parent and the
    # server rejects it. One machine, conflicting with itself.
    "later progress A" | Set-Content (Join-Path $saveA "slot1.sav") -Encoding utf8
    $next = Agent push $gameA --config $cfg
    Check "a later push does NOT conflict with itself" (-not ("$next" -match "CONFLICT"))

    $status = Agent status --config $cfg
    Check "server reports no conflict for the game"    (-not ("$status" -match "CONFLICT"))

    # =================================================================================
    # 5. TEMP ARCHIVES are per-process, so one push cannot delete another's mid-upload
    # =================================================================================
    $tmpDir = Join-Path $scratch "tmp"
    $leftovers = if (Test-Path $tmpDir) { @(Get-ChildItem $tmpDir -Filter "*.zip") } else { @() }
    Check "no temp archives leaked after the storm" ($leftovers.Count -eq 0)

    # =================================================================================
    # 6. STALE READER - a long-lived process must not push a SUPERSEDED parent
    # =================================================================================
    # The daemon loaded config.json at boot and holds TrackedGame references for its whole lifetime
    # (Daemon.StartFolderWatchers); nothing re-read them. So once another process pushed, every
    # watch-push here presented the boot-time parent, the server correctly recorded a conflict, and
    # because the conflict path deliberately does not advance the pointer, it never recovered.
    #
    # Quiesce first. Check 1 modified saveA and the daemon watches it, so without a settled start
    # this races the settle gate and passes or fails on timing rather than on correctness. That
    # timing accident is why the existing suite came close to this bug and never caught it.
    Start-Sleep -Seconds 20

    $preA = ((Get-Content $cfg -Raw | ConvertFrom-Json).Games | Where-Object { $_.Name -eq $gameA }).LastKnownVersionId
    "superseding progress A" | Set-Content (Join-Path $saveA "slot1.sav") -Encoding utf8
    Agent push $gameA --force --config $cfg | Out-Null
    $cliA = ((Get-Content $cfg -Raw | ConvertFrom-Json).Games | Where-Object { $_.Name -eq $gameA }).LastKnownVersionId
    Check "the CLI superseded A's parent version" ($cliA -ne $preA)

    # The daemon's in-memory copy of A is now behind the file. Give its watcher something to push.
    "post-supersede progress A" | Set-Content (Join-Path $saveA "slot1.sav") -Encoding utf8
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 1
        $nowA = ((Get-Content $cfg -Raw | ConvertFrom-Json).Games | Where-Object { $_.Name -eq $gameA }).LastKnownVersionId
        if ($nowA -ne $cliA) { break }
    }

    $ovA = (Invoke-RestMethod "$server/api/overview") | Where-Object { $_.game.name -eq $gameA }
    Check "the daemon's watch-push did NOT conflict on a superseded parent" (-not $ovA.hasOpenConflict)

    # =================================================================================
    # 7. RECONCILE must not rewind sync state - Save() is not the writer of sync state
    # =================================================================================
    # Game C is the discriminator, for the same reason it is in the offline section below: the
    # daemon watches saveC but nothing ever modifies it, so the daemon has never pushed C and its
    # in-memory copy still carries the boot-time null. A whole-object Save() writes that null over
    # the CLI's version - and C's next push then presents a stale parent and is rejected.
    Agent push $gameC --force --config $cfg | Out-Null
    $cliC = ((Get-Content $cfg -Raw | ConvertFrom-Json).Games | Where-Object { $_.Name -eq $gameC }).LastKnownVersionId
    Check "the CLI recorded a parent version for C" (-not [string]::IsNullOrWhiteSpace($cliC))

    # Force a reconcile change the daemon must persist. A literal JSON array, not ConvertTo-Json:
    # PowerShell 5.1 unwraps a single-element array to a bare string and the endpoint takes string[].
    $gameCId = ((Invoke-RestMethod "$server/api/overview") | Where-Object { $_.game.name -eq $gameC }).game.id
    Invoke-RestMethod "$server/api/games/$gameCId/excludes" -Method Post `
        -Body '["*.conctest"]' -ContentType "application/json" | Out-Null

    $globApplied = $false
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 1
        $c = (Get-Content $cfg -Raw | ConvertFrom-Json).Games | Where-Object { $_.Name -eq $gameC }
        if ($c.ExcludeGlobs -contains "*.conctest") { $globApplied = $true; break }
    }
    Check "the daemon reconciled the server's exclude change and wrote config" $globApplied

    # THE ASSERTION. Reconcile's write must not have rewound C's parent version.
    $finalC = ((Get-Content $cfg -Raw | ConvertFrom-Json).Games | Where-Object { $_.Name -eq $gameC }).LastKnownVersionId
    Check "reconcile's write did NOT rewind C's parent version" ($finalC -eq $cliC)

    # =================================================================================
    # 3 + 4. QUEUE and HEALTH survive a second writer WITH THE SERVER DOWN
    # =================================================================================
    # Same shape, same reason: the daemon loaded both files at startup and still believes they are
    # empty. A CLI push writes an entry; the daemon then writes its own. A whole-collection
    # overwrite erases the CLI's — losing a save that was never uploaded, and a failure report that
    # would have been a Deck owner's only signal.
    if ($serverProc -and -not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force }
    Start-Sleep -Seconds 3

    $queuePath  = Join-Path $scratch "offline-queue.json"
    $healthPath = Join-Path $scratch "health-events.json"

    # First writer: a short-lived CLI push for game C. C is the discriminator precisely BECAUSE the
    # daemon never touches it — its files are never modified, so no folder watcher fires and C can
    # only ever have reached the queue from the CLI process. (Using game A here would prove nothing:
    # the daemon watches saveA, so it already holds A in memory and would rewrite it either way.)
    Agent push $gameC --force --config $cfg | Out-Null

    $queuedFirst = if (Test-Path $queuePath) { @((Get-Content $queuePath -Raw | ConvertFrom-Json) | ForEach-Object { $_.GameName }) } else { @() }
    Check "the CLI push queued game C while offline" ($queuedFirst -contains $gameC)

    # Second writer: the long-lived daemon, whose queue and event set were loaded before any of that.
    "offline progress B" | Set-Content (Join-Path $saveB "slot1.sav") -Encoding utf8
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 1
        if (Test-Path $queuePath) {
            $names = @((Get-Content $queuePath -Raw | ConvertFrom-Json) | ForEach-Object { $_.GameName })
            if ($names -contains $gameB) { break }
        }
    }

    $queue = if (Test-Path $queuePath) { @(Get-Content $queuePath -Raw | ConvertFrom-Json) } else { @() }
    $queuedIds = @($queue | ForEach-Object { $_.GameName })
    Check "the daemon queued game B"                     ($queuedIds -contains $gameB)
    Check "the daemon's write did NOT erase C's entry"   ($queuedIds -contains $gameC)

    $health = if (Test-Path $healthPath) { Get-Content $healthPath -Raw | ConvertFrom-Json } else { $null }
    $healthGames = @($health.Events | ForEach-Object { $_.GameId })
    Check "health events file is valid JSON"             ($null -ne $health)
    Check "health kept events from BOTH processes"       (($healthGames | Select-Object -Unique).Count -ge 2)
}
finally {
    if ($daemon -and -not $daemon.HasExited) { Stop-Process -Id $daemon.Id -Force -ErrorAction SilentlyContinue }
    if ($serverProc -and -not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item Env:ASPNETCORE_URLS, Env:Storage__DbPath, Env:Storage__ArchiveRoot, Env:Backup__Enabled -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Concurrency: $pass passed, $fail failed."
if ($fail -gt 0) { exit 1 }
exit 0
