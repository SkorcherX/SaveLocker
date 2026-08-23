# One place to drive the throwaway test environment: a Windows agent, a headless WSL agent, a
# Steam Deck (or any SSH-reachable Linux box) and a dashboard container, all running beside — never
# instead of — the installed release agent.
#
#   .\tests\testenv.ps1 build     build all four from this working tree
#   .\tests\testenv.ps1 up        start them (registers on first run)
#   .\tests\testenv.ps1 down      stop them; the installed agent is never touched
#   .\tests\testenv.ps1 status    what is running, and which build
#   .\tests\testenv.ps1 test      run the suites
#   .\tests\testenv.ps1 sync      copy this tree's uncommitted changes into the WSL clone
#   .\tests\testenv.ps1 logs      tail every log the rig writes
#   .\tests\testenv.ps1 clean     down, then DELETE every test build and all its state — WSL,
#                                 Windows, the Deck and the dashboard container/image/volume
#   .\tests\testenv.ps1 deck-config -DeckHost deck@<ip> [-DeckServerUrl http://<lan-ip>:5080]
#                                 write/update tests/testenv.local.ps1 (below) instead of hand-
#                                 editing it; no args just prints what's currently saved
#
# Separation rests on three test-only variables (Gotchas.md → Test-only environment variables):
# SAVELOCKER_STATE_ROOT moves all Windows state, SAVELOCKER_TRAY_PORT moves the local API *and*
# scopes the single-instance mutex, SAVELOCKER_RUNKEY_SUBPATH keeps "Start with Windows" off the
# real Run key. Linux needs none of them — XDG_DATA_HOME already does the whole job, and the Deck
# target reuses that same trick over SSH: a self-contained build lands in its own directory pair
# (-DeckPrefix and "$DeckPrefix-state"), never ~/.local/share/SaveLocker, which is both the real
# install's binaries AND its state (Gotchas.md → Linux agent).
#
# The Deck has no universal default — every LAN differs — so it only runs when configured. Copy
# tests/testenv.local.ps1.example to tests/testenv.local.ps1 (gitignored) and fill in:
#   $env:SAVELOCKER_DECK_HOST        deck@192.168.68.67-style SSH target
#   $env:SAVELOCKER_DECK_SERVER_URL  this PC's LAN IP, e.g. http://192.168.68.58:5080 — the Deck
#                                     cannot dial "localhost" and reach a container on another
#                                     machine. Needed for `up`, not for `down`/`status`/`clean`.
# testenv.local.ps1 is loaded automatically below if present — re-edit it whenever the Deck's IP
# changes rather than re-typing an env var every shell. An explicit -DeckHost/-DeckServerUrl on the
# command line always wins over it. Leaving both unset skips the Deck everywhere `-Only` defaults
# to 'all'; passing `-Only deck` explicitly without them is an error instead of a silent no-op. The
# Deck is "awake only when the maintainer wakes it" (CONTEXT.md), so every SSH call has a short
# connect timeout and reports unreachable rather than hanging or aborting the rest of the rig.
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('build', 'up', 'down', 'status', 'test', 'sync', 'logs', 'clean', 'deck-config')]
    [string]$Command = 'status',

    # Never a released version number. A test build stamped with one compares equal to the real
    # release forever, so the update channel can never move it (Gotchas.md).
    [string]$Version = '0.5.10-test',

    [string]$StateRoot = $(if ($env:SAVELOCKER_TEST_ROOT) { $env:SAVELOCKER_TEST_ROOT }
                           else { Join-Path $env:LOCALAPPDATA 'SaveLocker-test' }),
    [int]$ConsolePort = 5080,
    [int]$WinPort     = 5188,
    # Not 5188: WSL2 publishes Linux listeners onto Windows localhost, so they really do collide.
    [int]$LinuxPort   = 5187,
    [string]$Distro   = 'Ubuntu',
    [string]$WslRepo  = '$HOME/SaveLocker',

    # SSH target for a real Deck, "user@host" — e.g. deck@192.168.68.67. No default: unlike the
    # Windows/WSL targets there is no "this machine", so an unset host means "don't touch the Deck"
    # rather than a guess. Set once via the env var so it never has to be typed.
    [string]$DeckHost = $env:SAVELOCKER_DECK_HOST,
    # Distinct from both the WSL rig's :5187 and the real Deck agent's :5178 — a real, separate
    # machine, so nothing here collides on the wire the way WSL2's localhost forwarding does.
    [int]$DeckPort = 5177,
    # Remote directory for the extracted self-contained build. State lives in the sibling
    # "$DeckPrefix-state" (set as XDG_DATA_HOME by testenv-deck.sh) — see header comment.
    [string]$DeckPrefix = '~/savelocker-test',
    [string]$DeckServerUrl = $env:SAVELOCKER_DECK_SERVER_URL,

    [ValidateSet('all', 'windows', 'linux', 'deck', 'console')]
    [string]$Only = 'all',

    [ValidateSet('all', 'agents', 'dashboard')]
    [string]$Suite = 'all',

    # Real trays, real waits, ~5 minutes, and it needs an interactive desktop.
    [switch]$SkipWinAgentSuite,
    [switch]$AgentsOnly
)

