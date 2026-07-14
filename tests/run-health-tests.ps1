# Agent health reporting (Linux agent Phase 5) - 15 checks. Runs on BOTH Windows and Linux.
#
#   Windows: drives src/Agent (net10.0-windows).  Linux: drives src/Agent.Linux (net10.0).
#
# Health reporting lives in Agent.Core, so this is the same code on both hosts. The point of the
# phase: a headless Deck cannot raise a toast, so the failures it would have toasted go to the
# server and the console shows them (Decisions.md 2).
#
# These checks drive REAL failures through the agent (a real conflict, a real blocked pull, a real
# offline push) rather than POSTing synthetic heartbeats. A test that only posts a heartbeat proves
# the endpoint parses JSON; it proves nothing about whether the agent ever calls it.
#
# This suite starts and STOPS its own server (check 6 requires the server to go away), so it runs on
# its own port with its own state dir and never touches the :5179 dev server. Restarting a server
# someone else configured is not possible anyway: the storage path it was given is not knowable from
# here, and guessing it would silently bring the server back up on an empty database.
#
# Prerequisites: server + agent built in Debug. Nothing else.
# Usage:  .\tests\run-health-tests.ps1   /   pwsh tests/run-health-tests.ps1

$ErrorActionPreference = "Continue"

$onWindows = if ($null -eq $IsWindows) { $true } else { $IsWindows }

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify-health"
$server  = "http://localhost:5181"

if ($onWindows) {
    $inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    $dotnet = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
    $dll    = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
} else {
    $dotnet = "dotnet"
    $dll    = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
}
if (-not (Test-Path $dll)) { Write-Host "Agent not built: $dll"; exit 2 }

# Wipe scratch: a leftover save folder from a previous run makes a pull "blocked" for reasons that
# have nothing to do with the code under test (see Gotchas.md).
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $scratch | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $dll @args 2>&1 }

# ---- This suite owns its server ----
$serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
if (-not (Test-Path $serverDll)) { Write-Host "Server not built: $serverDll"; exit 2 }

$state = Join-Path $scratch "state"
New-Item -ItemType Directory -Force (Join-Path $state "archives") | Out-Null

# The port and the dev DB path come from launchSettings.json, which ONLY `dotnet run` reads —
# launching the DLL directly binds :5000 and loads the production config. So both are passed
# explicitly here (see Gotchas.md).
$env:ASPNETCORE_URLS      = $server
$env:Storage__DbPath      = Join-Path $state "savelocker.db"
$env:Storage__ArchiveRoot = Join-Path $state "archives"
$env:Backup__Enabled      = "false"

function Start-TestServer {
    $p = Start-Process -FilePath $dotnet -ArgumentList $serverDll -PassThru -WindowStyle Hidden
    foreach ($i in 1..40) {
        Start-Sleep -Milliseconds 700
        try { Invoke-RestMethod "$server/api/admin/status" -TimeoutSec 3 | Out-Null; return $p } catch { }
    }
    Write-Host "FAIL: test server did not start on $server"
    exit 1
}

$serverProc = Start-TestServer

# The agent writes its pending-event file next to its other state, NOT next to --config, so both
# "machines" below share one. Each Agent invocation is its own process, so that is fine - but the
# test must reset it between phases or a stale event leaks across checks.
$stateDir = if ($onWindows) { Join-Path $env:ProgramData "SaveLocker" }
            else { Join-Path $env:HOME ".local/share/SaveLocker" }
$eventsFile = Join-Path $stateDir "health-events.json"
function ClearEvents { Remove-Item $eventsFile -Force -ErrorAction SilentlyContinue }

function Health($machineName) {
    (Invoke-RestMethod "$server/api/admin/health") | Where-Object { $_.machineName -eq $machineName }
}

$stamp = Get-Date -Format "HHmmss"
$pcName  = "HealthPC-$stamp"
$lapName = "HealthLap-$stamp"

# ---- Fixtures: two machines, two games, real save folders ----
$pcCfg   = Join-Path $scratch "pc.json"
$lapCfg  = Join-Path $scratch "lap.json"
$pcSave  = Join-Path $scratch "pc_save";  New-Item -ItemType Directory -Force $pcSave  | Out-Null
$lapSave = Join-Path $scratch "lap_save"; New-Item -ItemType Directory -Force $lapSave | Out-Null
$sideSave = Join-Path $scratch "side_save"; New-Item -ItemType Directory -Force $sideSave | Out-Null

foreach ($c in @($pcCfg, $lapCfg)) {
    @{ ServerUrl = $server; Games = @() } | ConvertTo-Json | Set-Content -Path $c -Encoding utf8
}

ClearEvents
Agent register --name $pcName  --config $pcCfg  | Out-Null
Agent register --name $lapName --config $lapCfg | Out-Null

"save data v1" | Set-Content (Join-Path $pcSave "save.dat")  -Encoding utf8
"side data"    | Set-Content (Join-Path $sideSave "s.dat")   -Encoding utf8

$gameName = "HealthGame-$stamp"
$sideName = "SideGame-$stamp"
Agent add-game --name $gameName --dir $pcSave   --config $pcCfg  | Out-Null
Agent add-game --name $sideName --dir $sideSave --config $pcCfg  | Out-Null
Agent add-game --name $gameName --dir $lapSave  --config $lapCfg | Out-Null

# =====================================================================================
# 1. Heartbeat - the agent reports that it exists, and what it is
# =====================================================================================
Agent push $gameName --config $pcCfg | Out-Null
$h = Health $pcName

