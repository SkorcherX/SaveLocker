# Multiple save paths per game (tasks/multiple-save-paths, Group B) — two machines, one game with a
# second save folder, end to end through the real CLI against a real server.
#
# Isolated-state harness: it owns its port, its SQLite DB and its archive store, and starts and stops
# its own server. It never touches the :5179 dev server.
#
# What each section proves:
#   1. add-path gives a game a second folder, and the push carries it.
#   2. A machine that has no folder for it keeps a SHADOW copy, taken from the pull, so its hash is
#      the head's — the property that stops two machines handing a folder back and forth.
#   3. Two full pull+push cycles on both machines create no version and no conflict.
#   4. Mapping the folder later moves the shadow in, changes nothing on the server, and deletes it.
#   5-7. An edit, a deleted file, and an emptied folder all reach the other machine.
#   8. Mapping onto a folder with different files asks; --keep cloud takes the synced copy.
#   9. A mapped folder deleted on one machine comes back after one corrective round, not a loop.
#  10. The folder rules: a folder inside the primary one is refused, and leaves no key behind.
#  11. remove-path retires the key for the fleet.
#
# Prerequisites: server + Windows agent built in Debug.
# Usage:  .\tests\run-multipath-tests.ps1

param([int]$Port = 5199)

$ErrorActionPreference = "Continue"

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify-multipath"
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
function Get-Json($path) { (Invoke-WebRequest "$url$path" -UseBasicParsing).Content | ConvertFrom-Json }

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
    function New-Dir($name) { $d = Join-Path $scratch $name; New-Item -ItemType Directory -Force $d | Out-Null; $d }
    function Set-File($dir, $name, $text) {
        New-Item -ItemType Directory -Force $dir | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $dir $name), $text)
    }
    function Read-File($dir, $name) {
        $p = Join-Path $dir $name
        if (Test-Path $p) { [System.IO.File]::ReadAllText($p) } else { $null }
    }
    function Hash-Of($cfg) {
        $line = Agent hash $game --config $cfg | Select-Object -Last 1
        "$line".Split(':')[-1].Trim()
    }
    function Config-Game($cfg) { ((Get-Content $cfg -Raw | ConvertFrom-Json).Games | Where-Object { $_.Name -eq $game }) }
    # Through the pipeline: Windows PowerShell's ConvertFrom-Json hands a JSON array back as ONE item.
    function Version-Count { @((Get-Json "/api/games/$gameId/versions") | ForEach-Object { $_ }).Count }
    function Has-Conflict { (Get-Json "/api/games/$gameId/state").hasOpenConflict -eq $true }
    function Cycle($cfg) { Agent pull $game --config $cfg | Out-Null; Agent push $game --config $cfg | Out-Null }

    # Separate state dirs: each machine's shadows live beside its own config.
    $pcCfg  = Join-Path (New-Dir "pc-state") "config.json"
    $lapCfg = Join-Path (New-Dir "lap-state") "config.json"
    foreach ($c in @($pcCfg, $lapCfg)) {
        @{ ServerUrl = $url; Games = @() } | ConvertTo-Json | Set-Content -Path $c -Encoding utf8
    }
    $pcSave    = New-Dir "pc_save"
    $pcStates  = New-Dir "pc_states"
    $lapSave   = New-Dir "lap_save"
    $lapStates = Join-Path $scratch "lap_states"   # not created: mapping must create it
    $lapShadowRoot = Join-Path $scratch "lap-state\shadow"

    $game = "MultiPath-$(Get-Date -Format HHmmss)"
    Agent register --name "MpPC"  --config $pcCfg  | Out-Null
    Agent register --name "MpLap" --config $lapCfg | Out-Null

    Write-Host ""
    Write-Host "==== 1. add-path gives a game a second folder, and the push carries it ===="
    Set-File $pcSave "slot1.sav" "primary progress"
    Set-File $pcStates "quick.state" "state v1"
    Agent add-game --name $game --dir $pcSave --config $pcCfg | Out-Null
    $out = Agent add-path $game --key states --dir $pcStates --config $pcCfg
    Check "add-path succeeds" ($LASTEXITCODE -eq 0)
    $gameId = (Config-Game $pcCfg).GameId
    Check "the folder is mapped in config" ((Config-Game $pcCfg).ExtraPaths[0].Directory -eq $pcStates)
    Agent push $game --config $pcCfg | Out-Null
    Check "the push created a version" ((Version-Count) -eq 1)
    $added = (Get-Json "/api/audit?limit=30") | Where-Object { $_.action -eq "game.save_path.add" }
    Check "the server audited the new folder under the machine" ($null -ne $added)

    Write-Host ""
    Write-Host "==== 2. A machine with no folder for it keeps a shadow copy ===="
    Agent add-game --name $game --dir $lapSave --config $lapCfg | Out-Null
    Agent pull $game --config $lapCfg | Out-Null
    $lapGame = Config-Game $lapCfg
    Check "the pull took the new folder key on" ($lapGame.ExtraPaths.Count -eq 1 -and $lapGame.ExtraPaths[0].Key -eq "states")
    Check "it is not mapped on the second machine" ($null -eq $lapGame.ExtraPaths[0].Directory)
    $shadow = Join-Path $lapShadowRoot ("{0}\states" -f ([guid]$gameId).ToString("N"))
    Check "the shadow holds the folder's file" ((Read-File $shadow "quick.state") -eq "state v1")
    Check "the primary save restored" ((Read-File $lapSave "slot1.sav") -eq "primary progress")
    Check "both machines hash the same" ((Hash-Of $pcCfg) -eq (Hash-Of $lapCfg))

    Write-Host ""
    Write-Host "==== 3. Two full sync cycles on both machines: no new version, no conflict ===="
    $before = Version-Count
    1..2 | ForEach-Object { Cycle $pcCfg; Cycle $lapCfg }
    Check "the version count did not rise ($before -> $(Version-Count))" ((Version-Count) -eq $before)
    Check "no conflict" (-not (Has-Conflict))

    Write-Host ""
    Write-Host "==== 4. Mapping the folder later moves the shadow in ===="
    $hashBefore = Hash-Of $lapCfg
    $out = Agent add-path $game --key states --dir $lapStates --config $lapCfg
    Check "add-path maps the existing key" ($LASTEXITCODE -eq 0)
    Check "the shadow's file moved into the new folder" ((Read-File $lapStates "quick.state") -eq "state v1")
    Check "the shadow is gone" (-not (Test-Path $shadow))
    Check "the hash did not change" ((Hash-Of $lapCfg) -eq $hashBefore)
    Agent push $game --config $lapCfg | Out-Null
    Check "nothing new to push" ((Version-Count) -eq $before)

    Write-Host ""
    Write-Host "==== 5. An edit reaches the other machine ===="
    Set-File $pcStates "quick.state" "state v2"
    Set-File $pcStates "slot2.state" "second"
    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --config $lapCfg | Out-Null
    Check "the edited file arrived" ((Read-File $lapStates "quick.state") -eq "state v2")
    Check "the new file arrived" ((Read-File $lapStates "slot2.state") -eq "second")

    Write-Host ""
    Write-Host "==== 6. A deleted file is deleted on the other machine ===="
    Remove-Item (Join-Path $pcStates "slot2.state")
    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --config $lapCfg | Out-Null
    Check "the deletion arrived" (-not (Test-Path (Join-Path $lapStates "slot2.state")))
    Check "the other file stayed" ((Read-File $lapStates "quick.state") -eq "state v2")

    Write-Host ""
    Write-Host "==== 7. An emptied folder is emptied on the other machine ===="
    Remove-Item (Join-Path $pcStates "*")
    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --config $lapCfg | Out-Null
    Check "the folder is still there" (Test-Path $lapStates)
    Check "and empty" (@(Get-ChildItem $lapStates).Count -eq 0)
    Check "the primary save was untouched" ((Read-File $lapSave "slot1.sav") -eq "primary progress")

    Write-Host ""
    Write-Host "==== 8. Mapping onto different files asks, and --keep cloud takes the synced copy ===="
    $pcCfgDir  = New-Dir "pc_cfg"
    $lapCfgDir = New-Dir "lap_cfg"
    Set-File $pcCfgDir "settings.ini" "from the pc"
    Agent add-path $game --key cfg --dir $pcCfgDir --config $pcCfg | Out-Null
    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --config $lapCfg | Out-Null
    Set-File $lapCfgDir "settings.ini" "from the laptop"
    $out = Agent add-path $game --key cfg --dir $lapCfgDir --config $lapCfg | Out-String
    Check "different files: add-path refuses and asks" ($LASTEXITCODE -ne 0 -and $out -match "--keep")
    Check "nothing was overwritten" ((Read-File $lapCfgDir "settings.ini") -eq "from the laptop")
    Agent add-path $game --key cfg --dir $lapCfgDir --keep cloud --config $lapCfg | Out-Null
    Check "--keep cloud takes the synced copy" ((Read-File $lapCfgDir "settings.ini") -eq "from the pc")
    $before = Version-Count
    Agent push $game --config $lapCfg | Out-Null
    Check "and leaves nothing to push" ((Version-Count) -eq $before)

    Write-Host ""
    Write-Host "==== 9. A deleted folder comes back after ONE corrective round ===="
    Set-File $pcStates "quick.state" "state v3"
    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --config $lapCfg | Out-Null
    $before = Version-Count
    Remove-Item $lapStates -Recurse -Force
    Agent push $game --config $lapCfg | Out-Null     # no marker for the missing folder
    Cycle $pcCfg                                     # leaves its folder alone, pushes it back once
    Agent pull $game --config $lapCfg | Out-Null     # recreates it
    Check "the PC's folder was left alone" ((Read-File $pcStates "quick.state") -eq "state v3")
    Check "the folder is back on the laptop" ((Read-File $lapStates "quick.state") -eq "state v3")
    $afterRound = Version-Count
    Check "one round cost at most two versions ($before -> $afterRound)" ($afterRound - $before -le 2)
    1..2 | ForEach-Object { Cycle $pcCfg; Cycle $lapCfg }
    Check "and then it stays put ($afterRound -> $(Version-Count))" ((Version-Count) -eq $afterRound)
    Check "still no conflict" (-not (Has-Conflict))

    Write-Host ""
    Write-Host "==== 10. A folder inside the primary one is refused, and leaves no key ===="
    $nested = New-Dir "pc_save\nested"
    $out = Agent add-path $game --key inner --dir $nested --config $pcCfg | Out-String
    Check "a nested folder is refused" ($LASTEXITCODE -ne 0 -and $out -match "nested")
    $elsewhere = New-Dir "pc_inner"
    Agent add-path $game --key inner --dir $elsewhere --config $pcCfg | Out-Null
    Check "the key was never taken on the server" ($LASTEXITCODE -eq 0)

    Write-Host ""
    Write-Host "==== 11. remove-path retires the key for the fleet ===="
    Agent remove-path $game --key inner --config $pcCfg | Out-Null
    Check "remove-path succeeds" ($LASTEXITCODE -eq 0)
    Check "the folder left this machine's config" (@((Config-Game $pcCfg).ExtraPaths | Where-Object { $_.Key -eq "inner" }).Count -eq 0)
    Check "its files stay on disk" (Test-Path $elsewhere)
    $out = Agent add-path $game --key inner --dir $elsewhere --config $pcCfg | Out-String
    Check "the retired key cannot be reused" ($LASTEXITCODE -ne 0 -and $out -match "before")
    $removed = (Get-Json "/api/audit?limit=50") | Where-Object { $_.action -eq "game.save_path.remove" }
    Check "the removal was audited" ($null -ne $removed)
}
finally {
    Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "==== MULTIPATH RESULT: $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
exit 0