$ErrorActionPreference = 'Continue'

# Deck settings live here, not just in $env: vars set for one shell — a home LAN's DHCP lease can
# move the Deck's IP at any time, so this needs to be a place you go edit ONE line and have every
# future session pick it up, not a shell profile you forget you edited. Gitignored (tests/*.local.ps1):
# it names your LAN, not the project's. testenv.local.ps1.example is the committed template.
# Explicit -DeckHost/-DeckServerUrl on the command line still win over whatever this sets.
$deckConfig = Join-Path $PSScriptRoot 'testenv.local.ps1'
if (Test-Path $deckConfig) {
    . $deckConfig
    if (-not $PSBoundParameters.ContainsKey('DeckHost') -and $env:SAVELOCKER_DECK_HOST) {
        $DeckHost = $env:SAVELOCKER_DECK_HOST
    }
    if (-not $PSBoundParameters.ContainsKey('DeckServerUrl') -and $env:SAVELOCKER_DECK_SERVER_URL) {
        $DeckServerUrl = $env:SAVELOCKER_DECK_SERVER_URL
    }
}

$root      = Split-Path $PSScriptRoot -Parent
$agentDll  = Join-Path $root 'src\Agent\bin\Debug\net10.0-windows\SaveLocker.Agent.dll'
$container = 'savelocker-test'
$image     = 'savelocker:test'
$volume    = 'savelocker-test-data'
$serverUrl = "http://localhost:$ConsolePort"
$winState  = Join-Path $StateRoot 'SaveLocker'

$inProgramFiles = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (Test-Path $inProgramFiles) { $dotnet = $inProgramFiles } else { $dotnet = 'dotnet' }

function Say  { param([string]$m) Write-Host "== $m" -ForegroundColor Cyan }
function Warn { param([string]$m) Write-Host "!! $m" -ForegroundColor Yellow }

