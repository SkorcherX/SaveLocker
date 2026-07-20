# Enrollment integration tests (Linux agent Phase 4) - 14 checks. Runs on BOTH Windows and Linux.
#
#   Windows: Windows PowerShell 5.1 or pwsh, drives src/Agent (net10.0-windows).
#   Linux:   pwsh (PowerShell Core), drives src/Agent.Linux (net10.0).
#
# `enroll` lives in Agent.Core, so it is the SAME code on both hosts - which is the point: a Deck
# and a Windows PC enroll through one implementation.
#
# What this does NOT cover: TLS pinning. The harness server is plain http, which has no identity to
# pin, so the checks below assert only that the agent correctly records NO pin and says so. The
# pin-and-warn path needs an https server - see tests/run-enrollment-tls-tests.ps1.
#
# Prerequisites: server running on http://localhost:5179 (no admin password), agent built in Debug.
# Usage:  .\tests\run-enrollment-tests.ps1   /   pwsh tests/run-enrollment-tests.ps1

$ErrorActionPreference = "Continue"

# $IsWindows is auto-defined by PowerShell Core but NOT by Windows PowerShell 5.1,
# where its absence is itself the signal that we are on Windows.
$onWindows = if ($null -eq $IsWindows) { $true } else { $IsWindows }

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify"
$server  = "http://localhost:5179"

if ($onWindows) {
    $inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    $dotnet = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
    $dll    = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
} else {
    $dotnet = "dotnet"
    $dll    = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
}

if (-not (Test-Path $dll)) {
    Write-Host "Agent not built: $dll"
    exit 2
}

New-Item -ItemType Directory -Force $scratch | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $dll @args 2>&1 }

