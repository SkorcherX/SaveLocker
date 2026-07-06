# Agent integration tests — 10 checks.
# Prerequisites: server running on http://localhost:5179, agent built in Debug.
# Usage:  .\tests\run-agent-tests.ps1
#
# Scratch state (generated configs, save dirs) is written to .verify/ (git-ignored).
# Fixture files (manifest.yaml) live alongside this script in tests/.

$ErrorActionPreference = "Continue"

$root     = Split-Path $PSScriptRoot -Parent
$dotnet   = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$dll      = Join-Path $root "src\Agent\bin\Debug\net9.0-windows\LocalGameSync.Agent.dll"
$fixtures = $PSScriptRoot
$scratch  = Join-Path $root ".verify"

New-Item -ItemType Directory -Force $scratch | Out-Null

$pcCfg  = Join-Path $scratch "pc-config.json"
$lapCfg = Join-Path $scratch "laptop-config.json"
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

# Fresh agent configs pointing at local server and fixture manifest.
foreach ($c in @($pcCfg, $lapCfg)) {
    @{ ServerUrl = "http://localhost:5179"; ManifestCachePath = "$fixtures\manifest.yaml"; Games = @() } |
        ConvertTo-Json | Set-Content -Path $c -Encoding utf8
}

# Detect-config written to scratch so the ManifestCachePath resolves correctly.
$detectCfg = Join-Path $scratch "detect-config.json"
@{ ServerUrl = "http://localhost:5179"; MachineName = "DetectTest"; ManifestCachePath = "$fixtures\manifest.yaml"; Games = @() } |
    ConvertTo-Json | Set-Content -Path $detectCfg -Encoding utf8

# ---- Detection (Phase 2): resolve a manifest game's save dir on this machine ----
$detectOut = Agent resolve --config $detectCfg --manifest "LGS Test Game"
$expected  = Join-Path $env:APPDATA "LGSTestGame"
Check "detection resolved <winAppData> save dir" (($detectOut -join "`n") -like "*$expected*")

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

$status = Agent status --config $pcCfg
Check "status shows conflict for SyncGame" (($status -join "`n") -like "*SyncGame*CONFLICT*")

Write-Host ""
Write-Host "==== AGENT RESULT: $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