function Use-TestEnvVars {
    $env:SAVELOCKER_STATE_ROOT     = $StateRoot
    $env:SAVELOCKER_TRAY_PORT      = "$WinPort"
    $env:SAVELOCKER_RUNKEY_SUBPATH = 'Software\SaveLocker\TestRun'
}
function Clear-TestEnvVars {
    # Leaving either set makes later runs in this shell behave in ways that look like bugs.
    Remove-Item Env:\SAVELOCKER_STATE_ROOT, Env:\SAVELOCKER_TRAY_PORT, Env:\SAVELOCKER_RUNKEY_SUBPATH `
        -ErrorAction SilentlyContinue
}

# Not `wslpath`: wsl.exe strips the backslashes out of a Windows path argument before the Linux side
# ever sees it, so "D:\a\b" arrives as "D:ab". Converting here is deterministic and needs no round trip.
function ConvertTo-WslPath {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    return '/mnt/' + $full.Substring(0, 1).ToLower() + ($full.Substring(2) -replace '\\', '/')
}

function Invoke-Wsl {
    param([string]$Sub, [string]$StdinFile, [string]$ArtifactDir)

    $sh = Join-Path $PSScriptRoot 'testenv.sh'
    if (-not (Test-Path $sh)) { throw "missing $sh" }
    $shWsl = ConvertTo-WslPath $sh

    # WIN_REPO is passed for every subcommand, not just sync: the build stages agent-ui/dist from it.
    $vars = @(
        "SAVELOCKER_WSL_REPO='$WslRepo'",
        "SAVELOCKER_LINUX_PORT=$LinuxPort",
        "SAVELOCKER_SERVER_URL='$serverUrl'",
        "SAVELOCKER_TEST_VERSION='$Version'",
        "SAVELOCKER_WIN_REPO='$(ConvertTo-WslPath $root)'"
    )
    if ($Sub -eq 'sync') {
        # The COMMON git dir, not this worktree: a worktree's .git is a file pointing at a Windows
        # path that WSL cannot follow, while branches live in the shared object store.
        $common = (& git -C $root rev-parse --path-format=absolute --git-common-dir)
        $vars += "SAVELOCKER_WIN_GITDIR='$(ConvertTo-WslPath (Split-Path $common -Parent))'"
        $vars += "SAVELOCKER_BRANCH='$(& git -C $root rev-parse --abbrev-ref HEAD)'"
    }
    if ($ArtifactDir) {
        # build-deck's only: where to drop the finished tarball so Windows can find it without
        # guessing at a \\wsl$\ UNC path.
        $vars += "SAVELOCKER_ARTIFACT_DIR='$(ConvertTo-WslPath $ArtifactDir)'"
    }

    # A .sh copied from this (CRLF) tree gives bash "bad interpreter: ...^M", so strip CR on the way
    # in rather than trusting the checkout's line endings.
    $cmd = "sed 's/\r`$//' '$shWsl' > ~/.savelocker-testenv.sh; " +
           ($vars -join ' ') + " bash ~/.savelocker-testenv.sh $Sub"
    if ($StdinFile) {
        $cmd += " < '$(ConvertTo-WslPath $StdinFile)'"
    }
    & wsl -d $Distro -- bash -c $cmd
}

function Test-DeckConfigured { return [bool]$DeckHost }

# The agent's local API — including the Deck test daemon's — is loopback-only, ALWAYS
# (AgentApiServer.Start: "binding it to a LAN interface would expose [machine control] to the
# whole network"; Decisions.md records a `--lan` flag that existed once and was withdrawn). So
# "http://<deck-ip>:<port>" is not reachable from this PC no matter what's set up on the Deck -
# there is nothing to set up, it refuses on principle. The daemon's own startup message says the
# sanctioned way to reach it remotely: an SSH tunnel.
function Get-DeckUiHint { return "ssh -L ${DeckPort}:localhost:${DeckPort} $DeckHost   then open http://localhost:$DeckPort" }

# Gates build/up (which respect -Only): explicit '-Only deck' with no host configured is an error,
# 'all' with no host configured is a silent skip so the default all-in-one flow still works for
# whoever hasn't wired up a Deck. down/status/logs/clean run unconditionally whenever a host IS
# configured instead — see the switch block, matching how 'down' already ignores -Only entirely.
function Use-Deck {
    if ($Only -eq 'deck') {
        if (-not $DeckHost) { throw "No Deck configured - set -DeckHost or `$env:SAVELOCKER_DECK_HOST (e.g. deck@192.168.68.67)." }
        return $true
    }
    if ($Only -eq 'all') {
        if ($DeckHost) { return $true }
        Warn "deck skipped - set -DeckHost or `$env:SAVELOCKER_DECK_HOST to include it"
        return $false
    }
    return $false
}