function Mint($body) {
    Invoke-RestMethod -Uri "$server/api/admin/enrollments" -Method Post `
        -ContentType "application/json" -Body ($body | ConvertTo-Json)
}

$stamp = Get-Date -Format "HHmmss"

# ---- A game must exist on the server for the policy to carry one ----
$gameName = "EnrollGame-$stamp"
$game = Invoke-RestMethod -Uri "$server/api/games" -Method Post -ContentType "application/json" `
    -Body (@{ name = $gameName; manifestKey = $null; customPathsJson = $null } | ConvertTo-Json)

# A second game whose save location is stored as a TEMPLATE rather than a literal path. A literal
# cannot mean the same folder on two machines — different user, different drive, or a Proton prefix —
# and two agents disagreeing about a game's save root by one segment is what makes a restore nest a
# folder under itself and delete the correctly-placed copy. Enroll is the Deck's whole setup, so it
# is the first thing that has to expand one.
$tmplName = "EnrollTemplate-$stamp"
$tmplGame = Invoke-RestMethod -Uri "$server/api/games" -Method Post -ContentType "application/json" `
    -Body (@{ name = $tmplName; manifestKey = $null; customPathsJson = $null } | ConvertTo-Json)

$onWin = if ($null -eq $IsWindows) { $true } else { $IsWindows }
if ($onWin) {
    $tmplExpected = Join-Path $env:APPDATA "$tmplName\Saves"
    New-Item -ItemType Directory -Force $tmplExpected | Out-Null
    Invoke-RestMethod ("$server/api/games/" + $tmplGame.id + "/save-dir?value=" +
        [uri]::EscapeDataString("<winAppData>/$tmplName/Saves")) -Method Post | Out-Null
}

# ---- 1. Mint: the policy carries a token, the server URL and the enabled games ----
$deckName = "phase4-deck-$stamp"
$mint = Mint @{ machineName = $deckName; ttlMinutes = 15 }
$policy = $mint.policy

Check "mint returns a raw token"                 ($policy.token -and $policy.token.Length -gt 20)
Check "policy carries the server URL"            ($policy.serverUrl -eq $server)
Check "policy binds the machine name"            ($policy.machineName -eq $deckName)
Check "policy carries the server's games"        ($policy.games.gameId -contains $game.id)

$policyFile = Join-Path $scratch "enroll-$stamp.json"
$policy | ConvertTo-Json -Depth 6 | Set-Content -Path $policyFile -Encoding utf8

# ---- 2. Enroll: the agent trades the token for a real machine key ----
# --name is deliberately WRONG here: the token was minted for $deckName, and a bound token must
# win. Otherwise a leaked file could be spent to claim any machine's identity.
$cfg = Join-Path $scratch "enroll-cfg-$stamp.json"
$out = Agent enroll --file $policyFile --name "an-imposter-name" --config $cfg
$enrollExit = $LASTEXITCODE
$saved = Get-Content $cfg -Raw | ConvertFrom-Json

Check "enroll succeeds"                          ($enrollExit -eq 0)
Check "enroll stored a machine API key"          ($saved.ApiKey -and $saved.ApiKey.Length -gt 20)
Check "enroll stored the machine id"             ($null -ne $saved.MachineId)
Check "bound token wins over --name"             ($saved.MachineName -eq $deckName)
Check "enroll pre-seeded the server's game"      ($saved.Games.GameId -contains $game.id)

if ($onWin) {
    $tmplTracked = $saved.Games | Where-Object { $_.GameId -eq $tmplGame.id }
    Check "enroll expanded a templated save path" ($tmplTracked.SaveDirectory -eq $tmplExpected)
    Check "the raw template was not stored"       ($tmplTracked.SaveDirectory -notlike "*<winAppData>*")
    Remove-Item $tmplExpected -Recurse -Force -ErrorAction SilentlyContinue
}
Check "plain http records no TLS pin"            ($null -eq $saved.ServerPin)

# ---- 3. The issued key is real: it authenticates an agent route ----
# `status` hits /api/games/{id}/state with X-Api-Key. A bogus key would 401 and print an error.
$status = Agent status --config $cfg
Check "the issued key authenticates"             ($LASTEXITCODE -eq 0 -and $status -notmatch "401")

# ---- 4. Single-use: the same file cannot be spent twice ----
$cfg2 = Join-Path $scratch "enroll-cfg2-$stamp.json"
$replay = Agent enroll --file $policyFile --config $cfg2
Check "replaying the file is refused"            ($LASTEXITCODE -ne 0 -and "$replay" -match "already been used")

# ---- 5. Revoked token is refused ----
$revokable = Mint @{ machineName = "phase4-revoked-$stamp"; ttlMinutes = 15 }
$revFile = Join-Path $scratch "enroll-revoked-$stamp.json"
$revokable.policy | ConvertTo-Json -Depth 6 | Set-Content -Path $revFile -Encoding utf8
Invoke-RestMethod -Uri "$server/api/admin/enrollments/$($revokable.id)" -Method Delete | Out-Null

$revOut = Agent enroll --file $revFile --config (Join-Path $scratch "enroll-cfg3-$stamp.json")
Check "revoked token is refused"                 ($LASTEXITCODE -ne 0 -and "$revOut" -match "Unknown enrollment token")

# ---- 6. A forged/expired file is refused ----
$expiredFile = Join-Path $scratch "enroll-expired-$stamp.json"
@{
    version    = 1
    serverUrl  = $server
    token      = "totally-made-up-token"
    expiresAt  = (Get-Date).ToUniversalTime().AddMinutes(-1).ToString("o")
    machineName = "phase4-expired-$stamp"
} | ConvertTo-Json | Set-Content -Path $expiredFile -Encoding utf8

$expOut = Agent enroll --file $expiredFile --config (Join-Path $scratch "enroll-cfg4-$stamp.json")
Check "expired file is refused"                  ($LASTEXITCODE -ne 0 -and "$expOut" -match "expired")

# An unexpired file with a garbage token must be refused by the SERVER, not just the local clock.
$forgedFile = Join-Path $scratch "enroll-forged-$stamp.json"
@{
    version    = 1
    serverUrl  = $server
    token      = "totally-made-up-token"
    expiresAt  = (Get-Date).ToUniversalTime().AddMinutes(15).ToString("o")
    machineName = "phase4-forged-$stamp"
} | ConvertTo-Json | Set-Content -Path $forgedFile -Encoding utf8

$forgedOut = Agent enroll --file $forgedFile --config (Join-Path $scratch "enroll-cfg5-$stamp.json")
Check "forged token is refused by the server"    ($LASTEXITCODE -ne 0 -and "$forgedOut" -match "Unknown enrollment token")

# ---- 7. The console can see the spent token, and who spent it ----
$list = Invoke-RestMethod -Uri "$server/api/admin/enrollments"
$mine = $list | Where-Object { $_.id -eq $mint.id }
Check "console shows the token as redeemed"      ($null -ne $mine.redeemedAt -and $mine.redeemedByMachineName -eq $deckName)

Write-Host ""
Write-Host "Enrollment: $pass passed, $fail failed."
if ($fail -gt 0) { exit 1 }
