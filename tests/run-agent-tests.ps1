# Agent integration tests — 45 checks on Windows / 43 on Linux. Runs on BOTH.
#
#   Windows: Windows PowerShell 5.1 or pwsh, drives src/Agent (net10.0-windows).
#   Linux:   pwsh (PowerShell Core), drives src/Agent.Linux (net10.0).
#
# The sync brain under test is the same Agent.Core on both — that is the point. Only the
# host binary and the save-detection expectation differ, and both differences are explicit
# below rather than being quietly skipped.
#
# Prerequisites: server running on http://localhost:5179, agent built in Debug.
# Usage:  .\tests\run-agent-tests.ps1   /   pwsh tests/run-agent-tests.ps1
#
# Scratch state (generated configs, save dirs) is written to .verify/ (git-ignored).
# Fixture files (manifest.yaml) live alongside this script in tests/.

$ErrorActionPreference = "Continue"

# $IsWindows is auto-defined by PowerShell Core but NOT by Windows PowerShell 5.1,
# where its absence is itself the signal that we are on Windows.
$onWindows = if ($null -eq $IsWindows) { $true } else { $IsWindows }

$root     = Split-Path $PSScriptRoot -Parent
$fixtures = $PSScriptRoot
$scratch  = Join-Path $root ".verify"

if ($onWindows) {
    # `dotnet` is not always on PATH in an open shell after a winget install (see Gotchas.md),
    # so prefer the known install location and fall back to PATH.
    $inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    $dotnet = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
    $dll    = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
} else {
    # AssemblyName is 'savelocker' (the command users type), not the project name.
    $dotnet = "dotnet"
    $dll    = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
}

if (-not (Test-Path $dll)) {
    Write-Host "Agent not built: $dll"
    Write-Host "Build it first, then re-run."
    exit 2
}

New-Item -ItemType Directory -Force $scratch | Out-Null

$pcCfg   = Join-Path $scratch "pc-config.json"
$lapCfg  = Join-Path $scratch "laptop-config.json"
$pcSave  = Join-Path $scratch "pc_save"
$lapSave = Join-Path $scratch "laptop_save"

New-Item -ItemType Directory -Force $pcSave  | Out-Null
New-Item -ItemType Directory -Force $lapSave | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $dll @args 2>&1 }

$manifest = Join-Path $fixtures "manifest.yaml"

# Fresh agent configs pointing at local server and fixture manifest.
foreach ($c in @($pcCfg, $lapCfg)) {
    @{ ServerUrl = "http://localhost:5179"; ManifestCachePath = $manifest; Games = @() } |
        ConvertTo-Json | Set-Content -Path $c -Encoding utf8
}

# Detect-config written to scratch so the ManifestCachePath resolves correctly.
$detectCfg = Join-Path $scratch "detect-config.json"
@{ ServerUrl = "http://localhost:5179"; MachineName = "DetectTest"; ManifestCachePath = $manifest; Games = @() } |
    ConvertTo-Json | Set-Content -Path $detectCfg -Encoding utf8

