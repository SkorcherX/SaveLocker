# Per-file delta upload (tasks/PerFileDeltaUpload.md) - regression coverage for "upload only
# changed files, not the whole save folder".
#
# Isolated-state harness: it owns its port, its SQLite DB, and its archive store, and it starts and
# stops every server it uses. It never touches the :5179 dev server.
#
# What each section proves, and why it's the thing that actually matters:
#   1. A clean fast-forward push of a multi-file save with ONE changed file takes the delta path —
#      asserted from the server's own audit trail (`upload.delta`, "N of M"), not inferred from a
#      passing push. The first TWO pushes to a fresh game are expected to stay on the full-archive
#      path (no server-side manifest baseline exists yet to diff against) — that self-healing
#      sequence is asserted explicitly so a change that skips it is caught here, not discovered on a
#      real fleet as "why did this take three pushes to speed up".
#   2. A pull on a SECOND machine after a chain of full + delta pushes reproduces every file
#      byte-for-byte — the actual proof that copy-forward reconstruction is correct, not merely that
#      the server accepted the upload.
#   3. A deletion-only push (0 files need fresh bytes) removes the file on pull without resurrecting
#      it from the base archive.
#   4. A tracked game below the size/count floor never attempts the manifest round trip at all.
#   5. A diverged push (stale parent) still produces a CONFLICT with a COMPLETE archive, and does
#      NOT go through the delta path — the one case reconstruction must never be trusted for.
#   6. A hostile delta payload (a file the server never asked for) is refused outright, proving the
#      allow-list check works. Tested against a deliberately broken payload, per this project's own
#      rule for security-relevant code (run-hardening-tests.ps1's convention).
#
# Prerequisites: server + Windows agent built in Debug (`dotnet build ... -c Debug`).
# Usage:  .\tests\run-delta-upload-tests.ps1

param([int]$Port = 5185)

$ErrorActionPreference = "Continue"

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify-delta-upload"
$url     = "http://localhost:$Port"

$inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$dotnet    = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
$agentDll  = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
$serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
if (-not (Test-Path $agentDll))  { Write-Host "Agent not built: $agentDll";   exit 2 }
if (-not (Test-Path $serverDll)) { Write-Host "Server not built: $serverDll"; exit 2 }

Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $scratch | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $agentDll @args 2>&1 }
function Get-Json($path) {
    return ((Invoke-WebRequest "$url$path" -UseBasicParsing).Content | ConvertFrom-Json)
}

# ---- Server (its own port + state; never touches the dev server) ----
$state = Join-Path $scratch "state"
New-Item -ItemType Directory -Force (Join-Path $state "archives") | Out-Null
$env:ASPNETCORE_URLS      = $url
$env:Storage__DbPath      = Join-Path $state "savelocker.db"
$env:Storage__ArchiveRoot = Join-Path $state "archives"
$env:Backup__Enabled      = "false"

$serverProc = Start-Process -FilePath $dotnet -ArgumentList $serverDll -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput "$scratch\server.out.log" -RedirectStandardError "$scratch\server.err.log"

$up = $false
foreach ($i in 1..60) {
    Start-Sleep -Milliseconds 500
    try { Invoke-RestMethod "$url/api/admin/status" -TimeoutSec 3 | Out-Null; $up = $true; break } catch { }
}
if (-not $up) {
    Write-Host "FAIL: server did not start on $url"
    Get-Content "$scratch\server.err.log" -ErrorAction SilentlyContinue | Select-Object -Last 20
    Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue
    exit 1
}