# Runs a subcommand of testenv-deck.sh over SSH — the same shape as Invoke-Wsl, just over the
# network instead of wsl.exe. A short ConnectTimeout matters here in a way it does not for WSL: the
# Deck is only awake when the maintainer wakes it (CONTEXT.md), so a cold Deck must fail in seconds,
# not hang on the default TCP timeout. Every caller wraps this in try/catch and Warn — a sleeping
# Deck must never abort the rest of the rig.
function Invoke-Deck {
    param([string]$Sub)

    $sh = Join-Path $PSScriptRoot 'testenv-deck.sh'
    if (-not (Test-Path $sh)) { throw "missing $sh" }

    # Same CRLF trap as Invoke-Wsl: scp copies this working tree's bytes as-is, so strip CR on the
    # remote side rather than trusting the checkout's line endings.
    & scp -o ConnectTimeout=5 -q $sh "${DeckHost}:/tmp/.savelocker-testenv-deck.raw"
    if ($LASTEXITCODE -ne 0) { throw "cannot reach $DeckHost (asleep?)" }

    $vars = @(
        "SAVELOCKER_DECK_PREFIX='$DeckPrefix'",
        "SAVELOCKER_DECK_PORT=$DeckPort",
        "SAVELOCKER_SERVER_URL='$DeckServerUrl'",
        "SAVELOCKER_TEST_VERSION='$Version'"
    )
    $cmd = "sed 's/\r`$//' /tmp/.savelocker-testenv-deck.raw > /tmp/.savelocker-testenv-deck.sh; " +
           ($vars -join ' ') + " bash /tmp/.savelocker-testenv-deck.sh $Sub"
    & ssh -o ConnectTimeout=5 $DeckHost $cmd
    if ($LASTEXITCODE -ne 0) { throw "deck '$Sub' failed (exit $LASTEXITCODE)" }
}

function Build-Deck {
    Say "building the Deck agent as $Version (self-contained linux-x64)"
    Build-AgentUi
    $artifactDir = Join-Path $env:TEMP 'savelocker-testenv-artifacts'
    New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
    Invoke-Wsl 'build-deck' -ArtifactDir $artifactDir
    $tarball = Join-Path $artifactDir "savelocker-$Version-linux-x64.tar.gz"
    if (-not (Test-Path $tarball)) { throw "deck build did not produce $tarball" }
    Write-Host "  built: $tarball"
}

function Start-Deck {
    $tarball = Join-Path (Join-Path $env:TEMP 'savelocker-testenv-artifacts') "savelocker-$Version-linux-x64.tar.gz"
    if (-not (Test-Path $tarball)) { throw "not built - run: .\tests\testenv.ps1 build -Only deck" }
    if (-not $DeckServerUrl) {
        throw "no -DeckServerUrl / `$env:SAVELOCKER_DECK_SERVER_URL - the Deck can't reach 'localhost', it needs this PC's LAN IP (e.g. http://192.168.68.58:$ConsolePort)"
    }

    Say "pushing $(Split-Path $tarball -Leaf) to $DeckHost"
    & scp -o ConnectTimeout=5 -q $tarball "${DeckHost}:/tmp/.savelocker-test.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "scp to $DeckHost failed (asleep?)" }

    Invoke-Deck 'up'
}

function Stop-Deck { Invoke-Deck 'down' }

# The installed release agent runs as SaveLocker.Agent.exe, so a dotnet.exe host with the agent DLL
# on its command line can only ever be a build from this tree. Nothing here can reach the real one.
function Get-TestTray {
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*SaveLocker.Agent.dll*' }
}

function Test-ConsoleUp {
    try { Invoke-RestMethod -Uri "$serverUrl/api/admin/status" -TimeoutSec 4 } catch { $null }
}