# ---- Detection: resolve a manifest game's save dir on this machine ----
# The manifest's paths are WINDOWS paths (<winAppData>). On Windows they resolve against the
# host. On Linux they deliberately resolve to NOTHING: a Proton game's paths live inside its own
# Wine prefix (per-game, so the caller must pass --prefix), and inventing a host path for a
# Windows game would be worse than admitting we cannot resolve it. Asserting the Linux refusal
# is the point — a resolver that guessed here would silently sync the wrong directory.
if ($onWindows) {
    # `resolve` only reports a directory that actually exists, so create it before asking.
    $expected = Join-Path $env:APPDATA "LGSTestGame"
    New-Item -ItemType Directory -Force $expected | Out-Null

    $detectOut = Agent resolve --config $detectCfg --manifest "LGS Test Game"
    Check "detection resolved <winAppData> save dir" (($detectOut -join "`n") -like "*$expected*")

    # <winPublic> must be the profile ROOT (C:\Users\Public). Resolving it as CommonDocuments
    # (C:\Users\Public\Documents) doubles the segment for every real manifest entry, which all look
    # like "<winPublic>/Documents/...". That silently broke detection for those games on Windows
    # while a Deck resolved them correctly — the two disagreeing about the same game's save root by
    # one segment is precisely what makes a restore nest and delete.
    $publicRoot = if ($env:PUBLIC) { $env:PUBLIC } else { "C:\Users\Public" }
    $publicExpected = Join-Path $publicRoot "Documents\LGSPublicTestGame"
    New-Item -ItemType Directory -Force $publicExpected | Out-Null

    # Create the DOUBLED path too, so the resolver has to choose between them. Without this the
    # second assertion is vacuous: the buggy resolver returns nothing at all (its path does not
    # exist), and "output contains no Documents\Documents" passes for the wrong reason.
    $publicDoubled = Join-Path $publicRoot "Documents\Documents\LGSPublicTestGame"
    New-Item -ItemType Directory -Force $publicDoubled | Out-Null
    try {
        $publicOut = Agent resolve --config $detectCfg --manifest "LGS Public Game"
        Check "detection resolved <winPublic> save dir" (($publicOut -join "`n") -like "*$publicExpected*")
        Check "<winPublic> did not double Documents"    (-not (($publicOut -join "`n") -like "*Documents\Documents*"))
    }
    finally {
        Remove-Item (Join-Path $publicRoot "Documents\Documents") -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $publicExpected -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    $detectOut = Agent resolve --config $detectCfg --manifest "LGS Test Game"
    Check "detection refuses to invent a host path for a Windows game" `
        (($detectOut -join "`n") -like "*no existing save directory found*")
}

# ---- Register two machines ----
$pcReg  = Agent register --config $pcCfg  --name PC
$lapReg = Agent register --config $lapCfg --name Laptop
Check "PC registered"     (($pcReg  -join "`n") -like "*Registered 'PC'*")
Check "Laptop registered" (($lapReg -join "`n") -like "*Registered 'Laptop'*")

# ---- PC: track game, create a save, push ----
Set-Content -Path (Join-Path $pcSave "slot1.sav") -Value "level=1" -Encoding utf8
Agent add-game --config $pcCfg --name "SyncGame" --dir $pcSave | Out-Null
$push1 = Agent push --config $pcCfg SyncGame
Check "PC initial push succeeds" (($push1 -join "`n") -like "*pushed new version*")

# ---- Laptop: track same game at its own dir, pull ----
Agent add-game --config $lapCfg --name "SyncGame" --dir $lapSave | Out-Null
$pull1 = Agent pull --config $lapCfg SyncGame
Check "Laptop pull restores save" (($pull1 -join "`n") -like "*restored latest save*")
Check "pulled file content matches PC" (
    (Test-Path (Join-Path $lapSave "slot1.sav")) -and
    ((Get-Content (Join-Path $lapSave "slot1.sav") -Raw).Trim() -eq "level=1")
)

# ---- Second pull is a no-op ----
$pull2 = Agent pull --config $lapCfg SyncGame
Check "second pull is a no-op (up to date)" (($pull2 -join "`n") -like "*already up to date*")

# ---- PC advances (v2), stale laptop push -> conflict ----
Set-Content -Path (Join-Path $pcSave "slot1.sav") -Value "level=2" -Encoding utf8
$push2 = Agent push --config $pcCfg SyncGame
Check "PC second push succeeds (v2)" (($push2 -join "`n") -like "*pushed new version*")

Set-Content -Path (Join-Path $lapSave "slot1.sav") -Value "level=2-laptop" -Encoding utf8
$pushConflict = Agent push --config $lapCfg SyncGame
Check "Laptop stale push reports CONFLICT" (($pushConflict -join "`n") -like "*CONFLICT*")

# ---- An agent may DESCRIBE a save location, but never hijack one ----
# The agent is the untrusted side of this, so both guards live on the server. Without them a single
# misconfigured machine could rewrite where the whole fleet looks for a game's saves — and a save
# root that is wrong by one segment is what makes a restore nest and delete.
$tmplGame = Invoke-RestMethod "http://localhost:5179/api/games" -Method Post -ContentType "application/json" `
    -Body (@{ name = "TemplateGuard-$stamp"; manifestKey = $null; customPathsJson = $null } | ConvertTo-Json)
$agentKey = (Invoke-RestMethod "http://localhost:5179/api/machines/register" -Method Post `
    -ContentType "application/json" -Body (@{ name = "TemplateAgent-$stamp" } | ConvertTo-Json)).apiKey
