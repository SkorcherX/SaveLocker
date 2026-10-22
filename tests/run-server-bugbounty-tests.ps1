# Server / console bug-bounty regressions (tasks/Console-BugBounty.md).
#
# Isolated-state harness: it owns its port, its SQLite DB, and its archive store, and it starts and
# stops every server it uses. It never touches the :5179 dev server.
#
# Sections are added one finding at a time, in the task's execution order.
#
#   CS-01 (P0) - deleting a machine must not delete or hide its save history.
#     The point of this section is the UPGRADE path, not just current behaviour: a pre-fix database
#     has SaveVersions.MachineId as a required column with ON DELETE CASCADE, so proving the fix
#     means seeding a real pre-fix DB, migrating it, and then deleting the machine. A test that only
#     ever sees a freshly created schema would pass without the migration existing at all.
#     The pre-fix server is built from a git worktree, the same pattern verify-password-compat.ps1
#     uses. -BaselineRef pins the last commit BEFORE the CS-01 fix rather than something moving
#     like HEAD~1, so the suite keeps seeding a genuinely pre-fix schema as later findings land.
#     A wrong ref does not pass quietly: the "really is pre-fix" check fails.
#
# Prerequisites: server + agent built in Debug. WSL with python3 (used to read the raw schema).
# Usage:  .\tests\run-server-bugbounty-tests.ps1  [-BaselineRef <ref>] [-ShowPreFixBehavior]
param(
    [string]$BaselineRef = "961ad35",   # last commit before the CS-01 fix
    [int]$Port = 5183,
    [switch]$ShowPreFixBehavior
)

$ErrorActionPreference = "Continue"

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify-server-bugbounty"
$oldWt   = Join-Path $scratch "baseline"
$url     = "http://localhost:$Port"

$inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$dotnet    = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
$agentDll  = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
$serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
if (-not (Test-Path $agentDll))  { Write-Host "Agent not built: $agentDll";   exit 2 }
if (-not (Test-Path $serverDll)) { Write-Host "Server not built: $serverDll"; exit 2 }

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $agentDll @args 2>&1 }

git worktree prune
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $scratch) {
    Write-Host "Could not wipe $scratch - a server from a previous run is probably still holding it."
    Write-Host "Find it with: Get-CimInstance Win32_Process -Filter ""Name='dotnet.exe'"" | ? CommandLine -like '*verify-server-bugbounty*'"
    exit 2
}
New-Item -ItemType Directory -Force (Join-Path $scratch "archives") | Out-Null
$db = Join-Path $scratch "savelocker.db"

# The port and DB path come from launchSettings.json, which only `dotnet run` reads - launching the
# DLL directly would bind :5000 and load the production config (see Gotchas.md).
$env:ASPNETCORE_URLS      = $url
$env:Storage__DbPath      = $db
$env:Storage__ArchiveRoot = Join-Path $scratch "archives"
$env:Backup__Enabled      = "false"
$env:Logging__EventLog__LogLevel__Default = "None"

# Tracked so the finally block can stop a server even when a check throws mid-section. A leaked
# server keeps a lock on the scratch DB and on the baseline worktree's DLLs, which makes the NEXT
# run fail on "worktree already exists" for reasons that have nothing to do with the code.
$script:servers = @()