function Get-WinAgentState {
    $tokenPath = Join-Path $winState 'api-token'
    if (-not (Test-Path $tokenPath)) { return $null }
    $token = (Get-Content $tokenPath -Raw).Trim()
    try {
        Invoke-RestMethod -Uri "http://localhost:$WinPort/api/state" `
            -Headers @{ 'X-SaveLocker-Token' = $token } -TimeoutSec 4
    } catch { $null }
}

# The Windows agent has exactly ONE pre-existing warning (MSB3277, a WindowsBase 4.0/5.0 conflict
# from WebView2) and a second one means something. So: stay quiet at the expected count, and dump
# the whole log the moment it changes.
function Invoke-Build {
    param([string]$Project, [int]$ExpectedWarnings)

    $out = & $dotnet build $Project --no-incremental -v q --nologo 2>&1
    $code = $LASTEXITCODE
    $text = $out -join "`n"
    $m = [regex]::Match($text, '(\d+) Warning\(s\)')
    if ($m.Success) { $warnings = [int]$m.Groups[1].Value } else { $warnings = 0 }

    if ($code -ne 0 -or $warnings -ne $ExpectedWarnings) {
        $out | ForEach-Object { Write-Host $_ }
        if ($code -ne 0) { throw "build failed: $Project" }
        Warn "expected $ExpectedWarnings warning(s), got $warnings - the log is above"
    } else {
        Write-Host "  build succeeded ($warnings warning(s), as expected)"
    }
}

# The rig is only isolated while it maps nothing real. A test agent enrolls from a scan of THIS
# machine, so it can map a real save folder - and then a pull from the test console would restore
# over the actual save. This only ever REPORTS: what the rig tracks is the operator's call, and a
# script that quietly untracked things would be making save-data decisions on its own.
function Show-RealGameMappings {
    $cfg = Join-Path $winState 'config.json'
    if (-not (Test-Path $cfg)) { return }
    $games = (Get-Content $cfg -Raw | ConvertFrom-Json).Games
    foreach ($g in $games) {
        if ($g.SaveDirectory -and -not $g.SaveDirectory.StartsWith($StateRoot, 'OrdinalIgnoreCase')) {
            Warn "the test agent maps '$($g.Name)' to a real folder: $($g.SaveDirectory)"
            Warn "  a pull from the test console would restore over it."
        }
    }
}

function Build-Windows {
    Say "building the Windows agent as $Version"
    if (Get-TestTray) { Stop-Windows }
    # SaveLocker.Agent.csproj runs its OWN "npm run build" (BeforeTargets="Build", unconditionally,
    # no staleness check) to stage the WebView2 UI — it has no idea whether node_modules exists. A
    # fresh worktree has never had `npm install` run in agent-ui/, so without this the csproj's own
    # npm step dies with "'tsc' is not recognized" before a single line of C# compiles.
    $agentUiDir = Join-Path $root 'agent-ui'
    if (-not (Test-Path (Join-Path $agentUiDir 'node_modules'))) {
        Say 'agent-ui has no node_modules yet - installing first'
        Push-Location $agentUiDir
        try { & npm install --no-audit --no-fund | Out-Null } finally { Pop-Location }
    }
    # -p:Version does NOT work here: MinVer assigns Version/FileVersion/AssemblyVersion in a later
    # target and overwrites it. MinVerVersionOverride is its own escape hatch.
    $env:MinVerVersionOverride = $Version
    try {
        Invoke-Build (Join-Path $root 'src\Agent\SaveLocker.Agent.csproj') 1
    } finally {
        Remove-Item Env:\MinVerVersionOverride -ErrorAction SilentlyContinue
    }
    Write-Host "  stamped: $((Get-Item $agentDll).VersionInfo.ProductVersion)"
}

# The Windows agent's csproj builds this itself; the Linux one cannot (no native node in WSL), so
# the dist has to exist here before it can be staged across.
function Build-AgentUi {
    $dist = Join-Path $root 'agent-ui\dist\index.html'
    $src  = Join-Path $root 'agent-ui\src'
    $needed = -not (Test-Path $dist)
    if (-not $needed) {
        $newest = (Get-ChildItem $src -Recurse -File | Sort-Object LastWriteTime -Descending |
                   Select-Object -First 1).LastWriteTime
        if ($newest -gt (Get-Item $dist).LastWriteTime) { $needed = $true }
    }
    if (-not $needed) { Write-Host '  agent-ui/dist is current'; return }

    Say 'building agent-ui (needed before the Linux agent can serve it)'
    Push-Location (Join-Path $root 'agent-ui')
    try {
        if (-not (Test-Path 'node_modules')) { & npm install --no-audit --no-fund | Out-Null }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'agent-ui build failed' }
    } finally { Pop-Location }
}