$hdr = @{ "X-Api-Key" = $agentKey }
function PostTemplate($v) {
    try {
        $r = Invoke-WebRequest ("http://localhost:5179/api/agent/games/" + $tmplGame.id + "/template?value=" + [uri]::EscapeDataString($v)) `
            -Method Post -Headers $hdr -UseBasicParsing
        return $r.StatusCode
    } catch { return $_.Exception.Response.StatusCode.value__ }
}
# Ask for the ONE game rather than filtering /api/overview. Filtering there returned a collection,
# so [string] joined it with spaces and an empty result read as " " rather than "" — the assertion
# failed while the server was behaving correctly. A direct lookup has no such ambiguity.
$tmplGameId = [string]$tmplGame.id
function CurrentSuggested {
    $state = Invoke-RestMethod "http://localhost:5179/api/games/$tmplGameId/state"
    return [string]$state.game.suggestedSaveDir
}

# A LITERAL path must be refused: it means nothing on any other machine, and accepting it here is
# exactly how one machine's path becomes everyone's.
$literalCode = PostTemplate "C:\Users\someone\Documents\Whatever"
$afterLiteral = [string](CurrentSuggested)
Check "a literal path is refused as a template"  ($literalCode -eq 204)
Check "and nothing was stored"                   ([string]::IsNullOrEmpty($afterLiteral))
if (-not [string]::IsNullOrEmpty($afterLiteral)) { Write-Host "      stored value was: [$afterLiteral]" }

$firstCode = PostTemplate "<winDocuments>/My Games/Guard"
Check "a template is accepted when none is set"  ($firstCode -eq 200)
Check "the template was stored"                  ((CurrentSuggested) -eq "<winDocuments>/My Games/Guard")

# First correct machine wins. A second agent must not be able to redefine it.
$secondCode = PostTemplate "<winAppData>/Hijacked"
Check "an existing template is not overwritten"  ($secondCode -eq 204)
Check "the original template survived"           ((CurrentSuggested) -eq "<winDocuments>/My Games/Guard")

$status = Agent status --config $pcCfg
Check "status shows conflict for SyncGame" (($status -join "`n") -like "*SyncGame*CONFLICT*")

# ---- Resolving a conflict must un-stick BOTH machines (backlog 0.4) ----
# Resolving used to be a database edit that merely looked like an action. An agent's parent version
# advances only on a successful push or a pull, and the upload path deliberately does NOT advance it
# on conflict - so both machines stayed behind the new head and conflicted again on their very next
# save. The console said resolved; the fleet disagreed.
#
# The WINNER is the counter-intuitive half, and the one that bit hardest on real hardware: its content
# is already byte-identical to the new head, but its pointer still names the parent it presented, so
# its next push is rejected exactly like the loser's.
# WARNING: Invoke-RestMethod does NOT unroll a JSON array consistently. Windows PowerShell 5.1 hands
# the whole array back as ONE item, so @() wraps it instead of flattening it and every downstream
# filter silently matches nothing - which reads as "the server returned nothing" and sends you off to
# debug the wrong side entirely. ConvertFrom-Json unrolls identically on 5.1 and pwsh 7, and this
# suite runs on both. See Gotchas.md.
function JsonArray($url) { @((Invoke-WebRequest $url -UseBasicParsing).Content | ConvertFrom-Json) }

# ---- Conflicts must DEDUPE, and retention must keep running while conflicted (backlog 0.1 + 0.2) ----
# The head never advances while a conflict is open, so VersionAId is constant and a machine that
# keeps saving used to write one ConflictFlag row per push. That is how a single unresolved
# divergence became 75 rows, which the console then offered one at a time, oldest first. Retention
# made it worse from the other side: prune was reachable only from the fast-forward path, so a
# conflicted game pruned nothing AND had every version pinned by an open conflict - 80 versions and
# 2.66 GB on a game configured to keep 5.
$syncGame = (JsonArray "http://localhost:5179/api/overview") | Where-Object { $_.game.name -eq "SyncGame" }
$gameId   = "$($syncGame.game.id)"
Invoke-RestMethod "http://localhost:5179/api/games/$gameId/retain?value=2" -Method Post | Out-Null

Set-Content -Path (Join-Path $lapSave "slot1.sav") -Value "level=2b-laptop" -Encoding utf8
Agent push --config $lapCfg SyncGame | Out-Null
Set-Content -Path (Join-Path $lapSave "slot1.sav") -Value "level=2c-laptop" -Encoding utf8
Agent push --config $lapCfg SyncGame | Out-Null

$open = @((JsonArray "http://localhost:5179/api/conflicts") | Where-Object { "$($_.gameId)" -eq $gameId })
Check "three divergent pushes produced ONE conflict" ($open.Count -eq 1)

# Read 'count' through PSObject.Properties on purpose: every PowerShell object carries an intrinsic
# .Count, so $open[0].count is ambiguous with a JSON field of the same name.
$conflictCount = [int]$open[0].PSObject.Properties['count'].Value
Check "the conflict counted every divergent push"   ($conflictCount -eq 3)
Check "the conflict names the stuck machine"        (-not [string]::IsNullOrWhiteSpace("$($open[0].machineId)"))

# Three rejected payloads are enough evidence. A fourth ordinary push reports the condition but
# must not archive or upload the same divergent save again; a force-push remains the explicit escape.
$beforeBackoff = JsonArray "http://localhost:5179/api/games/$gameId/versions"
$backedOff = Agent push --config $lapCfg SyncGame
$afterBackoff = JsonArray "http://localhost:5179/api/games/$gameId/versions"
$openAfterBackoff = @((JsonArray "http://localhost:5179/api/conflicts") | Where-Object { "$($_.gameId)" -eq $gameId })
Check "fourth conflicted push reports upload backoff" (($backedOff -join "`n") -like "*upload paused after 3*")
Check "backoff sent no fourth archive"                 ($afterBackoff.Count -eq $beforeBackoff.Count)
Check "backoff did not bump the server conflict"       ([int]$openAfterBackoff[0].PSObject.Properties['count'].Value -eq 3)

# Newest-first: the save the user has actually been playing is the one offered. Surfacing the oldest
# is exactly what made the console useless during the incident.
$versions = JsonArray "http://localhost:5179/api/games/$gameId/versions"
Check "the conflict offers the NEWEST divergent save" ("$($open[0].versionBId)" -eq "$($versions[0].id)")
Check "retention still runs while conflicted"         ($versions.Count -le 4)

$conf = (JsonArray "http://localhost:5179/api/conflicts") | Select-Object -First 1
Check "the server recorded an open conflict" ($null -ne $conf)

# A Set-as-Latest that happened after the conflict must not be silently undone by resolving to an
# older option. Pick an intervening divergent version that is newer than VersionA but is not one of
# the two options currently offered.
$intervening = $versions | Where-Object {
    "$($_.id)" -ne "$($conf.versionAId)" -and "$($_.id)" -ne "$($conf.versionBId)"
} | Sort-Object { [DateTime]$_.createdAt } -Descending | Select-Object -First 1
Invoke-RestMethod "http://localhost:5179/api/games/$gameId/set-latest?version=$($intervening.id)" -Method Post | Out-Null

$rewindCode = try {
    (Invoke-WebRequest "http://localhost:5179/api/conflicts/$($conf.id)/resolve?version=$($conf.versionAId)" `
        -Method Post -UseBasicParsing).StatusCode
} catch { $_.Exception.Response.StatusCode.value__ }
$headAfterBlockedRewind = (Invoke-RestMethod "http://localhost:5179/api/games/$gameId/state").head.id
$rewindAudit = JsonArray "http://localhost:5179/api/audit?limit=50" |
    Where-Object { $_.action -eq "conflict.resolve_rewind_blocked" }
Check "resolve refuses to rewind a newer Latest"       ($rewindCode -eq 400)
Check "blocked rewind leaves Latest untouched"         ("$headAfterBlockedRewind" -eq "$($intervening.id)")
Check "blocked rewind is distinctly audited"           (@($rewindAudit).Count -ge 1)

# Keep both: the laptop's newest divergent version becomes Latest, while both snapshots are
# protected from retention until the admin explicitly unprotects one.
Invoke-RestMethod "http://localhost:5179/api/conflicts/$($conf.id)/resolve?version=$($conf.versionBId)&keepBoth=true" -Method Post | Out-Null
$afterKeepBoth = JsonArray "http://localhost:5179/api/games/$gameId/versions"
$protectedConflictVersions = @($afterKeepBoth | Where-Object {
    "$($_.id)" -in @("$($conf.versionAId)", "$($conf.versionBId)") -and $_.protected
})
Check "Keep both protects both conflict versions" ($protectedConflictVersions.Count -eq 2)

$queued = @((JsonArray "http://localhost:5179/api/commands") | Where-Object {
    "$($_.type)" -eq "Pull" -and "$($_.status)" -eq "Pending" -and "$($_.gameId)" -eq "$($conf.gameId)"
})
$queuedMachines = @($queued | ForEach-Object { "$($_.machineId)" } | Sort-Object -Unique)
Check "resolving queued a pull for BOTH machines"  ($queuedMachines.Count -eq 2)
Check "the queued pulls are GUARDED, not forced"   (@($queued | Where-Object { $_.force }).Count -eq 0)

# The winner: its content already matches the head, so the pull short-circuits before touching a
# single file and simply repairs the pointer.
$lapPull = Agent pull --config $lapCfg SyncGame
Check "the winner's pull is a no-op that repairs it" (($lapPull -join "`n") -like "*already up to date*")

# THE ASSERTION. This is the exact failure that stranded a real user: resolve, play, conflict again.
Set-Content -Path (Join-Path $lapSave "slot1.sav") -Value "level=3-laptop" -Encoding utf8
$lapPush = Agent push --config $lapCfg SyncGame
Check "the winner's next push no longer conflicts"  (($lapPush -join "`n") -like "*pushed new version*")

# The loser had cleanly synced its own version, so it has nothing unpushed and the guarded pull
# restores the winner over it. (A loser carrying NEWER local edits is blocked instead, which is the
# honest answer - forcing it would destroy work the server has never seen.)
$pcPull = Agent pull --config $pcCfg SyncGame
Check "the loser pulls the winner cleanly" (
    (Test-Path (Join-Path $pcSave "slot1.sav")) -and
    ((Get-Content (Join-Path $pcSave "slot1.sav") -Raw).Trim() -eq "level=3-laptop")
)

# ---- The console can prune and download without shell access (backlog 1.4 + 1.5) ----
# Retention otherwise runs only as a side effect of an upload, and version download was agent-only
# (X-Api-Key group), so "back this save up before doing something destructive to it" could not be
# offered as a console step at all. Those two gaps are why recovering from the 2026-07-22 incident
# needed curl against the admin API.
$beforeUnprotect = JsonArray "http://localhost:5179/api/games/$gameId/versions"
Check "protected conflict versions survive automatic pruning" (
    @($beforeUnprotect | Where-Object {
        "$($_.id)" -in @("$($conf.versionAId)", "$($conf.versionBId)")
    }).Count -eq 2
)

Invoke-RestMethod "http://localhost:5179/api/games/$gameId/versions/$($conf.versionAId)/protected?value=false" -Method Post | Out-Null
$pruned = Invoke-RestMethod "http://localhost:5179/api/games/$gameId/prune" -Method Post
$after  = JsonArray "http://localhost:5179/api/games/$gameId/versions"
Check "prune-now reports what it removed"   ($null -ne $pruned.removed)
Check "prune-now respects the retain limit" ($after.Count -le 3)
Check "unprotected losing version becomes prunable" (
    @($after | Where-Object { "$($_.id)" -eq "$($conf.versionAId)" }).Count -eq 0
)
Check "the still-protected version survives pruning" (
    @($after | Where-Object { "$($_.id)" -eq "$($conf.versionBId)" -and $_.protected }).Count -eq 1
)

$headVersionId = "$($after[0].id)"
$dl = Invoke-WebRequest "http://localhost:5179/api/games/$gameId/versions/$headVersionId/download" -UseBasicParsing
Check "a version downloads from the console" (($dl.StatusCode -eq 200) -and ($dl.RawContentLength -gt 0))

# DownloadVersionAsync resolves by version id alone, so the route has to check the game itself -
# otherwise any game's archive is reachable through any game's URL.
$guard = try {
    (Invoke-WebRequest "http://localhost:5179/api/games/$([Guid]::NewGuid())/versions/$headVersionId/download" -UseBasicParsing).StatusCode
} catch { $_.Exception.Response.StatusCode.value__ }
Check "download refuses a version from another game" ($guard -eq 404)

# ---- status must survive an admin password being set ----
# This suite ran against a server with NO admin password, and the AdminPasswordFilter is wide open
# in that state — so `status` calling an admin-filtered endpoint with only a machine key passed here
# for as long as the bug existed, and 401'd on any real server that had a password set. Set one and
# re-run the exact same command: the agent must not need an admin secret to read its own game state.
$adminPw = "verify-admin-$(Get-Random)"
try {
    Invoke-RestMethod "http://localhost:5179/api/admin/password" -Method Post `
        -ContentType "application/json" -Body (@{ password = $adminPw } | ConvertTo-Json) | Out-Null

    $statusPw = Agent status --config $pcCfg
    Check "status still works once an admin password is set" (($statusPw -join "`n") -like "*SyncGame*")
    Check "status did not 401"                               (-not (($statusPw -join "`n") -like "*401*"))
}
finally {
    # Clear it, or every later suite against this server needs the header.
    Invoke-RestMethod "http://localhost:5179/api/admin/password" -Method Post `
        -ContentType "application/json" -Headers @{ "X-Admin-Password" = $adminPw } `
        -Body (@{ password = "" } | ConvertTo-Json) | Out-Null
}

Write-Host ""
Write-Host "==== AGENT RESULT: $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