try {
    # ---- Fixture helpers ----
    function New-RandomFile($path, $bytes = 400000) {
        $rng = [System.Random]::new()
        $buf = New-Object byte[] $bytes
        $rng.NextBytes($buf)
        [System.IO.File]::WriteAllBytes($path, $buf)
    }
    function Get-Sha256($path) { (Get-FileHash $path -Algorithm SHA256).Hash }

    $pcCfg  = Join-Path $scratch "pc-config.json"
    $lapCfg = Join-Path $scratch "lap-config.json"
    $pcSave  = Join-Path $scratch "pc_save";  New-Item -ItemType Directory -Force $pcSave  | Out-Null
    $lapSave = Join-Path $scratch "lap_save"; New-Item -ItemType Directory -Force $lapSave | Out-Null
    foreach ($c in @($pcCfg, $lapCfg)) {
        @{ ServerUrl = $url; Games = @() } | ConvertTo-Json | Set-Content -Path $c -Encoding utf8
    }

    $gameName = "DeltaTest-$(Get-Date -Format HHmmss)"
    Agent register --name "DeltaPC"  --config $pcCfg  | Out-Null
    Agent register --name "DeltaLap" --config $lapCfg | Out-Null

    1..8 | ForEach-Object { New-RandomFile (Join-Path $pcSave "slot$_.sav") }
    Agent add-game --name $gameName --dir $pcSave --config $pcCfg | Out-Null

    function Get-LastAudit($action, $game) {
        return (Get-Json "/api/audit?limit=30") | Where-Object { $_.gameName -eq $game -and $_.action -eq $action } |
            Select-Object -First 1
    }

    Write-Host ""
    Write-Host "==== 1. Self-healing baseline, then a real delta ===="

    # First-ever push: no parent to diff against at all.
    Agent push $gameName --config $pcCfg | Out-Null
    $a1 = Get-LastAudit "upload.create" $gameName
    Check "first push: full archive (no parent to diff against)" ($null -ne $a1)

    # Second push: a manifest IS sent now, but the head has no stored baseline yet - still full.
    New-RandomFile (Join-Path $pcSave "slot3.sav")
    Agent push $gameName --config $pcCfg | Out-Null
    $a2 = Get-LastAudit "upload.create" $gameName
    $d2 = Get-LastAudit "upload.delta" $gameName
    Check "second push: still full (server had no stored manifest to diff against)" `
        ($null -ne $a2 -and ($null -eq $d2 -or $d2.timestamp -lt $a1.timestamp))

    # Third push: the second push's manifest is now the baseline - this one should genuinely delta.
    New-RandomFile (Join-Path $pcSave "slot7.sav")
    Agent push $gameName --config $pcCfg | Out-Null
    $d3 = Get-LastAudit "upload.delta" $gameName
    Check "third push takes the delta path" ($null -ne $d3)
    Check "third push's delta needed exactly 1 of 8 files" ($d3.detail -eq "1 of 8 file(s) needed fresh bytes")

    Write-Host ""
    Write-Host "==== 2. A second machine's pull reproduces every file byte-for-byte ===="
    Agent add-game --name $gameName --dir $lapSave --config $lapCfg | Out-Null
    Agent pull $gameName --config $lapCfg | Out-Null

    $srcFiles = Get-ChildItem $pcSave | Sort-Object Name
    $pulledOk = $true
    foreach ($f in $srcFiles) {
        $dst = Join-Path $lapSave $f.Name
        if (-not (Test-Path $dst)) { $pulledOk = $false; continue }
        if ((Get-Sha256 $f.FullName) -ne (Get-Sha256 $dst)) { $pulledOk = $false }
    }
    Check "pulled save matches the source byte-for-byte, across a full+full+delta chain" `
        ($pulledOk -and (Get-ChildItem $lapSave).Count -eq $srcFiles.Count)

    Write-Host ""
    Write-Host "==== 3. A deletion-only push removes the file on pull, without resurrecting it ===="
    Remove-Item (Join-Path $pcSave "slot5.sav")
    Agent push $gameName --config $pcCfg | Out-Null
    $d4 = Get-LastAudit "upload.delta" $gameName
    Check "deletion-only push takes the delta path with zero files needed" `
        ($null -ne $d4 -and $d4.detail -eq "0 of 7 file(s) needed fresh bytes")

    Agent pull $gameName --config $lapCfg --force | Out-Null
    Check "the deleted file is gone after pull, not resurrected" (-not (Test-Path (Join-Path $lapSave "slot5.sav")))
    Check "the other 7 files survived the deletion push" ((Get-ChildItem $lapSave).Count -eq 7)

    Write-Host ""
    Write-Host "==== 4. Below the size/count floor, no manifest round trip happens at all ===="
    $smallSave = Join-Path $scratch "small_save"; New-Item -ItemType Directory -Force $smallSave | Out-Null
    "hello" | Set-Content -Path (Join-Path $smallSave "a.txt") -Encoding ascii -NoNewline
    "world" | Set-Content -Path (Join-Path $smallSave "b.txt") -Encoding ascii -NoNewline
    $smallGame = "DeltaSmall-$(Get-Date -Format HHmmss)"
    Agent add-game --name $smallGame --dir $smallSave --config $pcCfg | Out-Null
    Agent push $smallGame --config $pcCfg | Out-Null
    "changed" | Set-Content -Path (Join-Path $smallSave "a.txt") -Encoding ascii -NoNewline
    Agent push $smallGame --config $pcCfg | Out-Null
    $smallDelta = Get-LastAudit "upload.delta" $smallGame
    $smallCreate = Get-LastAudit "upload.create" $smallGame
    Check "a small tracked game never triggers a delta audit entry" ($null -eq $smallDelta)
    Check "a small tracked game's second push still just creates a version" ($null -ne $smallCreate)

    Write-Host ""
    Write-Host "==== 5. A diverged push stays on the full-archive path, never delta ===="
    # PC advances the head again...
    New-RandomFile (Join-Path $pcSave "slot1.sav")
    Agent push $gameName --config $pcCfg | Out-Null
    # ...while the laptop, still on the OLD parent, pushes its own unrelated change.
    New-RandomFile (Join-Path $lapSave "slot2.sav")
    Agent push $gameName --config $lapCfg | Out-Null

    $conflictAudit = Get-LastAudit "upload.conflict" $gameName
    $deltaAfterConflict = Get-LastAudit "upload.delta" $gameName
    Check "the diverged push is recorded as a conflict" ($null -ne $conflictAudit)
    Check "the diverged push did NOT take the delta path" `
        ($null -eq $deltaAfterConflict -or $deltaAfterConflict.timestamp -lt $conflictAudit.timestamp)

    $conflict = @(Get-Json "/api/conflicts" | Where-Object { $_.gameId -eq (Get-Json "/api/overview" |
        Where-Object { $_.game.name -eq $gameName }).game.id })[0]
    $lapMachine = (Get-Json "/api/machines") | Where-Object { $_.name -eq "DeltaLap" }
    $lapVersion = $conflict.versionBId
    $conflictZip = Join-Path $scratch "conflict.zip"
    $lapConfig = Get-Content $lapCfg | ConvertFrom-Json
    Invoke-WebRequest "$url/api/versions/$lapVersion/download" -Headers @{ "X-Api-Key" = $lapConfig.ApiKey } `
        -OutFile $conflictZip -UseBasicParsing | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($conflictZip)
    $entryCount = $zip.Entries.Count
    $zip.Dispose()
    Check "the conflicting version's archive is COMPLETE, not a partial delta payload" ($entryCount -eq 7)

    Write-Host ""
    Write-Host "==== 6. A hostile delta payload (an entry the server never asked for) is refused ===="
    # Resolve the conflict first so the game is back to a clean fast-forward state.
    Invoke-RestMethod "$url/api/conflicts/$($conflict.id)/resolve?version=$($conflict.versionAId)" -Method Post | Out-Null
    Agent pull $gameName --config $pcCfg | Out-Null
    # A genuine local change, so the server's per-file diff has something real to ask for - without
    # this, PC's manifest already matches the head exactly and NeedPaths would legitimately be empty.
    New-RandomFile (Join-Path $pcSave "slot4.sav")

    $pcConfig = Get-Content $pcCfg | ConvertFrom-Json
    $headers = @{ "X-Api-Key" = $pcConfig.ApiKey; "Content-Type" = "application/json" }
    $gameId = (Get-Json "/api/overview" | Where-Object { $_.game.name -eq $gameName }).game.id
    $headVersion = (Get-Json "/api/games/$gameId/state").head
    $versionsBefore = @(Get-Json "/api/games/$gameId/versions")

    # Build the manifest for the CURRENT local save dir, matching what the agent itself would send,
    # but claim a DIFFERENT content hash - a real hash would short-circuit straight to NoChange
    # before the server ever looks at Files, same trick run-server-bugbounty-tests.ps1 already uses
    # ("hash-good-2") since the server trusts the client-asserted aggregate hash either way.
    $flipChar = if ($headVersion.contentHash.Substring(0, 1) -eq "f") { "0" } else { "f" }
    $fakeHash = $flipChar + $headVersion.contentHash.Substring(1)
    $files = Get-ChildItem $pcSave | Sort-Object Name | ForEach-Object {
        @{ Path = $_.Name; Sha256 = (Get-Sha256 $_.FullName).ToLower(); Size = $_.Length }
    }
    $beginBody = @{ ContentHash = $fakeHash; ParentVersionId = $headVersion.id
                    Force = $false; Files = $files } | ConvertTo-Json -Depth 5
    $begin = Invoke-RestMethod "$url/api/games/$gameId/upload/begin" -Method Post -Headers $headers -Body $beginBody

    # The hostile check only means anything if the server actually granted the delta path - assert
    # that explicitly rather than let a wrong assumption about test state pass silently.
    Check "negotiation reached the delta path (prerequisite for this section)" ($begin.useDeltaPath -eq $true)

    if ($begin.useDeltaPath -eq $true) {
        Check "the negotiation actually asked for at least one file" ($begin.needPaths.Count -ge 1)

        # Include the REAL needed entry (correct content) so the "delta is missing an entry the
        # server asked for" refusal can't be what fires instead - PLUS one path the server never
        # asked for, so a Created result here could only mean the allow-list check itself is gone,
        # not that the payload just happened to be incomplete.
        $hostileZip = Join-Path $scratch "hostile.zip"
        if (Test-Path $hostileZip) { Remove-Item $hostileZip }
        $hostileArchive = [System.IO.Compression.ZipFile]::Open($hostileZip, "Create")
        foreach ($needed in $begin.needPaths) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $hostileArchive, (Join-Path $pcSave $needed), $needed) | Out-Null
        }
        $entry = $hostileArchive.CreateEntry("not-a-real-file.sav")
        $writer = New-Object System.IO.StreamWriter($entry.Open())
        $writer.Write("hostile bytes the server never asked for")
        $writer.Dispose()
        $hostileArchive.Dispose()

        $bytes = [System.IO.File]::ReadAllBytes($hostileZip)
        Invoke-WebRequest "$url/api/games/$gameId/upload/$($begin.sessionId)/chunk?offset=0" -Method Put `
            -Headers @{ "X-Api-Key" = $pcConfig.ApiKey } -Body $bytes `
            -ContentType "application/octet-stream" -UseBasicParsing | Out-Null

        $complete = Invoke-RestMethod "$url/api/games/$gameId/upload/$($begin.sessionId)/complete" -Method Post `
            -Headers @{ "X-Api-Key" = $pcConfig.ApiKey }
        Check "a delta payload naming an unasked-for path is refused (RetryFull, not Created)" `
            ($complete.status -eq "RetryFull")

        $versionsAfter = @(Get-Json "/api/games/$gameId/versions")
        Check "the hostile payload published no new version" ($versionsAfter.Count -eq $versionsBefore.Count)
    }

    Write-Host ""
    Write-Host "==== 7. A head that moves BETWEEN Begin and Complete is refused with RetryFull ===="
    # Distinct from section 5: that one diverges at Begin, so the server declines the delta before a
    # byte moves. This is the other window - the delta was legitimately negotiated, and the base it
    # was diffed against stopped being the head while the chunks were in flight. Reconstructing
    # against a stale base would silently drop whatever the other push changed in files this delta
    # never touched, so the only safe answer is "send me a whole archive instead".
    Agent push $gameName --config $pcCfg | Out-Null          # clean fast-forward state again
    Agent pull $gameName --config $lapCfg | Out-Null

    $headBefore = (Get-Json "/api/games/$gameId/state").head
    New-RandomFile (Join-Path $pcSave "slot6.sav")
    $filesNow = Get-ChildItem $pcSave | Sort-Object Name | ForEach-Object {
        @{ Path = $_.Name; Sha256 = (Get-Sha256 $_.FullName).ToLower(); Size = $_.Length }
    }
    # A REAL content hash, unlike section 6's deliberately-fabricated one. The server now verifies
    # that a reconstructed archive hashes to what the agent declared, so a fake hash here would be
    # refused for that reason and this section would "pass" without ever testing the stale-base
    # branch it exists for. Asking the agent for it also cross-checks that HashDirectory and
    # ComputeManifest still agree on the same bytes.
    $realHash7 = ((Agent hash --dir $pcSave) -join "").Trim()
    $beginBody7 = @{ ContentHash = $realHash7; ParentVersionId = $headBefore.id
                     Force = $false; Files = $filesNow } | ConvertTo-Json -Depth 5
    $begin7 = Invoke-RestMethod "$url/api/games/$gameId/upload/begin" -Method Post -Headers $headers -Body $beginBody7
    Check "section 7 prerequisite: the delta path was granted" ($begin7.useDeltaPath -eq $true)

    if ($begin7.useDeltaPath -eq $true) {
        # ...now the LAPTOP moves the head, from a different base, while PC's session is open.
        # Forced so this prerequisite cannot depend on the laptop's own accumulated sync state.
        New-RandomFile (Join-Path $lapSave "slot2.sav")
        Agent push $gameName --config $lapCfg --force | Out-Null
        $headMoved = (Get-Json "/api/games/$gameId/state").head
        Check "section 7 prerequisite: the head actually moved" ($headMoved.id -ne $headBefore.id)

        $deltaZip7 = Join-Path $scratch "delta7.zip"
        if (Test-Path $deltaZip7) { Remove-Item $deltaZip7 }
        $archive7 = [System.IO.Compression.ZipFile]::Open($deltaZip7, "Create")
        foreach ($needed in $begin7.needPaths) {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive7, (Join-Path $pcSave $needed), $needed) | Out-Null
        }
        $archive7.Dispose()

        $bytes7 = [System.IO.File]::ReadAllBytes($deltaZip7)
        Invoke-WebRequest "$url/api/games/$gameId/upload/$($begin7.sessionId)/chunk?offset=0" -Method Put `
            -Headers @{ "X-Api-Key" = $pcConfig.ApiKey } -Body $bytes7 `
            -ContentType "application/octet-stream" -UseBasicParsing | Out-Null

        $versions7Before = @(Get-Json "/api/games/$gameId/versions")
        $complete7 = Invoke-RestMethod "$url/api/games/$gameId/upload/$($begin7.sessionId)/complete" `
            -Method Post -Headers @{ "X-Api-Key" = $pcConfig.ApiKey }
        Check "a delta whose base stopped being the head is refused with RetryFull" `
            ($complete7.status -eq "RetryFull")
        Check "the stale delta published no new version" `
            (@(Get-Json "/api/games/$gameId/versions").Count -eq $versions7Before.Count)

        # And the agent's own next push does NOT silently drop: RetryFull never reaches SyncEngine,
        # so the full-archive retry runs and the genuine divergence it finds is recorded as such.
        # A regression that let RetryFull escape would show up here as no audit entry at all.
        $conflictsBefore = @(Get-Json "/api/audit?limit=50" |
            Where-Object { $_.gameName -eq $gameName -and $_.action -eq "upload.conflict" }).Count
        Agent push $gameName --config $pcCfg | Out-Null
        $conflictsAfter = @(Get-Json "/api/audit?limit=50" |
            Where-Object { $_.gameName -eq $gameName -and $_.action -eq "upload.conflict" }).Count
        Check "the agent's retry lands a real outcome, not a silent no-op" `
            ($conflictsAfter -gt $conflictsBefore)
    }

    Write-Host ""
    Write-Host "==== 8. A malformed manifest is refused at Begin, before anything is staged ===="
    # The manifest reaches an in-memory session that outlives the request and, eventually, rows keyed
    # (VersionId, Path). A duplicate path used to surface as a primary-key violation AFTER the version
    # was committed - the worst possible moment to find out.
    function Try-Begin($files) {
        $body = @{ ContentHash = ("a" * 64); ParentVersionId = $null; Force = $false; Files = $files } |
                ConvertTo-Json -Depth 5
        try {
            Invoke-RestMethod "$url/api/games/$gameId/upload/begin" -Method Post -Headers $headers -Body $body | Out-Null
            return 200
        } catch { return [int]$_.Exception.Response.StatusCode }
    }

    Check "a manifest listing the same path twice is refused (400)" `
        ((Try-Begin @(@{ Path = "slot1.sav"; Sha256 = ("b" * 64); Size = 10 },
                      @{ Path = "slot1.sav"; Sha256 = ("c" * 64); Size = 10 })) -eq 400)
    Check "a manifest path that escapes the save folder is refused (400)" `
        ((Try-Begin @(@{ Path = "../escape.sav"; Sha256 = ("b" * 64); Size = 10 })) -eq 400)
    Check "a manifest entry with a negative size is refused (400)" `
        ((Try-Begin @(@{ Path = "slot1.sav"; Sha256 = ("b" * 64); Size = -1 })) -eq 400)
    Check "an empty manifest path is refused (400)" `
        ((Try-Begin @(@{ Path = ""; Sha256 = ("b" * 64); Size = 10 })) -eq 400)

    Write-Host ""
    Write-Host "==== 9. The AGENT refuses a server that asks for a file outside the save folder ===="
    # Every other section here checks that the server rejects a bad payload. This is the other
    # direction, and the one nothing covered: the server names the paths a delta carries, so a
    # server the agent was pointed at by a forged enrollment file (Decisions.md 4) could name a
    # path that is not a save file at all and have the agent zip it up and upload it.
    $stubPort = 5186
    $stubUrl  = "http://localhost:$stubPort/"
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add($stubUrl)
    $listenerOk = $true
    try { $listener.Start() } catch { $listenerOk = $false }

    if (-not $listenerOk) {
        Write-Host "SKIP: HttpListener could not bind $stubUrl (needs a urlacl or an elevated shell)."
    }
    else {
        try {
            # A real file OUTSIDE the save folder. Without it, a FileNotFoundException would stop the
            # upload too, and this section would pass without proving the agent refused anything.
            $bait = Join-Path $scratch "hostile-target.txt"
            Set-Content -Path $bait -Value "a secret this server must never receive" -Encoding utf8

            # A genuine local change first: without one the push short-circuits on
            # "no local changes since last sync" and never contacts the stub at all.
            New-RandomFile (Join-Path $pcSave "slot5.sav")

            $stubCfg = Join-Path $scratch "stub-config.json"
            $cfgObj = Get-Content $pcCfg -Raw | ConvertFrom-Json
            $cfgObj.ServerUrl = "http://localhost:$stubPort"
            $cfgObj | ConvertTo-Json -Depth 10 | Set-Content -Path $stubCfg -Encoding utf8

            $stubOut = Join-Path $scratch "stub-push-out.txt"
            $proc = Start-Process -FilePath $dotnet -PassThru -NoNewWindow -RedirectStandardOutput $stubOut `
                -ArgumentList @($agentDll, "push", $gameName, "--config", $stubCfg)

            $beginSeen = $false; $chunkSeen = $false
            $deadline = (Get-Date).AddSeconds(90)
            while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
                $task = $listener.GetContextAsync()
                while (-not $task.AsyncWaitHandle.WaitOne(250)) {
                    if ($proc.HasExited -or (Get-Date) -ge $deadline) { break }
                }
                if (-not $task.IsCompleted) { break }

                $ctx  = $task.Result
                $path = $ctx.Request.Url.AbsolutePath
                if ($path -like "*/upload/begin") {
                    $beginSeen = $true
                    $json = @{ sessionId = [guid]::NewGuid().ToString(); noChange = $null
                               useDeltaPath = $true; needPaths = @("../hostile-target.txt") } |
                            ConvertTo-Json -Compress
                }
                elseif ($path -like "*/chunk") { $chunkSeen = $true; $json = '{"bytesReceived":0}' }
                # The push is followed by a heartbeat; answer it in the shape the reporter expects so
                # a stub-shaped null does not crash the agent before its output can be read.
                else { $json = '{"escalatedConflicts":[],"commands":[],"acknowledged":[]}' }

                $buf = [System.Text.Encoding]::UTF8.GetBytes($json)
                $ctx.Response.StatusCode = 200
                $ctx.Response.ContentType = "application/json"
                $ctx.Response.ContentLength64 = $buf.Length
                $ctx.Response.OutputStream.Write($buf, 0, $buf.Length)
                $ctx.Response.Close()
            }
            if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }

            # beginSeen is asserted too: without it, an agent that failed for some unrelated reason
            # would never reach the chunk request either, and this would pass having proved nothing.
            Check "section 9 prerequisite: the agent did negotiate with the stub server" $beginSeen
            Check "the agent uploaded NOTHING after being asked for a path outside the save folder" `
                (-not $chunkSeen)

            $pushOut = if (Test-Path $stubOut) { Get-Content $stubOut -Raw } else { "" }
            Check "the refusal is reported, not silently swallowed" ($pushOut -match "REFUSED")
        }
        finally { $listener.Stop(); $listener.Close() }
    }
}
finally {
    if ($serverProc -and -not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

Write-Host ""
Write-Host "==== DELTA UPLOAD RESULT: $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 } else { exit 0 }