function Start-TestServer($dll, $tag) {
    $p = Start-Process -FilePath $dotnet -ArgumentList @($dll) -PassThru -NoNewWindow `
        -RedirectStandardOutput "$scratch\$tag.out.log" -RedirectStandardError "$scratch\$tag.err.log"
    $script:servers += $p
    foreach ($i in 1..60) {
        Start-Sleep -Milliseconds 700
        try { Invoke-RestMethod "$url/api/admin/status" -TimeoutSec 3 | Out-Null; return $p } catch { }
    }
    Get-Content "$scratch\$tag.err.log" -ErrorAction SilentlyContinue | Select-Object -Last 20
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Write-Host "FAIL: server ($tag) did not start on $url"
    exit 1
}
function Stop-TestServer($p) {
    if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2   # the DLL keeps a lock on the SQLite file for a moment
}

# Invoke-RestMethod on Windows PowerShell hands a JSON array back as ONE object, so @(...) around it
# yields a single nested element and every .Count assertion silently measures 1. Parsing the body
# ourselves keeps a list a list.
function Get-Json($path) {
    return ((Invoke-WebRequest "$url$path" -UseBasicParsing).Content | ConvertFrom-Json)
}

# There is no sqlite3 CLI on the Windows box, so raw schema reads go through WSL python3 - the same
# approach verify-password-compat.ps1 uses.
function ConvertTo-WslPath($p) {
    $drive = $p.Substring(0, 1).ToLower()
    return "/mnt/$drive" + ($p.Substring(2) -replace '\\', '/')
}
function Invoke-Sqlite($sql) {
    $py = Join-Path $scratch "query.py"
    @'
import sqlite3, sys
con = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
for row in con.execute(sys.argv[2]):
    print("|".join("" if c is None else str(c) for c in row))
'@ | Set-Content -Path $py -Encoding utf8
    return (& wsl -d Ubuntu-24.04 -- python3 (ConvertTo-WslPath $py) (ConvertTo-WslPath $db) $sql)
}

$stamp = Get-Date -Format "HHmmss"
$pcName   = "BBPC-$stamp"
$lapName  = "BBLap-$stamp"
$gameName = "BBGame-$stamp"

$pcCfg   = Join-Path $scratch "pc.json"
$lapCfg  = Join-Path $scratch "lap.json"
$pcSave  = Join-Path $scratch "pc_save";  New-Item -ItemType Directory -Force $pcSave  | Out-Null
$lapSave = Join-Path $scratch "lap_save"; New-Item -ItemType Directory -Force $lapSave | Out-Null
foreach ($c in @($pcCfg, $lapCfg)) {
    @{ ServerUrl = $url; Games = @() } | ConvertTo-Json | Set-Content -Path $c -Encoding utf8
}

Write-Host ""
Write-Host "==== CS-01: machine deletion must preserve save history ===="

# =====================================================================================
# 1. Seed a REAL pre-fix database with the pre-fix server
# =====================================================================================
git worktree prune
git worktree add --quiet --detach $oldWt $BaselineRef
if ($LASTEXITCODE -ne 0) { Write-Host "FAIL: could not create baseline worktree at $BaselineRef"; exit 1 }

try {
    & $dotnet build (Join-Path $oldWt "src\Server\SaveLocker.Server.csproj") --no-incremental -v quiet --nologo | Out-Null
    $oldServerDll = Join-Path $oldWt "src\Server\bin\Debug\net10.0\SaveLocker.Server.dll"
    if (-not (Test-Path $oldServerDll)) { Write-Host "FAIL: baseline server did not build"; exit 1 }

    $srv = Start-TestServer $oldServerDll "prefix"

    # History worth losing: two versions from the PC (one of them protected, one of them the head),
    # plus a divergent push from the laptop that leaves an OPEN conflict referencing a PC version.
    Agent register --name $pcName  --config $pcCfg  | Out-Null
    Agent register --name $lapName --config $lapCfg | Out-Null

    "save v1" | Set-Content (Join-Path $pcSave "save.dat") -Encoding utf8
    Agent add-game --name $gameName --dir $pcSave  --config $pcCfg  | Out-Null
    Agent add-game --name $gameName --dir $lapSave --config $lapCfg | Out-Null
    Agent push $gameName --config $pcCfg | Out-Null

    Agent pull $gameName --config $lapCfg | Out-Null       # laptop syncs to head
    "save v2" | Set-Content (Join-Path $pcSave "save.dat") -Encoding utf8
    Agent push $gameName --config $pcCfg | Out-Null        # PC advances the head

    "laptop's divergent progress" | Set-Content (Join-Path $lapSave "save.dat") -Encoding utf8
    Agent push $gameName --config $lapCfg | Out-Null       # stale parent -> open conflict

    $game = (Get-Json "/api/overview") | Where-Object { $_.game.name -eq $gameName }
    $gameId = $game.game.id
    $before = @(Get-Json "/api/games/$gameId/versions")
    $pcMachine = (Get-Json "/api/machines") | Where-Object { $_.name -eq $pcName }

    # Protect the OLDEST PC version: retention exemption must survive the delete too.
    $oldestPc = ($before | Where-Object { $_.machineId -eq $pcMachine.id } | Sort-Object createdAt)[0]
    Invoke-RestMethod "$url/api/games/$gameId/versions/$($oldestPc.id)/protected?value=true" -Method Post | Out-Null

    $before      = @(Get-Json "/api/games/$gameId/versions")
    $stateBefore = Get-Json "/api/games/$gameId/state"
    $conflictBefore = @(Get-Json "/api/conflicts" | Where-Object { $_.gameId -eq $gameId })[0]

    # Bytes, not just row counts: the promise is that the archives stay downloadable.
    $hashesBefore = @{}
    foreach ($v in $before) {
        $out = Join-Path $scratch "before_$($v.id).zip"
        try {
            Invoke-WebRequest "$url/api/games/$gameId/versions/$($v.id)/download" -OutFile $out | Out-Null
            $hashesBefore[$v.id] = (Get-FileHash $out -Algorithm SHA256).Hash
        } catch {
            Write-Host "     seed download failed for $($v.id) (game $gameId): $($_.Exception.Message)"
        }
    }
    Check "seed: every seeded archive downloads" ($hashesBefore.Count -eq $before.Count)
    Write-Host "     seeded game $gameId with $($before.Count) versions: $(($before | ForEach-Object { $_.id }) -join ', ')"

    Check "seed: PC produced 2 versions, laptop 1"  ($before.Count -eq 3)
    Check "seed: the game has an open conflict"     ($null -ne $conflictBefore)
    Check "seed: a PC version is protected"         (@($before | Where-Object { $_.protected }).Count -eq 1)

    # -ShowPreFixBehavior deletes the machine on the PRE-FIX server instead of migrating, so the
    # failure this section guards against can be seen rather than taken on trust. It reports and
    # exits; it is not part of a normal run.
    if ($ShowPreFixBehavior) {
        Invoke-RestMethod "$url/api/machines/$($pcMachine.id)" -Method Delete | Out-Null
        $preFix      = @(Get-Json "/api/games/$gameId/versions")
        $preFixState = Get-Json "/api/games/$gameId/state"
        Write-Host ""
        Write-Host "PRE-FIX behaviour on $BaselineRef, after deleting one machine:"
        Write-Host "  versions:        $($before.Count) -> $($preFix.Count)"
        Write-Host "  head:            $($stateBefore.head.id) -> $(if ($null -eq $preFixState.head) { '(null)' } else { $preFixState.head.id })"
        Write-Host "  total storage:   $($stateBefore.totalStorageBytes) -> $($preFixState.totalStorageBytes)"
        Write-Host "  archives ON DISK: $(@(Get-ChildItem (Join-Path $scratch 'archives') -Recurse -Filter *.zip).Count) (orphaned)"
        Stop-TestServer $srv
        exit 0
    }

    Stop-TestServer $srv

    # The pre-fix schema is the whole premise of this section. If the baseline already had the
    # column, nothing below would be testing a migration.
    $cols = Invoke-Sqlite "SELECT name FROM pragma_table_info('SaveVersions')"
    Check "baseline ($BaselineRef) really is pre-fix (no MachineName column)" ($cols -notcontains "MachineName")

    # =====================================================================================
    # 2. The NEW server opens that same DB and migrates it
    # =====================================================================================
    $srv = Start-TestServer $serverDll "postfix"

    $afterMigrate = @(Get-Json "/api/games/$gameId/versions")
    Check "migration preserves the version count"      ($afterMigrate.Count -eq $before.Count)

    # =====================================================================================
    # 3. Delete the machine - the action that used to destroy the history
    # =====================================================================================
    Invoke-RestMethod "$url/api/machines/$($pcMachine.id)" -Method Delete | Out-Null

    $machines = @(Get-Json "/api/machines")
    Check "the deleted machine is gone (its key is revoked)" (@($machines | Where-Object { $_.id -eq $pcMachine.id }).Count -eq 0)

    $after      = @(Get-Json "/api/games/$gameId/versions")
    $stateAfter = Get-Json "/api/games/$gameId/state"

    Check "version count is unchanged"          ($after.Count -eq $before.Count)
    Check "the head still resolves"             ($null -ne $stateAfter.head)
    Check "the head is the same version"        ($stateAfter.head.id -eq $stateBefore.head.id)
    Check "the head content hash is unchanged"  ($stateAfter.head.contentHash -eq $stateBefore.head.contentHash)
    Check "total storage is unchanged"          ($stateAfter.totalStorageBytes -eq $stateBefore.totalStorageBytes -and $stateBefore.totalStorageBytes -gt 0)
    Check "the protected flag survives"         (@($after | Where-Object { $_.protected }).Count -eq 1)
    Check "the game still reports its conflict" ($stateAfter.hasOpenConflict -eq $true)
    
    $sizesKept = $true; $hashesKept = $true; $downloadsKept = $true
    foreach ($v in $before) {
        $now = $after | Where-Object { $_.id -eq $v.id }
        if ($null -eq $now) { $sizesKept = $false; continue }
        if ($now.size -ne $v.size -or $now.contentHash -ne $v.contentHash -or
            $now.createdAt -ne $v.createdAt -or $now.parentVersionId -ne $v.parentVersionId) { $sizesKept = $false }
        try {
            $out = Join-Path $scratch "after_$($v.id).zip"
            Invoke-WebRequest "$url/api/games/$gameId/versions/$($v.id)/download" -OutFile $out | Out-Null
            if ((Get-FileHash $out -Algorithm SHA256).Hash -ne $hashesBefore[$v.id]) { $hashesKept = $false }
        } catch { $downloadsKept = $false }
    }
    Check "every version keeps its size/hash/timestamp/lineage" $sizesKept
    Check "every archive is still downloadable"                 $downloadsKept
    Check "every archive returns byte-identical content"         $hashesKept

    # The uploader must still be nameable - an empty machine name in the console is the misreport
    # this finding also called out.
    $orphans = @($after | Where-Object { $null -eq $_.machineId })
    Check "the deleted machine's versions lose their live link" ($orphans.Count -eq 2)
    Check "the deleted uploader is still named honestly"        (@($orphans | Where-Object { $_.machineName -like "$pcName*(deleted)" }).Count -eq 2)
    $lapMachine = (Get-Json "/api/machines") | Where-Object { $_.name -eq $lapName }
    Check "the surviving machine's version keeps its live link" `
        (@($after | Where-Object { $_.machineId -eq $lapMachine.id -and $_.machineName -eq $lapName }).Count -eq 1)

    # =====================================================================================
    # 4. The conflict that referenced the deleted machine must still be resolvable
    # =====================================================================================
    # Resolution queues pulls for the machines behind the two versions; one of them no longer
    # exists, and queueing a command for it would violate the foreign key.
    $conflictAfter = @(Get-Json "/api/conflicts" | Where-Object { $_.gameId -eq $gameId })[0]
    Check "the open conflict survives the delete" ($conflictAfter.id -eq $conflictBefore.id)

    $resolved = $false
    try {
        Invoke-RestMethod "$url/api/conflicts/$($conflictAfter.id)/resolve?version=$($stateBefore.head.id)" -Method Post | Out-Null
        $resolved = $true
    } catch { Write-Host "     resolve failed: $($_.Exception.Message)" }
    Check "a conflict naming a deleted machine still resolves" $resolved
    Check "resolving it left the chosen version as head" `
        ((Get-Json "/api/games/$gameId/state").head.id -eq $stateBefore.head.id)

    # Raw schema reads come last: python3 cannot open the file while the server holds it in WAL mode.
    Stop-TestServer $srv

    $cols = Invoke-Sqlite "SELECT name FROM pragma_table_info('SaveVersions')"
    Check "migration adds the uploader-name snapshot"   ($cols -contains "MachineName")
    Check "migration makes the machine link nullable" `
        (@(Invoke-Sqlite "SELECT [notnull] FROM pragma_table_info('SaveVersions') WHERE name = 'MachineId'")[0] -eq "0")
    Check "the machine link is no longer a cascade" `
        (@(Invoke-Sqlite "SELECT on_delete FROM pragma_foreign_key_list('SaveVersions') WHERE [table] = 'Machines'")[0] -eq "SET NULL")
    $unnamed = @(Invoke-Sqlite "SELECT COUNT(*) FROM SaveVersions WHERE MachineName = ''")[0]
    Check "every version can name its uploader"         ($unnamed -eq "0")

    # =====================================================================================
    # CS-02: command delivery is crash-safe and single-claim
    # =====================================================================================
    # Driven straight through the HTTP API with a machine key rather than through the agent: the
    # failures being reproduced are "the agent never came back" and "two pollers claimed at once",
    # neither of which a cooperating agent process can be made to perform on cue.
    Write-Host ""
    Write-Host "==== CS-02: a claimed command must not be lost or double-claimed ===="

    # Own database, and a lease short enough to wait out. LeaseMinutes is config, so this is the
    # real expiry path, not a test-only shortcut.
    $env:Storage__DbPath        = Join-Path $scratch "commands.db"
    $env:Commands__LeaseMinutes = "0.05"          # 3 seconds
    $leaseWait = 5
    $srv = Start-TestServer $serverDll "commands"

    $reg = Invoke-RestMethod "$url/api/machines/register" -Method Post -ContentType "application/json" `
        -Body (@{ name = "CmdPC-$stamp" } | ConvertTo-Json)
    $agentHdr = @{ "X-Api-Key" = $reg.apiKey }
    $cmdGame  = Invoke-RestMethod "$url/api/games" -Method Post -ContentType "application/json" `
        -Body (@{ name = "CmdGame-$stamp" } | ConvertTo-Json)

    function Queue-Command($type) {
        return Invoke-RestMethod "$url/api/commands" -Method Post -ContentType "application/json" `
            -Body (@{ machineId = $reg.machineId; gameId = $cmdGame.id; type = $type; force = $false } | ConvertTo-Json)
    }
    function Claim-Commands {
        return @(((Invoke-WebRequest "$url/api/agent/commands" -Headers $agentHdr -UseBasicParsing).Content | ConvertFrom-Json))
    }
    function Report-Command($id, $status, $result) {
        Invoke-RestMethod "$url/api/agent/commands/$id/result" -Method Post -Headers $agentHdr `
            -ContentType "application/json" -Body (@{ status = $status; result = $result } | ConvertTo-Json)
    }
    function Admin-Command($id) {
        return @(Get-Json "/api/commands" | Where-Object { $_.id -eq $id })[0]
    }

    # ---- 1. Crash after the GET, before execution ----
    $c1 = Queue-Command "Pull"
    $claim1 = Claim-Commands
    Check "a queued command is delivered"            (@($claim1 | Where-Object { $_.id -eq $c1.id }).Count -eq 1)
    Check "delivery records the first claim"         ((Admin-Command $c1.id).claimCount -eq 1)

    # The agent has it and has not answered: a second poll inside the lease must not run it twice.
    Check "an in-flight command is not re-delivered"  (@(Claim-Commands | Where-Object { $_.id -eq $c1.id }).Count -eq 0)

    Start-Sleep -Seconds $leaseWait
    $reclaim = Claim-Commands
    Check "an unacknowledged command comes back after its lease" (@($reclaim | Where-Object { $_.id -eq $c1.id }).Count -eq 1)
    $c1After = Admin-Command $c1.id
    Check "the reclaim is visible in the command list"           ($c1After.claimCount -eq 2)
    Check "the reclaimed command is still not Done/Failed"       ($c1After.status -eq "Dispatched")
    Check "the reclaim is audited" `
        (@(Get-Json "/api/audit?limit=200" | Where-Object { $_.action -eq "command.reclaim" }).Count -ge 1)

    # ---- 2. Execution succeeded but the result POST was lost ----
    # The retry runs the command again (safe for every type) and reports it. Reporting twice must be
    # a no-op, not a reopened command.
    Report-Command $c1.id "Done" "pulled 1 game" | Out-Null
    $done = Admin-Command $c1.id
    Check "reporting a reclaimed command completes it" ($done.status -eq "Done")
    Check "completion clears the lease"                ($null -eq $done.leaseExpiresAt)

    $lateOk = $false
    try { Report-Command $c1.id "Failed" "late duplicate report" | Out-Null; $lateOk = $true } catch { }
    $stillDone = Admin-Command $c1.id
    Check "a late duplicate report is accepted"         $lateOk
    Check "a late report does not reopen a done command" ($stillDone.status -eq "Done" -and $stillDone.result -eq "pulled 1 game")

    Start-Sleep -Seconds $leaseWait
    Check "a completed command never comes back"        (@(Claim-Commands | Where-Object { $_.id -eq $c1.id }).Count -eq 0)

    # ---- 3. Two simultaneous polls with one machine identity ----
    # Both jobs spin until a shared wall-clock instant so the two GETs really do overlap; a
    # sequential pair would pass even against the old read-then-update.
    $c2 = Queue-Command "Push"
    $startAt = (Get-Date).AddSeconds(3).ToUniversalTime().Ticks
    $racer = {
        param($u, $key, $at)
        while ([DateTime]::UtcNow.Ticks -lt $at) { }
        try {
            ((Invoke-WebRequest "$u/api/agent/commands" -Headers @{ "X-Api-Key" = $key } -UseBasicParsing).Content)
        } catch { "ERROR: $($_.Exception.Message)" }
    }
    $jobs = 1..2 | ForEach-Object { Start-Job -ScriptBlock $racer -ArgumentList $url, $reg.apiKey, $startAt }
    $bodies = $jobs | Wait-Job -Timeout 60 | Receive-Job
    $jobs | Remove-Job -Force
    $winners = @($bodies | Where-Object { $_ -like "*$($c2.id)*" })
    Check "two simultaneous polls: no request errored"  (@($bodies | Where-Object { $_ -like "ERROR:*" }).Count -eq 0)
    Check "two simultaneous polls: exactly one claim"   ($winners.Count -eq 1)
    Check "the raced command was claimed exactly once"  ((Admin-Command $c2.id).claimCount -eq 1)

    # ---- 4. Explicit failure is terminal ----
    Report-Command $c2.id "Failed" "disk full" | Out-Null
    $failed = Admin-Command $c2.id
    Check "an explicit failure is recorded"             ($failed.status -eq "Failed" -and $failed.result -eq "disk full")
    Start-Sleep -Seconds $leaseWait
    Check "a failed command is not retried forever"     (@(Claim-Commands | Where-Object { $_.id -eq $c2.id }).Count -eq 0)

    Stop-TestServer $srv

    # =====================================================================================
    # CS-03: archive ingestion is transactional at the filesystem boundary
    # =====================================================================================
    # Every check here is about what is left BEHIND after something goes wrong: a truncated archive
    # at a real version path, a file no row names, or a row whose file is gone. The last one is the
    # only unrecoverable direction, so it is the one the ordering has to rule out.
    Write-Host ""
    Write-Host "==== CS-03: no half-ingested archives, in either direction ===="

    $env:Storage__DbPath        = Join-Path $scratch "uploads.db"
    $env:Storage__ArchiveRoot   = Join-Path $scratch "upload-archives"
    $env:Storage__MaxUploadMb   = "1"
    Remove-Item Env:Commands__LeaseMinutes -ErrorAction SilentlyContinue
    $archRoot = $env:Storage__ArchiveRoot
    New-Item -ItemType Directory -Force $archRoot | Out-Null

    $srv = Start-TestServer $serverDll "uploads"

    $ureg = Invoke-RestMethod "$url/api/machines/register" -Method Post -ContentType "application/json" `
        -Body (@{ name = "UpPC-$stamp" } | ConvertTo-Json)
    $uhdr = @{ "X-Api-Key" = $ureg.apiKey }
    $ugame = Invoke-RestMethod "$url/api/games" -Method Post -ContentType "application/json" `
        -Body (@{ name = "UpGame-$stamp" } | ConvertTo-Json)

    function Staged { @(Get-ChildItem (Join-Path $archRoot ".incoming") -Filter *.part -ErrorAction SilentlyContinue) }
    function ArchiveFiles { @(Get-ChildItem $archRoot -Recurse -Filter *.zip -ErrorAction SilentlyContinue) }
    function UploadVersions { @(Get-Json "/api/games/$($ugame.id)/versions") }

    # A real (tiny) archive, so the success path is proven with the same helper as the failures.
    $goodZip = Join-Path $scratch "good.zip"
    $zipSrc  = Join-Path $scratch "zipsrc"; New-Item -ItemType Directory -Force $zipSrc | Out-Null
    "hello" | Set-Content (Join-Path $zipSrc "save.dat") -Encoding utf8
    Compress-Archive -Path (Join-Path $zipSrc "*") -DestinationPath $goodZip -Force

    function Post-Upload($path, $hash, $parent) {
        # Returns the HTTP status code, whether it succeeded or not. A parent is passed on the happy
        # path so a second upload fast-forwards instead of opening a conflict, which would make the
        # version undeletable for reasons that have nothing to do with this section.
        $q = "hash=$hash"
        if ($parent) { $q += "&parent=$parent" }
        try {
            $r = Invoke-WebRequest "$url/api/games/$($ugame.id)/upload?$q" -Method Post `
                -Headers $uhdr -InFile $path -ContentType "application/octet-stream" -UseBasicParsing
            return [int]$r.StatusCode
        } catch {
            if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
            return -1
        }
    }

    # ---- 1. The success path still works, and leaves nothing staged ----
    Check "a normal upload is accepted"          ((Post-Upload $goodZip "hash-good-1") -eq 200)
    Check "the upload produced one version"      (@(UploadVersions).Count -eq 1)
    Check "a completed upload stages nothing"    (@(Staged).Count -eq 0)
    Check "the archive is at its final path"     (@(ArchiveFiles).Count -eq 1)

    # ---- 2. Oversized body: refused as 413, and nothing is left of it ----
    $bigFile = Join-Path $scratch "too-big.bin"
    $fs = [System.IO.File]::Create($bigFile)
    $fs.SetLength(3MB); $fs.Close()
    $status = Post-Upload $bigFile "hash-oversized"
    Check "an oversized upload is refused with 413" ($status -eq 413)
    Check "the oversized upload created no version" (@(UploadVersions).Count -eq 1)
    Check "the oversized upload left nothing staged" (@(Staged).Count -eq 0)
    Check "the oversized upload published no archive" (@(ArchiveFiles).Count -eq 1)

    # ---- 3. Client disconnects mid-body ----
    # Raw socket on purpose: it declares a Content-Length it never delivers and then vanishes, which
    # is what a killed agent or a dropped tunnel actually looks like. Invoke-WebRequest cannot.
    # ::new, not New-Object Type(a,b) — that form passes ONE array argument, finds no ctor, and the
    # whole disconnect test then passes vacuously because no request was ever made.
    $client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $Port)
    $stream = $client.GetStream()
    $head = "POST /api/games/$($ugame.id)/upload?hash=hash-truncated HTTP/1.1`r`n" +
            "Host: localhost:$Port`r`nX-Api-Key: $($ureg.apiKey)`r`n" +
            "Content-Type: application/octet-stream`r`nContent-Length: 900000`r`n`r`n"
    $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($headBytes, 0, $headBytes.Length)
    $stream.Write((New-Object byte[] -ArgumentList 4096), 0, 4096)      # a fraction of what was promised
    $stream.Flush()
    Start-Sleep -Milliseconds 500
    $client.Close()                                       # and gone
    Start-Sleep -Seconds 2

    # The pre-fix server leaves a 4096-byte "archive" at a real version path here, with no row.
    Check "a disconnected upload created no version"  (@(UploadVersions).Count -eq 1)
    Check "a disconnected upload left nothing staged" (@(Staged).Count -eq 0)
    Check "a disconnected upload published no archive" (@(ArchiveFiles).Count -eq 1)
    Check "the server survived the disconnect" `
        ((Invoke-WebRequest "$url/api/admin/status" -UseBasicParsing).StatusCode -eq 200)

    # ---- 4. Deleting a version whose archive cannot be removed ----
    # The file is held open with no sharing, so File.Delete fails for real. The row must still go
    # (the admin asked for it) and the undeleted file must be recorded, not silently forgotten.
    $v1 = @(UploadVersions)[0]
    Check "the second upload fast-forwards, not conflicts" `
        ((Post-Upload $goodZip "hash-good-2" $v1.id) -eq 200)
    $victim = @(UploadVersions) | Where-Object { $_.contentHash -eq "hash-good-1" }
    $victimPath = (ArchiveFiles | Where-Object { $_.Name -like "*$($victim.id.Replace('-',''))*" }).FullName
    Check "the version to delete has an archive on disk" ($null -ne $victimPath)

    $held = [System.IO.File]::Open($victimPath, 'Open', 'Read', 'None')
    try {
        $delOk = $false
        try { Invoke-RestMethod "$url/api/games/$($ugame.id)/versions/$($victim.id)" -Method Delete | Out-Null; $delOk = $true } catch { }
        Check "deleting a version succeeds even if its file is locked" $delOk
        Check "the version row is gone"                 (@(UploadVersions | Where-Object { $_.id -eq $victim.id }).Count -eq 0)
        Check "the undeletable archive is audited as orphaned" `
            (@(Get-Json "/api/audit?limit=200" | Where-Object { $_.action -eq "archive.orphaned" }).Count -ge 1)
    } finally { $held.Close() }

    # ---- 5. No live version may point at a missing archive ----
    # The invariant the ordering exists to protect, checked across everything above.
    $dangling = 0
    foreach ($v in UploadVersions) {
        try { Invoke-WebRequest "$url/api/games/$($ugame.id)/versions/$($v.id)/download" -OutFile (Join-Path $scratch "chk.zip") -UseBasicParsing | Out-Null }
        catch { $dangling++ }
    }
    Check "every surviving version still downloads" ($dangling -eq 0)

    # ---- 6. Stale staging files are swept on startup ----
    $incoming = Join-Path $archRoot ".incoming"
    New-Item -ItemType Directory -Force $incoming | Out-Null
    $stale = Join-Path $incoming "00000000000000000000000000000000-dead.part"
    $fresh = Join-Path $incoming "11111111111111111111111111111111-live.part"
    "abandoned" | Set-Content $stale -Encoding utf8
    "in flight" | Set-Content $fresh -Encoding utf8
    (Get-Item $stale).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddHours(-4)

    Stop-TestServer $srv
    $srv = Start-TestServer $serverDll "uploads2"

    Check "startup sweeps an abandoned staging file"     (-not (Test-Path $stale))
    Check "startup leaves a recent staging file alone"   (Test-Path $fresh)

    Stop-TestServer $srv

    # =====================================================================================
    # CS-04: every authoritative head change reaches the fleet
    # =====================================================================================
    # Two real agents, because the failure is not "the pointer is wrong" — the pointer was always
    # right. It is that a machine's PARENT is not repaired, which only shows up as a conflict on that
    # machine's next push. So each case ends by pushing and checking it was accepted.
    Write-Host ""
    Write-Host "==== CS-04: a server-side head change must reach every machine ===="

    $env:Storage__DbPath      = Join-Path $scratch "heads.db"
    $env:Storage__ArchiveRoot = Join-Path $scratch "head-archives"
    Remove-Item Env:Storage__MaxUploadMb -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $env:Storage__ArchiveRoot | Out-Null
    $srv = Start-TestServer $serverDll "heads"

    $hpcName  = "HeadPC-$stamp"
    $hlapName = "HeadLap-$stamp"
    $hdeckName = "HeadDeck-$stamp"
    $hgameName = "HeadGame-$stamp"
    $hpcCfg   = Join-Path $scratch "hpc.json"
    $hlapCfg  = Join-Path $scratch "hlap.json"
    $hpcSave  = Join-Path $scratch "hpc_save";  New-Item -ItemType Directory -Force $hpcSave  | Out-Null
    $hlapSave = Join-Path $scratch "hlap_save"; New-Item -ItemType Directory -Force $hlapSave | Out-Null
    foreach ($c in @($hpcCfg, $hlapCfg)) {
        @{ ServerUrl = $url; Games = @() } | ConvertTo-Json | Set-Content -Path $c -Encoding utf8
    }

    Agent register --name $hpcName  --config $hpcCfg  | Out-Null
    Agent register --name $hlapName --config $hlapCfg | Out-Null
    $deckReg = Invoke-RestMethod "$url/api/machines/register" -Method Post -ContentType "application/json" `
        -Body (@{ name = $hdeckName } | ConvertTo-Json)

    "head v1" | Set-Content (Join-Path $hpcSave "save.dat") -Encoding utf8
    Agent add-game --name $hgameName --dir $hpcSave  --config $hpcCfg  | Out-Null
    Agent add-game --name $hgameName --dir $hlapSave --config $hlapCfg | Out-Null
    Agent push $hgameName --config $hpcCfg | Out-Null          # v1 becomes head
    Agent pull $hgameName --config $hlapCfg | Out-Null         # laptop is current on v1

    $hgame = ((Get-Json "/api/overview") | Where-Object { $_.game.name -eq $hgameName }).game
    $hpc   = (Get-Json "/api/machines") | Where-Object { $_.name -eq $hpcName }
    $hlap  = (Get-Json "/api/machines") | Where-Object { $_.name -eq $hlapName }

    # A third machine that syncs this game but has never uploaded: a Deck an admin has configured
    # and nothing more. It is displaced by a head change exactly like the other two.
    Invoke-RestMethod "$url/api/games/$($hgame.id)/paths/$($deckReg.machineId)?value=$([uri]::EscapeDataString((Join-Path $scratch 'hdeck_save')))" -Method Post | Out-Null

    function PendingPulls($machineId) {
        @(Get-Json "/api/commands" | Where-Object {
            $_.machineId -eq $machineId -and $_.gameId -eq $hgame.id -and
            $_.type -eq "Pull" -and $_.status -eq "Pending" })
    }

    # ---- 1. An automatic conflict policy must tell the machines it displaced ----
    Invoke-RestMethod "$url/api/games/$($hgame.id)/conflict-policy" -Method Post -ContentType "application/json" `
        -Body (@{ policy = "NewestWins" } | ConvertTo-Json) | Out-Null

    "laptop v2"  | Set-Content (Join-Path $hlapSave "save.dat") -Encoding utf8
    Agent push $hgameName --config $hlapCfg | Out-Null          # fast-forward: head = laptop v2

    "pc v3 (stale parent)" | Set-Content (Join-Path $hpcSave "save.dat") -Encoding utf8
    $autoPush = Agent push $hgameName --config $hpcCfg          # diverges; NewestWins hands it to PC
    Check "NewestWins accepted the divergent push"  (-not ("$autoPush" -match "CONFLICT"))
    Check "the policy advanced the head to the winner" `
        ((Get-Json "/api/games/$($hgame.id)/state").head.machineName -eq $hpcName)

    Check "the displaced machine is told to pull"     (@(PendingPulls $hlap.id).Count -eq 1)
    Check "the displaced Deck is told to pull"        (@(PendingPulls $deckReg.machineId).Count -eq 1)
    Check "the winning uploader gets no redundant pull" (@(PendingPulls $hpc.id).Count -eq 0)
    Check "the queued pull is UNFORCED"              (@(PendingPulls $hlap.id)[0].force -eq $false)

    # The point of the whole finding: the displaced machine repairs its parent, so its next save is
    # accepted instead of opening a conflict.
    Agent pull $hgameName --config $hlapCfg | Out-Null
    "laptop v4 after repair" | Set-Content (Join-Path $hlapSave "save.dat") -Encoding utf8
    $repaired = Agent push $hgameName --config $hlapCfg
    Check "the displaced machine's next push does not conflict" (-not ("$repaired" -match "CONFLICT"))

    # ---- 2. Set as Latest must do what the console promises ----
    # Back to Manual so the laptop's stale push opens a real conflict to be superseded.
    Invoke-RestMethod "$url/api/games/$($hgame.id)/conflict-policy" -Method Post -ContentType "application/json" `
        -Body (@{ policy = "Manual" } | ConvertTo-Json) | Out-Null

    Agent pull $hgameName --config $hpcCfg | Out-Null           # PC current again
    "pc v5"  | Set-Content (Join-Path $hpcSave "save.dat") -Encoding utf8
    Agent push $hgameName --config $hpcCfg | Out-Null           # head = pc v5
    "laptop divergent v6" | Set-Content (Join-Path $hlapSave "save.dat") -Encoding utf8
    Agent push $hgameName --config $hlapCfg | Out-Null           # CONFLICT (manual)

    Check "a manual conflict is open before Set as Latest" `
        ((Get-Json "/api/games/$($hgame.id)/state").hasOpenConflict -eq $true)

    # Drain every queued pull first, so the assertions below measure what Set as Latest itself
    # queued rather than leftovers from section 1. Claiming is how an agent drains, so that is how
    # this drains: each machine's own key, straight through the agent endpoint.
    $keys = @{
        $hpc.id             = (Get-Content $hpcCfg  -Raw | ConvertFrom-Json).ApiKey
        $hlap.id            = (Get-Content $hlapCfg -Raw | ConvertFrom-Json).ApiKey
        $deckReg.machineId  = $deckReg.apiKey
    }
    foreach ($m in $keys.Keys) {
        Invoke-WebRequest "$url/api/agent/commands" -Headers @{ "X-Api-Key" = $keys[$m] } -UseBasicParsing | Out-Null
    }
    Check "the queue is drained before measuring Set as Latest" `
        (@(PendingPulls $hlap.id).Count -eq 0 -and @(PendingPulls $hpc.id).Count -eq 0 -and @(PendingPulls $deckReg.machineId).Count -eq 0)

    # First a version the conflict does NOT offer: the disagreement is not the admin's to close, and
    # leaving it open is what keeps the "resolution may not rewind a newer Latest" guard reachable.
    $conf = @(Get-Json "/api/conflicts" | Where-Object { $_.gameId -eq $hgame.id })[0]
    $unrelated = @(Get-Json "/api/games/$($hgame.id)/versions" |
        Where-Object { $_.id -ne $conf.versionAId -and $_.id -ne $conf.versionBId })[0]
    Invoke-RestMethod "$url/api/games/$($hgame.id)/set-latest?version=$($unrelated.id)" -Method Post | Out-Null
    Check "a conflict the chosen version is not part of stays open" `
        ((Get-Json "/api/games/$($hgame.id)/state").hasOpenConflict -eq $true)

    # Now the version the conflict actually offers.
    $target = @(Get-Json "/api/games/$($hgame.id)/versions" | Where-Object { $_.id -eq $conf.versionBId })[0]
    Invoke-RestMethod "$url/api/games/$($hgame.id)/set-latest?version=$($target.id)" -Method Post | Out-Null

    Check "Set as Latest moved the head"              ((Get-Json "/api/games/$($hgame.id)/state").head.id -eq $target.id)
    Check "Set as Latest closed the conflict it decides" ((Get-Json "/api/games/$($hgame.id)/state").hasOpenConflict -eq $false)
    Check "superseding a conflict is audited" `
        (@(Get-Json "/api/audit?limit=200" | Where-Object { $_.action -eq "conflict.resolve_superseded" }).Count -ge 1)
    Check "Set as Latest queues a pull for every machine that syncs the game" `
        (@(PendingPulls $hlap.id).Count -eq 1 -and @(PendingPulls $hpc.id).Count -eq 1 -and @(PendingPulls $deckReg.machineId).Count -eq 1)
    Check "Set as Latest queues UNFORCED pulls" `
        (@(@(PendingPulls $hlap.id) + @(PendingPulls $hpc.id) | Where-Object { $_.force }).Count -eq 0)

    # ---- 3. Deduplication: a second head change must not stack a pull per click ----
    $target2 = @(Get-Json "/api/games/$($hgame.id)/versions")[0]
    Invoke-RestMethod "$url/api/games/$($hgame.id)/set-latest?version=$($target2.id)" -Method Post | Out-Null
    Check "a second head change does not stack pulls" `
        (@(PendingPulls $hlap.id).Count -eq 1 -and @(PendingPulls $hpc.id).Count -eq 1)

    # =====================================================================================
    # CS-05: lease acquisition is atomic
    # =====================================================================================
    # Reuses the CS-04 server: two registered machines and a game are exactly the fixture needed.
    # The failure is a read-then-insert against a unique index, so the test has to make both
    # requests overlap; a sequential pair passes against the broken code.
    Write-Host ""
    Write-Host "==== CS-05: two machines launching at once ===="

    $lkeys = @{
        pc  = (Get-Content $hpcCfg  -Raw | ConvertFrom-Json).ApiKey
        lap = (Get-Content $hlapCfg -Raw | ConvertFrom-Json).ApiKey
    }
    function LeaseState { Get-Json "/api/games/$($hgame.id)/state" }
    function ForceRelease { Invoke-RestMethod "$url/api/games/$($hgame.id)/lease/force" -Method Delete | Out-Null }

    $leaseRacer = {
        param($u, $gameId, $key, $at)
        while ([DateTime]::UtcNow.Ticks -lt $at) { }
        try {
            $r = Invoke-WebRequest "$u/api/games/$gameId/lease" -Method Post -Headers @{ "X-Api-Key" = $key } -UseBasicParsing
            "$([int]$r.StatusCode)|$($r.Content)"
        } catch {
            $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
            "$code|ERROR $($_.Exception.Message)"
        }
    }

    ForceRelease
    $at = (Get-Date).AddSeconds(3).ToUniversalTime().Ticks
    $ljobs = @(
        (Start-Job -ScriptBlock $leaseRacer -ArgumentList $url, $hgame.id, $lkeys.pc,  $at),
        (Start-Job -ScriptBlock $leaseRacer -ArgumentList $url, $hgame.id, $lkeys.lap, $at)
    )
    $results = $ljobs | Wait-Job -Timeout 60 | Receive-Job
    $ljobs | Remove-Job -Force

    $codes   = @($results | ForEach-Object { ($_ -split '\|', 2)[0] })
    $bodies  = @($results | ForEach-Object { ($_ -split '\|', 2)[1] })
    $granted = @($bodies | Where-Object { $_ -notlike "ERROR*" } | ForEach-Object { ($_ | ConvertFrom-Json).granted })

    Check "simultaneous acquire: both requests answered 200"  (@($codes | Where-Object { $_ -ne "200" }).Count -eq 0)
    Check "simultaneous acquire: neither hit a server error"  (@($bodies | Where-Object { $_ -like "ERROR*" }).Count -eq 0)
    Check "simultaneous acquire: exactly one winner"          (@($granted | Where-Object { $_ -eq $true }).Count -eq 1)
    Check "simultaneous acquire: the loser is told no"        (@($granted | Where-Object { $_ -eq $false }).Count -eq 1)

    # The loser must be handed the WINNER's lease, not an empty one: that string is what the launch
    # wrapper shows the user as "checked out on <machine>".
    $loserBody = @($bodies | Where-Object { $_ -notlike "ERROR*" -and ($_ | ConvertFrom-Json).granted -eq $false })[0]
    $loserLease = ($loserBody | ConvertFrom-Json).lease
    # holderMachineName, not machineName: naming it wrong yields $null, which silently makes the
    # holder lookup below pick a machine at random and turns two later checks into coin flips.
    Check "the loser is told who holds it" `
        ($loserLease.holderMachineName -in @($hpcName, $hlapName) -and
         $loserLease.holderMachineName -eq (LeaseState).lease.holderMachineName)

    # ---- Re-acquiring your own live lease is a renewal, not a refusal ----
    $holderName = (LeaseState).lease.holderMachineName
    Check "the game reports a live holder"                   ($holderName -in @($hpcName, $hlapName))
    $holderKey = if ($holderName -eq $hpcName) { $lkeys.pc } else { $lkeys.lap }
    $again = Invoke-RestMethod "$url/api/games/$($hgame.id)/lease" -Method Post -Headers @{ "X-Api-Key" = $holderKey }
    Check "the holder re-acquiring its own lease is granted"  ($again.granted -eq $true)

    # ---- The other machine is still refused while it is live ----
    $otherKey = if ($holderKey -eq $lkeys.pc) { $lkeys.lap } else { $lkeys.pc }
    $refused = Invoke-RestMethod "$url/api/games/$($hgame.id)/lease" -Method Post -Headers @{ "X-Api-Key" = $otherKey }
    Check "a live lease refuses the other machine"           ($refused.granted -eq $false)

    # ---- A released lease is immediately takeable ----
    Invoke-RestMethod "$url/api/games/$($hgame.id)/lease" -Method Delete -Headers @{ "X-Api-Key" = $holderKey } | Out-Null
    $afterRelease = Invoke-RestMethod "$url/api/games/$($hgame.id)/lease" -Method Post -Headers @{ "X-Api-Key" = $otherKey }
    Check "the other machine takes the lease once released"  ($afterRelease.granted -eq $true)

    # ---- Reading the game must not be a write ----
    # GET /state used to delete an expired lease as a side effect, so two parallel reads could
    # collide on the same row. Hammer it and require every read to succeed.
    ForceRelease
    $readJobs = 1..4 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($u, $gameId, $at)
            while ([DateTime]::UtcNow.Ticks -lt $at) { }
            $bad = 0
            foreach ($i in 1..10) {
                try { Invoke-WebRequest "$u/api/games/$gameId/state" -UseBasicParsing | Out-Null } catch { $bad++ }
            }
            $bad
        } -ArgumentList $url, $hgame.id, ((Get-Date).AddSeconds(3).ToUniversalTime().Ticks)
    }
    $readFailures = ($readJobs | Wait-Job -Timeout 90 | Receive-Job | Measure-Object -Sum).Sum
    $readJobs | Remove-Job -Force
    Check "parallel reads of a game never fail"              ($readFailures -eq 0)

    # =====================================================================================
    # CS-06 (trimmed): the URL handed to agents must be one THEY can reach
    # =====================================================================================
    # Scoped deliberately. This deployment is LAN-only over plain HTTP with no reverse proxy and no
    # tunnel, so the forwarded-header and HTTPS-scheme half of the finding has nothing to protect
    # and is not implemented (Decisions.md). What survives is a real LAN failure: the console
    # reached at localhost mints an enrollment file telling the new machine to sync with itself.
    # This harness reaches the server at localhost, so it is already in that state.
    Write-Host ""
    Write-Host "==== CS-06: enrollment and update URLs an agent can actually reach ===="

    function MintEnrollment($body) {
        try {
            $r = Invoke-WebRequest "$url/api/admin/enrollments" -Method Post -ContentType "application/json" `
                -Body ($body | ConvertTo-Json) -UseBasicParsing
            return @{ code = [int]$r.StatusCode; body = ($r.Content | ConvertFrom-Json) }
        } catch {
            return @{ code = [int]$_.Exception.Response.StatusCode; body = $null }
        }
    }
    function EnrollmentCount { @(Get-Json "/api/admin/enrollments").Count }

    $eff = Get-Json "/api/admin/enrollments/effective-url"
    Check "the console reports the URL it would write"  ($eff.url -eq $url)
    Check "a localhost origin is flagged as unusable"   ($eff.isLoopback -eq $true)

    $tokensBefore = EnrollmentCount
    $refused = MintEnrollment @{ machineName = "cs06-loopback"; ttlMinutes = 10 }
    Check "minting is refused when the URL is loopback" ($refused.code -eq 400)
    Check "the refused mint burnt no token"             ((EnrollmentCount) -eq $tokensBefore)

    # A bad override must be caught BEFORE minting: the token is single-use and unrecoverable, so
    # validating afterwards would cost the admin the token as well as the attempt.
    $bad = MintEnrollment @{ machineName = "cs06-bad"; ttlMinutes = 10; serverUrl = "192.168.68.55:5080" }
    Check "an override with no scheme is refused"       ($bad.code -eq 400)
    Check "the refused override burnt no token"         ((EnrollmentCount) -eq $tokensBefore)

    $good = MintEnrollment @{ machineName = "cs06-good"; ttlMinutes = 10; serverUrl = "http://192.168.68.55:5080/" }
    Check "a valid override mints"                      ($good.code -eq 200)
    Check "the policy carries the override, normalised" ($good.body.policy.serverUrl -eq "http://192.168.68.55:5080")

    # Agent and server on one machine is a real setup — it is what run-enrollment-tests does. Only
    # an INFERRED loopback address is refused; one the admin states is a declaration of intent.
    $sameBox = MintEnrollment @{ machineName = "cs06-samebox"; ttlMinutes = 10; serverUrl = $url }
    Check "an explicitly stated loopback URL is allowed" ($sameBox.code -eq 200)
    Check "the same-box policy carries localhost"        ($sameBox.body.policy.serverUrl -eq $url)

    # ---- Server:PublicBaseUrl wins over the request origin ----
    Stop-TestServer $srv
    $env:Server__PublicBaseUrl = "http://192.168.68.55:5080"
    $srv = Start-TestServer $serverDll "publicurl"

    $eff2 = Get-Json "/api/admin/enrollments/effective-url"
    Check "a configured public URL wins over the origin" ($eff2.url -eq "http://192.168.68.55:5080" -and $eff2.fromConfig -eq $true)
    Check "a configured public URL clears the block"     ($eff2.isLoopback -eq $false)

    $configured = MintEnrollment @{ machineName = "cs06-configured"; ttlMinutes = 10 }
    Check "minting over localhost now succeeds"          ($configured.code -eq 200)
    Check "the policy carries the configured URL"        ($configured.body.policy.serverUrl -eq "http://192.168.68.55:5080")

    Remove-Item Env:Server__PublicBaseUrl -ErrorAction SilentlyContinue
    Stop-TestServer $srv

    # =====================================================================================
    # CS-07 + CS-08: the hosted installer is bounded, and replacing it is all-or-nothing
    # =====================================================================================
    # The update channel's failure mode is quiet: an admin uploads, the console says something
    # cheerful, and the fleet stops being offered updates because the metadata names a file that is
    # not there. So every check here ends by asking whether the installer still DOWNLOADS.
    Write-Host ""
    Write-Host "==== CS-07/08: bounded uploads, atomic installer replacement ===="

    $env:Storage__DbPath              = Join-Path $scratch "installer.db"
    $env:Storage__AgentInstallerRoot  = Join-Path $scratch "installer-store"
    $env:AgentUpdate__MaxInstallerMb  = "2"
    New-Item -ItemType Directory -Force $env:Storage__AgentInstallerRoot | Out-Null
    $instRoot = $env:Storage__AgentInstallerRoot
    $srv = Start-TestServer $serverDll "installer"

    # /api/agent/latest is an agent route, so the platform checks below need a real machine key.
    # Registered here rather than in the checks so a restart mid-block cannot invalidate it.
    $instReg = Invoke-RestMethod "$url/api/machines/register" -Method Post -ContentType "application/json" `
        -Body (@{ name = "InstallerProbe" } | ConvertTo-Json)
    $instHdr = @{ "X-Api-Key" = $instReg.apiKey }

    function MakeExe($path, $bytes, $marker) {
        $fs = [System.IO.File]::Create($path)
        $fs.SetLength($bytes)
        $fs.Position = 0
        $m = [System.Text.Encoding]::ASCII.GetBytes($marker)
        $fs.Write($m, 0, $m.Length)
        $fs.Close()
    }
    # A real multipart body, built by hand: Invoke-WebRequest -Form is PowerShell 7 only.
    # $platform is deliberately OPTIONAL and appended only when given: half the point of these
    # checks is that a caller which names no platform still gets the Windows slot it always got.
    function PlatformQuery($platform) { if ($platform) { "&platform=$platform" } else { "" } }
    # Leading "?" form, for the routes that have no other query parameter.
    function PlatformOnly($platform) { if ($platform) { "?platform=$platform" } else { "" } }
    function PostInstaller($path, $version, $fileName, $platform) {
        $boundary = [Guid]::NewGuid().ToString()
        $LF = "`r`n"
        $head = "--$boundary$LF" +
                "Content-Disposition: form-data; name=`"file`"; filename=`"$fileName`"$LF" +
                "Content-Type: application/octet-stream$LF$LF"
        $tail = "$LF--$boundary--$LF"
        $body = New-Object System.IO.MemoryStream
        $hb = [System.Text.Encoding]::ASCII.GetBytes($head);  $body.Write($hb, 0, $hb.Length)
        $fb = [System.IO.File]::ReadAllBytes($path);          $body.Write($fb, 0, $fb.Length)
        $tb = [System.Text.Encoding]::ASCII.GetBytes($tail);  $body.Write($tb, 0, $tb.Length)
        try {
            $r = Invoke-WebRequest "$url/api/admin/agent-installer?version=$version$(PlatformQuery $platform)" -Method Post `
                -ContentType "multipart/form-data; boundary=$boundary" -Body $body.ToArray() -UseBasicParsing
            return @{ code = [int]$r.StatusCode; body = ($r.Content | ConvertFrom-Json) }
        } catch {
            return @{ code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }; body = $null }
        } finally { $body.Dispose() }
    }
    function InstallerStatus($platform) {
        try { return ((Invoke-WebRequest "$url/api/admin/agent-installer$(PlatformOnly $platform)" -UseBasicParsing).Content | ConvertFrom-Json) }
        catch { return $null }
    }
    function InstallerStatusCode($platform) {
        try { return [int](Invoke-WebRequest "$url/api/admin/agent-installer$(PlatformOnly $platform)" -UseBasicParsing).StatusCode }
        catch {
            if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
            return -1
        }
    }
    function InstallerDownloads($platform) {
        try {
            $out = Join-Path $scratch "dl-installer.bin"
            Invoke-WebRequest "$url/api/agent/installer/download$(PlatformOnly $platform)" -OutFile $out -UseBasicParsing | Out-Null
            return (Get-Item $out).Length
        } catch { return -1 }
    }
    function StagedInstallerFiles {
        @(Get-ChildItem $instRoot -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like ".incoming-*" -or $_.Name -like ".info-*" })
    }

    # ---- 1. A good installer publishes ----
    $goodExe = Join-Path $scratch "SaveLocker-Agent-Setup-1.0.0.exe"
    MakeExe $goodExe 65536 "GOOD-1.0.0"
    $up1 = PostInstaller $goodExe "1.0.0" "SaveLocker-Agent-Setup-1.0.0.exe"
    Check "a valid installer uploads"                 ($up1.code -eq 200)
    Check "the console reports it"                    ((InstallerStatus).version -eq "1.0.0")
    Check "it downloads at the right size"            ((InstallerDownloads) -eq 65536)
    Check "the upload left nothing staged"            (@(StagedInstallerFiles).Count -eq 0)

    # ---- 2. CS-07: the body limit is enforced, not removed ----
    $hugeExe = Join-Path $scratch "SaveLocker-Agent-Setup-9.9.9.exe"
    MakeExe $hugeExe (5MB) "TOO-BIG"
    $tooBig = PostInstaller $hugeExe "9.9.9" "SaveLocker-Agent-Setup-9.9.9.exe"
    Check "an oversized installer is refused with 413" ($tooBig.code -eq 413)
    Check "the oversized upload left nothing staged"   (@(StagedInstallerFiles).Count -eq 0)

    # ---- 3. CS-08: a refused replacement must not cost the working installer ----
    Check "the previous installer is still described"  ((InstallerStatus).version -eq "1.0.0")
    Check "the previous installer still downloads"     ((InstallerDownloads) -eq 65536)

    # ---- 4. Validation happens before anything on disk is touched ----
    $notExe = Join-Path $scratch "notes.txt"
    MakeExe $notExe 1024 "NOT-AN-EXE"
    $badExt = PostInstaller $notExe "2.0.0" "notes.txt"
    Check "a non-.exe upload is refused"               ($badExt.code -eq 400)

    $badVer = PostInstaller $goodExe "not-a-version" "SaveLocker-Agent-Setup-x.exe"
    Check "an unparseable version is refused"          ($badVer.code -eq 400)

    $noName = PostInstaller $goodExe "2.0.0" ""
    Check "an empty filename is refused"               ($noName.code -eq 400)

    Check "after every refusal the old installer stands" `
        ((InstallerStatus).version -eq "1.0.0" -and (InstallerDownloads) -eq 65536)
    Check "the refusals left nothing staged"           (@(StagedInstallerFiles).Count -eq 0)

    # ---- 5. A client that vanishes mid-upload ----
    # Honest note: this one does NOT fail against the pre-fix build. There the body is buffered by
    # ReadFormAsync before SaveAsync runs at all, so an abandoned upload never reached the
    # delete-everything-first step. It guards the new staged path against regressing, rather than
    # reproducing the old bug — checks 2 and 4 are the ones that fail pre-fix.
    $client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $Port)
    $stream = $client.GetStream()
    $bnd = [Guid]::NewGuid().ToString()
    $head = "POST /api/admin/agent-installer?version=3.0.0 HTTP/1.1`r`nHost: localhost:$Port`r`n" +
            "Content-Type: multipart/form-data; boundary=$bnd`r`nContent-Length: 900000`r`n`r`n" +
            "--$bnd`r`nContent-Disposition: form-data; name=`"file`"; filename=`"SaveLocker-Agent-Setup-3.0.0.exe`"`r`n`r`n"
    $hb = [System.Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($hb, 0, $hb.Length)
    $stream.Write((New-Object byte[] -ArgumentList 4096), 0, 4096)
    $stream.Flush()
    Start-Sleep -Milliseconds 500
    $client.Close()
    Start-Sleep -Seconds 2

    Check "an abandoned upload leaves the old installer" ((InstallerStatus).version -eq "1.0.0")
    Check "the abandoned upload still downloads the old" ((InstallerDownloads) -eq 65536)

    # ---- 6. Two writers at once produce one coherent installer ----
    # Not "whoever wins": the point is that the metadata and the file on disk always agree, which
    # is what the old delete-then-write-then-describe sequence could not promise.
    $exeA = Join-Path $scratch "SaveLocker-Agent-Setup-4.0.0.exe"; MakeExe $exeA 40000 "AAA-4.0.0"
    $exeB = Join-Path $scratch "SaveLocker-Agent-Setup-5.0.0.exe"; MakeExe $exeB 50000 "BBB-5.0.0"
    $at = (Get-Date).AddSeconds(3).ToUniversalTime().Ticks
    $uploader = {
        param($u, $path, $version, $fileName, $at)
        $boundary = [Guid]::NewGuid().ToString()
        $LF = "`r`n"
        $head = "--$boundary$LF" +
                "Content-Disposition: form-data; name=`"file`"; filename=`"$fileName`"$LF" +
                "Content-Type: application/octet-stream$LF$LF"
        $tail = "$LF--$boundary--$LF"
        $ms = New-Object System.IO.MemoryStream
        $hb = [System.Text.Encoding]::ASCII.GetBytes($head); $ms.Write($hb, 0, $hb.Length)
        $fb = [System.IO.File]::ReadAllBytes($path);         $ms.Write($fb, 0, $fb.Length)
        $tb = [System.Text.Encoding]::ASCII.GetBytes($tail); $ms.Write($tb, 0, $tb.Length)
        while ([DateTime]::UtcNow.Ticks -lt $at) { }
        try {
            $r = Invoke-WebRequest "$u/api/admin/agent-installer?version=$version" -Method Post `
                -ContentType "multipart/form-data; boundary=$boundary" -Body $ms.ToArray() -UseBasicParsing
            "$([int]$r.StatusCode)"
        } catch { "ERR" } finally { $ms.Dispose() }
    }
    $ijobs = @(
        (Start-Job -ScriptBlock $uploader -ArgumentList $url, $exeA, "4.0.0", "SaveLocker-Agent-Setup-4.0.0.exe", $at),
        (Start-Job -ScriptBlock $uploader -ArgumentList $url, $exeB, "5.0.0", "SaveLocker-Agent-Setup-5.0.0.exe", $at)
    )
    $icodes = $ijobs | Wait-Job -Timeout 90 | Receive-Job
    $ijobs | Remove-Job -Force

    Check "two simultaneous uploads both answered 200"  (@($icodes | Where-Object { $_ -ne "200" }).Count -eq 0)
    $final = InstallerStatus
    $expectedSize = if ($final.version -eq "4.0.0") { 40000 } else { 50000 }
    Check "one of the two won cleanly"                  ($final.version -in @("4.0.0", "5.0.0"))
    Check "metadata and file agree after the race"      ((InstallerDownloads) -eq $expectedSize -and $final.sizeBytes -eq $expectedSize)
    Check "the loser's binary was cleaned up"           (@(Get-ChildItem $instRoot -Filter *.exe).Count -eq 1)
    Check "the race left nothing staged"                (@(StagedInstallerFiles).Count -eq 0)

    # ---- 7. Startup sweeps abandoned installer staging ----
    $staleInst = Join-Path $instRoot ".incoming-deadbeef.part"
    "abandoned" | Set-Content $staleInst -Encoding utf8
    (Get-Item $staleInst).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddHours(-4)
    Stop-TestServer $srv
    $srv = Start-TestServer $serverDll "installer2"
    Check "startup sweeps abandoned installer staging"  (-not (Test-Path $staleInst))
    Check "the installer survived the restart"          ((InstallerDownloads) -eq $expectedSize)

    # ---- 8. Delete clears both the binary and the metadata ----
    Invoke-RestMethod "$url/api/admin/agent-installer" -Method Delete | Out-Null
    Check "delete removes the installer"                ($null -eq (InstallerStatus))
    Check "delete removes the binary too"               (@(Get-ChildItem $instRoot -Filter *.exe).Count -eq 0)

    # ---- 9. Platform slots are independent, and an absent platform still means Windows ----
    # The failure this guards is not a crash. It is a Deck being offered a Windows .exe, or a
    # Windows box being offered a tarball, because one slot leaked into the other — and the agent
    # would then download something it cannot run and report a version nobody is on. The last
    # check is the compatibility one: an agent built before slots existed sends no platform at all.
    $linuxRoot = Join-Path $instRoot "linux-x64"
    $tarball   = Join-Path $scratch "savelocker-2.0.0-linux-x64.tar.gz"
    MakeExe $tarball 24000 "TARBALL-2.0.0"

    $wrongSlotA = PostInstaller $tarball "2.0.0" "savelocker-2.0.0-linux-x64.tar.gz" $null
    Check "a tarball into the Windows slot is refused"   ($wrongSlotA.code -eq 400)
    $wrongSlotB = PostInstaller $goodExe "1.0.0" "SaveLocker-Agent-Setup-1.0.0.exe" "linux-x64"
    Check "an .exe into the Linux slot is refused"       ($wrongSlotB.code -eq 400)

    # A platform names a directory, so an unrecognised one must be refused rather than defaulted —
    # being quietly handed the Windows installer is the answer that gets a Deck into trouble.
    Check "an unknown platform is refused on status"     ((InstallerStatusCode "solaris") -eq 400)
    $bogusUpload = PostInstaller $goodExe "1.0.0" "SaveLocker-Agent-Setup-1.0.0.exe" "solaris"
    Check "an unknown platform is refused on upload"     ($bogusUpload.code -eq 400)
    Check "an unknown platform is refused on download"   ((InstallerDownloads "solaris") -eq -1)

    # Both slots filled at once, with DIFFERENT versions — the normal state of a fleet whose
    # Windows installer is ahead of its Deck tarball, and the one a single-slot store cannot hold.
    $win2 = PostInstaller $goodExe "1.0.0" "SaveLocker-Agent-Setup-1.0.0.exe" "win-x64"
    $lin2 = PostInstaller $tarball "2.0.0" "savelocker-2.0.0-linux-x64.tar.gz" "linux-x64"
    Check "both slots accept their own package"          ($win2.code -eq 200 -and $lin2.code -eq 200)
    Check "each slot reports its own version"            ((InstallerStatus "win-x64").version -eq "1.0.0" -and
                                                          (InstallerStatus "linux-x64").version -eq "2.0.0")
    Check "each slot downloads its own bytes"            ((InstallerDownloads "win-x64") -eq 65536 -and
                                                          (InstallerDownloads "linux-x64") -eq 24000)
    Check "the Linux package is stored under its own root" `
        (@(Get-ChildItem $linuxRoot -Filter *.tar.gz).Count -eq 1 -and
         @(Get-ChildItem $instRoot -Filter *.tar.gz).Count -eq 0)
    Check "the Windows slot kept its own .exe"           (@(Get-ChildItem $instRoot -Filter *.exe).Count -eq 1)

    # Compatibility: no ?platform= at all is exactly what every already-deployed agent sends.
    Check "an absent platform still means Windows"       ((InstallerStatus).version -eq "1.0.0" -and
                                                          (InstallerDownloads) -eq 65536)
    $latestDefault = Invoke-RestMethod "$url/api/agent/latest" -Headers $instHdr
    Check "/api/agent/latest defaults to the Windows slot" ($latestDefault.latestVersion -eq "1.0.0")
    $latestLinux = Invoke-RestMethod "$url/api/agent/latest?platform=linux-x64" -Headers $instHdr
    Check "/api/agent/latest serves the Linux slot"      ($latestLinux.latestVersion -eq "2.0.0")
    # The URL it hands back has to name the same platform, or the agent follows it and downloads
    # the other OS's package with a digest that will never match.
    Check "the offered URL carries the platform"         ($latestLinux.downloadUrl -like "*platform=linux-x64*")

    # Deleting one slot must not touch the other.
    Invoke-RestMethod "$url/api/admin/agent-installer?platform=linux-x64" -Method Delete | Out-Null
    Check "deleting Linux leaves Windows serving"        ((InstallerStatus "win-x64").version -eq "1.0.0" -and
                                                          (InstallerDownloads "win-x64") -eq 65536)
    Check "deleting Linux emptied only its own root"     ($null -eq (InstallerStatus "linux-x64") -and
                                                          @(Get-ChildItem $linuxRoot -Filter *.tar.gz).Count -eq 0)

    # ---- 10. The AgentUpdate:* config fallback also defaults to Windows ----
    # Its own check because the hosted path and the fallback path choose a platform SEPARATELY, and
    # a defect in the second one is invisible from the first: with a hosted installer present the
    # fallback never runs, so every check above passes while this one is broken. Reading the raw
    # (null) parameter instead of the normalised one sent every pre-platform agent to the Linux
    # section, which is empty, so the server answered 204 and the whole fleet quietly went "up to
    # date" forever. WA-05's off-origin block caught that; this is the cheap local guard.
    #
    # The Windows slot is emptied while the server is still UP — a web cmdlet aimed at a stopped
    # server throws a TERMINATING error that -ErrorAction cannot suppress, which takes the rest of
    # the suite with it.
    Invoke-RestMethod "$url/api/admin/agent-installer" -Method Delete | Out-Null
    Stop-TestServer $srv
    $env:AgentUpdate__LatestVersion = "7.7.7"
    $env:AgentUpdate__DownloadUrl   = "http://example.invalid/SaveLocker-Agent-Setup-7.7.7.exe"
    $srv = Start-TestServer $serverDll "installer-fallback"
    Remove-Item Env:AgentUpdate__LatestVersion, Env:AgentUpdate__DownloadUrl -ErrorAction SilentlyContinue

    Check "the hosted slot really is empty first"        ($null -eq (InstallerStatus))
    $fbDefault = Invoke-RestMethod "$url/api/agent/latest" -Headers $instHdr
    Check "the config fallback answers an agent that names no platform" ($fbDefault.latestVersion -eq "7.7.7")
    # Linux has its own section and nothing in it, so it must answer 204 rather than borrow the
    # Windows URL — a Deck handed a .exe would download it and never be able to run it.
    $fbLinuxCode = try {
        [int](Invoke-WebRequest "$url/api/agent/latest?platform=linux-x64" -Headers $instHdr -UseBasicParsing).StatusCode
    } catch { -1 }
    Check "the Windows fallback is not offered to Linux"  ($fbLinuxCode -eq 204)

    Remove-Item Env:Storage__AgentInstallerRoot, Env:AgentUpdate__MaxInstallerMb -ErrorAction SilentlyContinue
    Stop-TestServer $srv

    # =====================================================================================
    # CS-09 + CS-10: don't consume a credential you did not end up using
    # =====================================================================================
    # Two shapes of the same mistake: write first, find out afterwards. A SteamGridDB key was stored
    # before it was checked, and an enrollment token was burnt before the machine key it buys was
    # issued.
    Write-Host ""
    Write-Host "==== CS-09/10: settings and enrollment commit only on success ===="

    $env:Storage__DbPath = Join-Path $scratch "settings.db"
    # Seeded through configuration rather than the API, because the API now verifies before storing
    # and this box has no valid SteamGridDB key. GetEffectiveAsync falls back to config, so this is
    # a genuine "working key already in place" for the purposes of the check below.
    $env:SteamGridDb__ApiKey = "config-key-that-already-works"
    $srv = Start-TestServer $serverDll "settings"

    function Settings { Get-Json "/api/settings" }
    function SaveSgdbKey($key) {
        try {
            $r = Invoke-WebRequest "$url/api/settings/steamgriddb-key" -Method Post -ContentType "application/json" `
                -Body (@{ apiKey = $key } | ConvertTo-Json) -UseBasicParsing
            return @{ code = [int]$r.StatusCode; body = ($r.Content | ConvertFrom-Json) }
        } catch {
            $resp = $_.Exception.Response
            $text = if ($resp) { (New-Object System.IO.StreamReader($resp.GetResponseStream())).ReadToEnd() } else { "" }
            return @{ code = if ($resp) { [int]$resp.StatusCode } else { -1 }; body = ($text | ConvertFrom-Json -ErrorAction SilentlyContinue) }
        }
    }

    Check "the seeded key is reported as configured"  ((Settings).steamGridDbConfigured -eq $true)

    # A bogus key. Whether SteamGridDB rejects it or this box cannot reach SteamGridDB at all, the
    # observable requirement is identical and is the point of the finding: do not store it, and do
    # not report success.
    $rejected = SaveSgdbKey "definitely-not-a-real-steamgriddb-key"
    Check "a key that fails verification is not a 200"  ($rejected.code -ge 400)
    Check "the failure says why"                        (-not [string]::IsNullOrWhiteSpace($rejected.body.message))
    Check "the working key is still configured"         ((Settings).steamGridDbConfigured -eq $true)
    Check "the working key still comes from config"     ((Settings).steamGridDbFromConfig -eq $true)

    # Clearing is not a verification path and must still work.
    $cleared = SaveSgdbKey $null
    Check "clearing the key succeeds"                   ($cleared.code -eq 200 -and $cleared.body.ok -eq $true)

    # ---- CS-10: one policy, two agents, at the same instant ----
    $mint = Invoke-RestMethod "$url/api/admin/enrollments" -Method Post -ContentType "application/json" `
        -Body (@{ machineName = "RaceDeck-$stamp"; ttlMinutes = 15; serverUrl = $url } | ConvertTo-Json)

    $redeemer = {
        param($u, $token, $at)
        while ([DateTime]::UtcNow.Ticks -lt $at) { }
        try {
            $r = Invoke-WebRequest "$u/api/enroll" -Method Post -ContentType "application/json" `
                -Body (@{ token = $token } | ConvertTo-Json) -UseBasicParsing
            "$([int]$r.StatusCode)|$($r.Content)"
        } catch {
            $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
            "$code|"
        }
    }
    $at = (Get-Date).AddSeconds(3).ToUniversalTime().Ticks
    $rjobs = @(
        (Start-Job -ScriptBlock $redeemer -ArgumentList $url, $mint.policy.token, $at),
        (Start-Job -ScriptBlock $redeemer -ArgumentList $url, $mint.policy.token, $at)
    )
    $rres = $rjobs | Wait-Job -Timeout 90 | Receive-Job
    $rjobs | Remove-Job -Force

    $rcodes = @($rres | ForEach-Object { ($_ -split '\|', 2)[0] })
    $keys = @($rres | ForEach-Object { ($_ -split '\|', 2)[1] } |
        Where-Object { $_ } | ForEach-Object { ($_ | ConvertFrom-Json).apiKey } | Where-Object { $_ })

    Check "simultaneous redeem: exactly one succeeds"   (@($rcodes | Where-Object { $_ -eq "200" }).Count -eq 1)
    Check "simultaneous redeem: exactly one machine key" ($keys.Count -eq 1)
    Check "simultaneous redeem: the loser is refused"   (@($rcodes | Where-Object { $_ -ne "200" }).Count -eq 1)
    Check "the race created ONE machine"                (@(Get-Json "/api/machines" | Where-Object { $_.name -eq "RaceDeck-$stamp" }).Count -eq 1)

    # The winner's key must actually work — a token spent for a key that does not authenticate is
    # the failure this section exists to rule out.
    $worked = $false
    try {
        Invoke-WebRequest "$url/api/games" -Headers @{ "X-Api-Key" = $keys[0] } -UseBasicParsing | Out-Null
        $worked = $true
    } catch { }
    Check "the issued key authenticates"                $worked

    $spent = @(Get-Json "/api/admin/enrollments" | Where-Object { $_.id -eq $mint.id })[0]
    Check "the spent token records who spent it"        ($spent.redeemedByMachineName -eq "RaceDeck-$stamp")

    # =====================================================================================
    # CS-11: a machine that has never checked in is its own state
    # =====================================================================================
    # The fix is in the console, which cannot be reached from here. What IS checkable — and what the
    # console's rendering now depends on — is the server contract underneath it: health returns a row
    # for every machine, and lastHeartbeat is null for one that has never reported. The console used
    # to test for a missing ROW, which the server never produces, so "never reported" was dead code
    # and a freshly enrolled agent read as "offline since —".
    Write-Host ""
    Write-Host "==== CS-11: the never-reported contract ===="

    $silent = Invoke-RestMethod "$url/api/machines/register" -Method Post -ContentType "application/json" `
        -Body (@{ name = "SilentDeck-$stamp" } | ConvertTo-Json)
    $health = @(Get-Json "/api/admin/health" | Where-Object { $_.machineName -eq "SilentDeck-$stamp" })

    Check "health returns a row for a machine that never reported" ($health.Count -eq 1)
    Check "its lastHeartbeat is null, not a date"                  ($null -eq $health[0].lastHeartbeat)
    Check "it is not reported as online"                           ($health[0].online -eq $false)

    # And a machine that HAS reported must still be distinguishable, or the console would call
    # everything "never reported" instead.
    Invoke-RestMethod "$url/api/agent/health" -Method Post -Headers @{ "X-Api-Key" = $silent.apiKey } `
        -ContentType "application/json" `
        -Body (@{ agentVersion = "9.9.9"; platform = "Linux"; trackedGames = 0; unmappedGames = 0; offlineQueueDepth = 0 } | ConvertTo-Json) | Out-Null
    $afterBeat = @(Get-Json "/api/admin/health" | Where-Object { $_.machineName -eq "SilentDeck-$stamp" })[0]
    Check "once it reports, lastHeartbeat is set"                  ($null -ne $afterBeat.lastHeartbeat)
    Check "and it is online"                                       ($afterBeat.online -eq $true)

    Remove-Item Env:SteamGridDb__ApiKey -ErrorAction SilentlyContinue
    Stop-TestServer $srv

    # =====================================================================================
    # CS-12: the chunked upload protocol
    # =====================================================================================
    # The single-shot route above is all-or-nothing: ArchiveStore.SaveAsync deletes its staging file
    # on any failure, so a body that stopped early was simply never published. The chunked route
    # gives that up by construction - the archive is assembled across many requests and only the
    # last one says it is finished - so every guarantee it still has to offer needs asserting here
    # rather than inferred from the single-shot section.
    #
    # The sharp one is a chunk that faults PART-WAY through. Its bytes used to stay on disk and stay
    # counted, so the client's retry of that same chunk arrived BEHIND the session, was read as a
    # replay of one that had already succeeded, and was answered 200 without writing anything - the
    # rest of that chunk silently gone. On the last chunk that published a truncated zip under the
    # content hash of a complete one, which every other machine then pulled believing it was intact.
    Write-Host ""
    Write-Host "==== CS-12: chunked uploads assemble exactly, or refuse ===="

    $env:Storage__DbPath      = Join-Path $scratch "chunked.db"
    $env:Storage__ArchiveRoot = Join-Path $scratch "chunk-archives"
    $env:Storage__MaxUploadMb = "2"
    $chunkRoot = $env:Storage__ArchiveRoot
    New-Item -ItemType Directory -Force $chunkRoot | Out-Null
    $srv = Start-TestServer $serverDll "chunked"

    $creg = Invoke-RestMethod "$url/api/machines/register" -Method Post -ContentType "application/json" `
        -Body (@{ name = "ChunkPC-$stamp" } | ConvertTo-Json)
    $chdr = @{ "X-Api-Key" = $creg.apiKey }
    $othr = Invoke-RestMethod "$url/api/machines/register" -Method Post -ContentType "application/json" `
        -Body (@{ name = "ChunkOther-$stamp" } | ConvertTo-Json)
    $cgame = Invoke-RestMethod "$url/api/games" -Method Post -ContentType "application/json" `
        -Body (@{ name = "ChunkGame-$stamp" } | ConvertTo-Json)

    function CStaged { @(Get-ChildItem (Join-Path $chunkRoot ".incoming") -Filter *.part -ErrorAction SilentlyContinue) }
    function CVersions { @(Get-Json "/api/games/$($cgame.id)/versions") }

    # A real zip, because CompleteSession now reads the archive's central directory before it
    # publishes anything - which is the only integrity check available here. The session's
    # ContentHash is SaveArchive.HashDirectory's hash of the save FOLDER, not of the zip carrying
    # it, so the server cannot recompute it without extracting; a zip's index living at its END is
    # what makes "did every byte arrive" answerable at all.
    $cZipSrc = Join-Path $scratch "chunk-src"; New-Item -ItemType Directory -Force $cZipSrc | Out-Null
    # Incompressible on purpose: a zip of repetitive text collapses to a few hundred bytes, which
    # splits into "chunks" too small for an offset bug to have anywhere to hide. Fixed seed so a
    # failure is reproducible.
    $cPayload = New-Object byte[] (300KB)
    (New-Object Random -ArgumentList 20260824).NextBytes($cPayload)
    [System.IO.File]::WriteAllBytes((Join-Path $cZipSrc "save.dat"), $cPayload)
    "meta" | Set-Content (Join-Path $cZipSrc "meta.txt") -Encoding utf8
    $cZip = Join-Path $scratch "chunk.zip"
    Compress-Archive -Path (Join-Path $cZipSrc "*") -DestinationPath $cZip -Force
    $cBytes = [System.IO.File]::ReadAllBytes($cZip)

    # The leading comma is load-bearing: PowerShell unrolls an array returned from a function into
    # the pipeline, so a bare `return $s` hands back Object[], which Invoke-WebRequest then sends as
    # a STRING. Every chunk went up mangled and every byte-count assertion measured the wrong thing.
    function Slice($from, $count) {
        $s = New-Object byte[] $count
        [Array]::Copy($cBytes, $from, $s, 0, $count)
        return ,$s
    }
    function BeginUpload($hash, $parent, $hdr) {
        $body = @{ contentHash = $hash; parentVersionId = $parent; force = $false } | ConvertTo-Json
        return Invoke-RestMethod "$url/api/games/$($cgame.id)/upload/begin" -Method Post `
            -Headers $hdr -ContentType "application/json" -Body $body
    }
    function PutChunk($session, $offset, $bytes, $hdr) {
        try {
            $r = Invoke-WebRequest "$url/api/games/$($cgame.id)/upload/$session/chunk?offset=$offset" `
                -Method Put -Headers $hdr -Body $bytes -ContentType "application/octet-stream" -UseBasicParsing
            return @{ Code = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json) }
        } catch {
            if ($_.Exception.Response) { return @{ Code = [int]$_.Exception.Response.StatusCode; Body = $null } }
            return @{ Code = -1; Body = $null }
        }
    }
    function CompleteUpload($session, $hdr) {
        try {
            $r = Invoke-WebRequest "$url/api/games/$($cgame.id)/upload/$session/complete" -Method Post `
                -Headers $hdr -UseBasicParsing
            return @{ Code = [int]$r.StatusCode; Body = ($r.Content | ConvertFrom-Json) }
        } catch {
            if ($_.Exception.Response) { return @{ Code = [int]$_.Exception.Response.StatusCode; Body = $null } }
            return @{ Code = -1; Body = $null }
        }
    }

    $third = [int]([Math]::Floor($cBytes.Length / 3))

    # ---- 1. The happy path assembles byte-for-byte ----
    $b1 = BeginUpload "hash-chunk-1" $null $chdr
    Check "begin hands back a session"            ($null -ne $b1.sessionId)
    Check "begin with no head reports no NoChange" ($null -eq $b1.noChange)

    $o = 0
    $r = PutChunk $b1.sessionId $o (Slice $o $third) $chdr;                       $o += $third
    Check "the first chunk is accepted"           ($r.Code -eq 200 -and $r.Body.bytesReceived -eq $o)
    $r = PutChunk $b1.sessionId $o (Slice $o $third) $chdr;                       $o += $third
    Check "the second chunk is accepted"          ($r.Code -eq 200 -and $r.Body.bytesReceived -eq $o)
    $r = PutChunk $b1.sessionId $o (Slice $o ($cBytes.Length - $o)) $chdr
    Check "the final chunk is accepted"           ($r.Code -eq 200 -and $r.Body.bytesReceived -eq $cBytes.Length)

    $done = CompleteUpload $b1.sessionId $chdr
    Check "complete publishes a version"          ($done.Code -eq 200 -and $done.Body.status -eq "Created")
    Check "the published size is the whole archive" ($done.Body.version.size -eq $cBytes.Length)
    Check "a completed session stages nothing"    (@(CStaged).Count -eq 0)

    $roundTrip = Join-Path $scratch "chunk-roundtrip.zip"
    Invoke-WebRequest "$url/api/games/$($cgame.id)/versions/$($done.Body.version.id)/download" `
        -OutFile $roundTrip -UseBasicParsing | Out-Null
    Check "the assembled archive is byte-identical" `
        (@(Compare-Object ([System.IO.File]::ReadAllBytes($roundTrip)) $cBytes -SyncWindow 0).Count -eq 0)

    $head1 = $done.Body.version.id

    # ---- 2. Begin short-circuits when the head already has this content ----
    $noChange = BeginUpload "hash-chunk-1" $head1 $chdr
    Check "begin on identical content reports NoChange" ($noChange.noChange.status -eq "NoChange")
    Check "and opens no session for it"                 ($null -eq $noChange.sessionId)

    # ---- 3. A replay of a whole chunk is a no-op, not a double-append ----
    $b2 = BeginUpload "hash-chunk-2" $head1 $chdr
    $r  = PutChunk $b2.sessionId 0 (Slice 0 $third) $chdr
    $again = PutChunk $b2.sessionId 0 (Slice 0 $third) $chdr
    Check "replaying a chunk at the same offset is accepted" ($again.Code -eq 200)
    Check "and does not append it twice"                     ($again.Body.bytesReceived -eq $third)

    # ---- 4. A gap AHEAD of the session is refused ----
    $gap = PutChunk $b2.sessionId ($third + 999) (Slice 0 16) $chdr
    Check "a chunk past the session's end is refused with 409" ($gap.Code -eq 409)

    # ---- 5. Another machine cannot touch the session, and neither can an unknown one ----
    $stolen = PutChunk $b2.sessionId $third (Slice $third 16) @{ "X-Api-Key" = $othr.apiKey }
    Check "another machine's key cannot append to the session" ($stolen.Code -eq 404)
    $ghost = PutChunk ([Guid]::NewGuid()) 0 (Slice 0 16) $chdr
    Check "an unknown session is refused with 404"             ($ghost.Code -eq 404)

    # ---- 6. Truncation must never publish ----
    # The whole point of the integrity gate: this session is told it is finished while holding only
    # a fragment. Pre-fix, complete moved those bytes to a real version path under the FULL
    # archive's content hash, and every machine that pulled it got a corrupt save.
    $shortDone = CompleteUpload $b2.sessionId $chdr
    Check "completing a truncated session is refused with 422" ($shortDone.Code -eq 422)
    Check "the truncated session published no version"         (@(CVersions).Count -eq 1)
    Check "and left nothing staged behind it"                  (@(CStaged).Count -eq 0)

    # ---- 7. A chunk that faults mid-body rolls back, so its retry is a real write ----
    # Raw socket, same reason as the single-shot section: it declares a Content-Length it never
    # delivers and then vanishes, which is what a dropped tunnel actually looks like. Pre-fix the
    # partial bytes stayed counted, so the retry below arrived BEHIND the session and was swallowed
    # as a replay - the archive then completed short, or the next chunk failed 409.
    $b3 = BeginUpload "hash-chunk-3" $head1 $chdr
    $client = [System.Net.Sockets.TcpClient]::new("127.0.0.1", $Port)
    $stream = $client.GetStream()
    $head = "PUT /api/games/$($cgame.id)/upload/$($b3.sessionId)/chunk?offset=0 HTTP/1.1`r`n" +
            "Host: localhost:$Port`r`nX-Api-Key: $($creg.apiKey)`r`n" +
            "Content-Type: application/octet-stream`r`nContent-Length: $third`r`n`r`n"
    $headBytes = [System.Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($headBytes, 0, $headBytes.Length)
    $stream.Write((Slice 0 512), 0, 512)          # a fraction of what was promised
    $stream.Flush()
    Start-Sleep -Milliseconds 500
    $client.Close()                                # and gone
    Start-Sleep -Seconds 2

    Check "the server survived the mid-chunk disconnect" `
        ((Invoke-WebRequest "$url/api/admin/status" -UseBasicParsing).StatusCode -eq 200)

    # The retry, in full, at the offset the client still believes it is at. It must be treated as an
    # exact-offset write - if the partial bytes were kept it is read as a replay and answers $third
    # without writing, which is the corruption this section exists to pin.
    $retry = PutChunk $b3.sessionId 0 (Slice 0 $third) $chdr
    Check "the retry of a faulted chunk is accepted"  ($retry.Code -eq 200)
    Check "and the session holds exactly one copy of it" ($retry.Body.bytesReceived -eq $third)

    $o = $third
    $r = PutChunk $b3.sessionId $o (Slice $o ($cBytes.Length - $o)) $chdr
    Check "the rest of the archive still lines up"    ($r.Code -eq 200 -and $r.Body.bytesReceived -eq $cBytes.Length)
    $recovered = CompleteUpload $b3.sessionId $chdr
    Check "a session that survived a faulted chunk publishes" ($recovered.Code -eq 200)

    $recoveredZip = Join-Path $scratch "chunk-recovered.zip"
    Invoke-WebRequest "$url/api/games/$($cgame.id)/versions/$($recovered.Body.version.id)/download" `
        -OutFile $recoveredZip -UseBasicParsing | Out-Null
    Check "and what it published is byte-identical to the source" `
        (@(Compare-Object ([System.IO.File]::ReadAllBytes($recoveredZip)) $cBytes -SyncWindow 0).Count -eq 0)

    # ---- 8. Crossing the cap ACROSS chunks is a 413, and the session goes with it ----
    # Cumulative, not per-request: each body here is comfortably under the request-size limit, so
    # Kestrel lets both through and only ArchiveStore's own running total can notice. That is the
    # shape a real over-cap upload has (4 MiB chunks against a 200 MB cap), and the one where the
    # throw lands MID-chunk with bytes from that same chunk already written.
    #
    # Left alive, the session answered the agent's retry with a replay 200 and then failed the NEXT
    # chunk on an offset mismatch, so the reason the upload died stopped being visible anywhere.
    $b4 = BeginUpload "hash-chunk-oversize" $head1 $chdr
    $half = New-Object byte[] (1200KB)
    $under = PutChunk $b4.sessionId 0 $half $chdr
    Check "a chunk under the cap is accepted"               ($under.Code -eq 200)
    $over = PutChunk $b4.sessionId $half.Length $half $chdr
    Check "the chunk that crosses the cap is refused with 413" ($over.Code -eq 413)
    $after = PutChunk $b4.sessionId 0 (Slice 0 16) $chdr
    Check "an over-cap session is dropped, not left stale"  ($after.Code -eq 404)
    Check "the over-cap attempt published no version"       (@(CVersions).Count -eq 2)
    Check "and left nothing staged"                         (@(CStaged).Count -eq 0)

    Remove-Item Env:Storage__MaxUploadMb -ErrorAction SilentlyContinue
    Stop-TestServer $srv
}
finally {
    foreach ($p in $script:servers) { Stop-TestServer $p }
    git worktree remove --force $oldWt 2>&1 | Out-Null
    git worktree prune
    Remove-Item Env:ASPNETCORE_URLS, Env:Storage__DbPath, Env:Storage__ArchiveRoot, `
        Env:Backup__Enabled, Env:Logging__EventLog__LogLevel__Default, Env:Commands__LeaseMinutes, `
        Env:Storage__MaxUploadMb, Env:Server__PublicBaseUrl, Env:Storage__AgentInstallerRoot, `
        Env:AgentUpdate__MaxInstallerMb, Env:SteamGridDb__ApiKey -ErrorAction SilentlyContinue
    Get-Job -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "==== SERVER BUG BOUNTY: $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