function Build-Console {
    Say "building the dashboard image as $Version"
    $commit = (& git -C $root rev-parse --short HEAD)
    $built  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    & docker build -t $image -f (Join-Path $root 'src\Server\Dockerfile') `
        --build-arg "SAVELOCKER_VERSION=$Version" `
        --build-arg "SAVELOCKER_COMMIT=$commit" `
        --build-arg "SAVELOCKER_BUILT_AT=$built" $root
    # Check the exit code explicitly: a piped docker build reports the pipe's status, which has
    # masked a genuine "failed to fetch oauth token" as success before.
    if ($LASTEXITCODE -ne 0) { throw 'docker build failed' }
}

function Start-Console {
    Say "console on :$ConsolePort"
    & docker rm -f $container 2>$null | Out-Null
    & docker run -d --name $container -p "${ConsolePort}:8080" -v "${volume}:/data" $image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'docker run failed' }
    for ($i = 0; $i -lt 40; $i++) {
        $s = Test-ConsoleUp
        if ($s) { Write-Host "  $($s.build.version) @ $($s.build.commit)"; return }
        Start-Sleep -Milliseconds 800
    }
    Warn "console did not answer on :$ConsolePort"
}

function Start-Windows {
    if (-not (Test-Path $agentDll)) { throw "not built — run: .\tests\testenv.ps1 build" }
    Stop-Windows | Out-Null
    Use-TestEnvVars
    try {
        $cfg = Join-Path $winState 'config.json'
        $registered = (Test-Path $cfg) -and ((Get-Content $cfg -Raw) -match '"apiKey"')
        if (-not $registered) {
            Say "registering the Windows test agent against $serverUrl"
            & $dotnet $agentDll set-server --url $serverUrl | Out-Null
            & $dotnet $agentDll register --name 'WinTest' | Select-Object -First 1
        }
        Say "tray on :$WinPort"
        $p = Start-Process $dotnet -ArgumentList @($agentDll) -PassThru
        for ($i = 0; $i -lt 40; $i++) {
            $s = Get-WinAgentState
            if ($s) { Write-Host "  pid $($p.Id) — v$($s.buildLabel) — connected=$($s.connected)"; return }
            Start-Sleep -Milliseconds 700
        }
        Warn "the test tray did not answer on :$WinPort (log: $winState\agent.log)"
    } finally { Clear-TestEnvVars }
}

function Stop-Windows {
    $procs = @(Get-TestTray)
    if ($procs.Length -eq 0) { Write-Host 'no test tray running'; return }
    foreach ($p in $procs) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Host "stopped test tray (pid $($p.ProcessId))"
    }
}

function Stop-Console {
    & docker stop $container 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "stopped $container" } else { Write-Host 'no console container running' }
}

function Invoke-Suites {
    $results = [ordered]@{}

    if ($Suite -eq 'all' -or $Suite -eq 'agents') {
        Say 'agents — Linux suite (WSL)'
        Invoke-Wsl 'test'
        $results['linux agent'] = $LASTEXITCODE

        if ($SkipWinAgentSuite) {
            Warn 'skipping the Windows agent bug bounty (-SkipWinAgentSuite)'
        } else {
            Say 'agents — Windows bug bounty (~5 min, needs an interactive desktop)'
            & powershell -NoProfile -File (Join-Path $PSScriptRoot 'run-winagent-tests.ps1')
            $results['windows agent'] = $LASTEXITCODE
        }
    }

    if ($Suite -eq 'all' -or $Suite -eq 'dashboard') {
        Say 'dashboard — server bug bounty (owns its own port and DB)'
        & powershell -NoProfile -File (Join-Path $PSScriptRoot 'run-server-bugbounty-tests.ps1')
        $results['server'] = $LASTEXITCODE

        Say 'dashboard — agent-update scheduling (owns its own port and DB)'
        & powershell -NoProfile -File (Join-Path $PSScriptRoot 'run-schedule-tests.ps1')
        $results['scheduling'] = $LASTEXITCODE

        Say 'dashboard — console lint and build'
        Push-Location (Join-Path $root 'web')
        try {
            # A fresh worktree has no node_modules, and npm's failure there reads like a lint failure.
            if (-not (Test-Path 'node_modules')) { & npm install --no-audit --no-fund | Out-Null }
            & npm run lint
            $lint = $LASTEXITCODE
            & npm run build
            if ($LASTEXITCODE -ne 0 -or $lint -ne 0) { $results['console web'] = 1 } else { $results['console web'] = 0 }
        } finally { Pop-Location }

        Say 'dashboard — agent UI build (this is what type-checks the generated API types)'
        Push-Location (Join-Path $root 'agent-ui')
        try {
            if (-not (Test-Path 'node_modules')) { & npm install --no-audit --no-fund | Out-Null }
            & npm run build
            $results['agent ui'] = $LASTEXITCODE
        } finally { Pop-Location }
    }

    Write-Host ''
    Say 'summary'
    foreach ($k in $results.Keys) {
        if ($results[$k] -eq 0) { Write-Host "  PASS  $k" -ForegroundColor Green }
        else { Write-Host "  FAIL  $k (exit $($results[$k]))" -ForegroundColor Red }
    }
}

switch ($Command) {

    'build' {
        if ($Only -eq 'all' -or $Only -eq 'windows') { Build-Windows }
        if ($Only -eq 'all' -or $Only -eq 'linux')   { Build-AgentUi; Invoke-Wsl 'build' }
        if (Use-Deck) { Build-Deck }
        if ($Only -eq 'all' -or $Only -eq 'console') { Build-Console }
    }

    'up' {
        if ($Only -eq 'all' -or $Only -eq 'console') { Start-Console }
        if ($Only -eq 'all' -or $Only -eq 'windows') { Start-Windows }
        if ($Only -eq 'all' -or $Only -eq 'linux')   { Invoke-Wsl 'up' }
        if (Use-Deck) {
            try { Start-Deck } catch { Warn "deck: $($_.Exception.Message)" }
        }
        Show-RealGameMappings
        Write-Host ''
        Write-Host "console      $serverUrl"
        Write-Host "windows UI   http://localhost:$WinPort"
        Write-Host "linux UI     http://localhost:$LinuxPort   (WSL2 forwards it to Windows)"
        if ($DeckHost) { Write-Host "deck UI      $(Get-DeckUiHint)" }
    }

    'down' {
        Say 'stopping the test rig (the installed agent is not touched)'
        Stop-Windows
        Invoke-Wsl 'down'
        if (Test-DeckConfigured) {
            try { Stop-Deck } catch { Warn "deck: $($_.Exception.Message)" }
        }
        if (-not $AgentsOnly) { Stop-Console }
    }

    'status' {
        $c = Test-ConsoleUp
        if ($c) { Write-Host "console      v$($c.build.version) @ $($c.build.commit)  ($serverUrl)" }
        else    { Write-Host "console      down ($serverUrl)" }

        $w = Get-WinAgentState
        if ($w) { Write-Host "windows      v$($w.buildLabel)  connected=$($w.connected)  :$WinPort" }
        else    { Write-Host "windows      down (:$WinPort)" }

        Invoke-Wsl 'status'
        Show-RealGameMappings

        if (Test-DeckConfigured) {
            try {
                Invoke-Deck 'status'
                Write-Host "deck UI      $(Get-DeckUiHint)"
            } catch { Warn "deck: $($_.Exception.Message)" }
        } else {
            Write-Host 'deck         not configured (set -DeckHost or $env:SAVELOCKER_DECK_HOST)'
        }

        $release = Get-Process SaveLocker.Agent -ErrorAction SilentlyContinue
        if ($release) {
            $ver = (Get-Item $release[0].Path).VersionInfo.FileVersion
            Write-Host "installed    v$ver  pid $($release[0].Id)  (untouched by this script)"
        } else {
            Write-Host 'installed    not running'
        }
    }

    'test' { Invoke-Suites }

    'sync' {
        # From git status, never a hand-written list: a hardcoded one went stale mid-session once and
        # the clone silently built an older tree than the one under test.
        $changed = & git -C $root status --porcelain |
            ForEach-Object { $_.Substring(3).Trim('"') } |
            Where-Object { $_ -and (Test-Path (Join-Path $root $_)) }
        if (-not $changed) { Write-Host 'nothing changed to sync'; break }
        $list = Join-Path $env:TEMP 'savelocker-testenv-sync.txt'
        Set-Content -Path $list -Value $changed -Encoding ASCII
        Say "syncing $($changed.Count) changed file(s) into the WSL clone"
        Invoke-Wsl 'sync' $list
        Remove-Item $list -ErrorAction SilentlyContinue
    }

    'logs' {
        $winLog = Join-Path $winState 'agent.log'
        if (Test-Path $winLog) { Say "windows test agent — $winLog"; Get-Content $winLog -Tail 20 }
        # The bash fallback text must be SINGLE-quoted, not double: PowerShell 5.1 mis-serialises an
        # embedded " inside a single-quoted PS string when building a native command line for
        # wsl.exe, and the far side receives "...echo "(none)"" with the quoting broken - a plain
        # bash syntax error, reproduced and confirmed fixed 2026-08-19.
        Say 'wsl daemon'
        & wsl -d $Distro -- bash -c 'tail -20 ~/.savelocker-testenv-daemon.log 2>/dev/null || echo ''(none)'''
        Say 'wsl suite'
        & wsl -d $Distro -- bash -c 'tail -20 ~/.savelocker-testenv-suite.log 2>/dev/null || echo ''(none)'''
        if (Test-DeckConfigured) {
            Say 'deck daemon'
            try { Invoke-Deck 'logs' } catch { Warn "deck: $($_.Exception.Message)" }
        }
    }

    'clean' {
        Say "deleting the test rig's builds and state (the installed agent and any real save data are never touched)"

        Stop-Windows
        if (Test-Path $StateRoot) {
            Remove-Item $StateRoot -Recurse -Force
            Write-Host "  removed $StateRoot"
        }

        Invoke-Wsl 'clean'

        if (Test-DeckConfigured) {
            try { Invoke-Deck 'clean' } catch { Warn "deck: $($_.Exception.Message)" }
        }

        if (-not $AgentsOnly) {
            Say 'removing the dashboard container, image and data volume'
            & docker rm -f $container 2>$null | Out-Null
            & docker volume rm $volume 2>$null | Out-Null
            & docker rmi $image 2>$null | Out-Null
            Write-Host "  removed $container / $volume / $image"
        }
    }

    'deck-config' {
        # $DeckHost/$DeckServerUrl already reflect explicit flag > saved file > env var by this
        # point (the loader above), so writing them straight back merges a one-sided edit — e.g.
        # `deck-config -DeckHost deck@newip` alone keeps whatever SAVELOCKER_DECK_SERVER_URL was
        # already saved instead of blanking it.
        if ($PSBoundParameters.ContainsKey('DeckHost') -or $PSBoundParameters.ContainsKey('DeckServerUrl')) {
            if (-not $DeckHost) { throw 'no -DeckHost given and none saved yet' }
            $lines = @(
                "# Written by: testenv.ps1 deck-config — edit by hand or re-run the command."
                "`$env:SAVELOCKER_DECK_HOST = '$DeckHost'"
            )
            if ($DeckServerUrl) { $lines += "`$env:SAVELOCKER_DECK_SERVER_URL = '$DeckServerUrl'" }
            Set-Content -Path $deckConfig -Value $lines -Encoding UTF8
            Say "saved to $deckConfig"
        }

        Write-Host "deck host:        $(if ($DeckHost) { $DeckHost } else { '(not set)' })"
        Write-Host "deck server url:  $(if ($DeckServerUrl) { $DeckServerUrl } else { '(not set)' })"
    }
}