Check "heartbeat: machine reports online"        ($h.online -eq $true)
Check "heartbeat: reports its agent version"     ($h.agentVersion -match '^\d+\.\d+')
Check "heartbeat: reports its platform"          ($h.platform -in @("Windows", "Linux"))
Check "heartbeat: reports tracked game count"    ($h.trackedGames -ge 2)
Check "heartbeat: reports its last sync time"    ($null -ne $h.lastSyncTime)
Check "a healthy machine has no open problems"   (@($h.openEvents).Length -eq 0)

# =====================================================================================
# 2. A real conflict raises a real event, tied to the machine that is STUCK
# =====================================================================================
# The server already records the ConflictFlag, so the dashboard knows a conflict exists. What it
# cannot know is WHICH machine cannot sync. That is what this event carries.
ClearEvents
Agent pull $gameName --config $lapCfg | Out-Null          # laptop syncs to head
"save data v2 (pc)" | Set-Content (Join-Path $pcSave "save.dat") -Encoding utf8
Agent push $gameName --config $pcCfg | Out-Null           # PC advances head

"laptop's own divergent progress" | Set-Content (Join-Path $lapSave "save.dat") -Encoding utf8
Agent push $gameName --config $lapCfg | Out-Null          # laptop pushes stale parent -> CONFLICT

$lh = Health $lapName
$conflict = $lh.openEvents | Where-Object { $_.code -eq "sync.conflict" }
Check "conflict raises an event on the stuck machine" ($null -ne $conflict)
Check "conflict event is an Error"                    ($conflict.severity -eq "Error")
Check "conflict event names the game"                 ($conflict.gameName -eq $gameName)

# =====================================================================================
# 3. Dedupe - a persistent fault must not manufacture a row per poll
# =====================================================================================
Agent push $gameName --config $lapCfg | Out-Null          # same conflict, reported again
$lh2 = Health $lapName

# @(...) forces a real array. Without it, PowerShell resolves `.Count` on a single result to the
# DTO's OWN `count` field (the dedupe counter) - property lookup is case-insensitive - so this
# assertion would silently measure the wrong number and "pass" while proving nothing.
$conflicts2 = @($lh2.openEvents | Where-Object { $_.code -eq "sync.conflict" })
Check "repeat fault stays ONE open event"             ($conflicts2.Length -eq 1)
Check "repeat fault increments the count"             ($conflicts2[0].count -gt $conflict.count)

# =====================================================================================
# 4. Self-healing - a machine that recovers must not leave a stale alarm
# =====================================================================================
Agent pull $gameName --force --config $lapCfg | Out-Null  # take the server copy: laptop is well again
$lh3 = Health $lapName
Check "a clean sync closes the machine's events"      (@($lh3.openEvents | Where-Object { $_.code -eq "sync.conflict" }).Length -eq 0)

# =====================================================================================
# 5. Save folder missing - silently syncing NOTHING is the failure this catches
# =====================================================================================
ClearEvents
$gone = Join-Path $scratch "vanished"
$cfg = Get-Content $pcCfg -Raw | ConvertFrom-Json
($cfg.Games | Where-Object { $_.Name -eq $sideName }).SaveDirectory = $gone
$cfg | ConvertTo-Json -Depth 8 | Set-Content $pcCfg -Encoding utf8

Agent push $sideName --config $pcCfg | Out-Null
$h2 = Health $pcName
$missing = $h2.openEvents | Where-Object { $_.code -eq "savedir.missing" }
Check "a missing save folder is reported"             ($null -ne $missing)
Check "a missing save folder is a Warning"            ($missing.severity -eq "Warning")

# =====================================================================================
# 6. THE ONE THAT MATTERS: a failure that happens while the server is UNREACHABLE
# =====================================================================================
# "I cannot reach the server" cannot be reported when it happens - that is the whole problem. The
# event must persist to disk and go out after contact returns. If this only worked while online it
# would be reporting nothing at the exact moment it is needed.
ClearEvents
# Stop OUR server only — never every dotnet process on the box, which would take the dev server (and
# on CI, the job) down with it.
Stop-Process -Id $serverProc.Id -Force
Start-Sleep -Seconds 2

"offline edit" | Set-Content (Join-Path $pcSave "save.dat") -Encoding utf8
Agent push $gameName --config $pcCfg | Out-Null   # server is down: this must fail and PERSIST

$persisted = if (Test-Path $eventsFile) { Get-Content $eventsFile -Raw } else { "" }
Check "an offline failure is persisted to disk"       ($persisted -match "server.unreachable")

# Bring the server back on the SAME state, then make the agent talk to it about a DIFFERENT game,
# so the pending event is delivered rather than being dropped by that game's own recovery.
$serverProc = Start-TestServer

Agent pull $sideName --config $pcCfg | Out-Null   # any contact flushes the pending report
$h3 = Health $pcName
$unreachable = $h3.openEvents | Where-Object { $_.code -eq "server.unreachable" }
Check "the offline failure is delivered on reconnect" ($null -ne $unreachable)

# =====================================================================================
# 7. Dismiss
# =====================================================================================
if ($null -ne $unreachable) {
    Invoke-RestMethod "$server/api/admin/health/events/$($unreachable.id)/dismiss" -Method Post | Out-Null
    $h4 = Health $pcName
    Check "a dismissed event leaves the open set"     (@($h4.openEvents | Where-Object { $_.id -eq $unreachable.id }).Length -eq 0)
} else {
    Check "a dismissed event leaves the open set"     $false
}

ClearEvents
if ($serverProc -and -not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force }
Remove-Item Env:ASPNETCORE_URLS, Env:Storage__DbPath, Env:Storage__ArchiveRoot, Env:Backup__Enabled -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Health: $pass passed, $fail failed."
if ($fail -gt 0) { exit 1 }
