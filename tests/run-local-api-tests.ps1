# Local agent API - 30 checks (up to 31 when a candidate is scanned). Runs on BOTH Windows and Linux.
#
# The agent's own API (AgentApiServer, shared by the Windows tray and the Linux daemon) manages this
# machine: it rewrites config, enrolls games and re-registers against the server. It used to be
# unauthenticated, allow every CORS origin, and hand out the machine's server API key to anyone who
# asked. These are SECURITY tests: each proves the ATTACK FAILS, not that the UI still works.
#
#   1. Unauthenticated callers are refused        - no token / wrong token => 401.
#   2. The machine API key is never served        - it must not appear in ANY response body.
#   3. DNS rebinding is refused                   - a foreign Host header => 403, even on loopback.
#   4. Cross-origin pages are refused             - a foreign Origin header => 403.
#   5. The token reaches the UI, and only the UI  - injected into index.html, 0600 on disk.
#   6. The path browser is rooted                 - outside $HOME/Steam => 400, incl. via .. and a symlink.
#   7. --lan is gone                              - it bound this API to every interface.
#
# Plus one correctness check that is not about attack surface:
#
#   10. A server change moves ALL traffic          - the cached SyncEngine used to keep pushing to
#                                                    the old host while the poller moved (LA-01).
#
# The daemon runs on its own port so this suite never collides with a real agent on :5178.
# Usage: .\tests\run-local-api-tests.ps1 / pwsh tests/run-local-api-tests.ps1

$ErrorActionPreference = "Continue"

$onWindows = if ($null -eq $IsWindows) { $true } else { $IsWindows }

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify-localapi"
$port    = 5188
$base    = "http://localhost:$port"

# `daemon` lives only in the Linux host project, but the API it serves is Agent.Core's — the same
# object the Windows tray hosts. Driving it here exercises the shared code on whichever OS we are on.
if ($onWindows) {
    $inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    $dotnet = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
} else {
    $dotnet = "dotnet"
}
$dll = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
if (-not (Test-Path $dll)) { Write-Host "Agent not built: $dll"; exit 2 }

Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $scratch | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}

# HttpClient, not Invoke-WebRequest: these tests turn on exact control of the Host and Origin
# headers, and on reading a 401/403 without it being thrown as a terminating error.
Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient

# Returns a hashtable with Status and Body. Never throws on a non-2xx.
function Send($path, $token, $hostHeader, $origin) {
    $req = New-Object System.Net.Http.HttpRequestMessage("GET", "$base$path")
    if ($token)      { $req.Headers.Add("X-SaveLocker-Token", $token) }
    if ($hostHeader) { $req.Headers.Host = $hostHeader }
    if ($origin)     { $req.Headers.Add("Origin", $origin) }
    try {
        $res = $http.SendAsync($req).GetAwaiter().GetResult()
        return @{ Status = [int]$res.StatusCode; Body = $res.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
    } catch {
        return @{ Status = 0; Body = "" }
    }
}

# POST with a JSON body. Same never-throws contract as Send.
function SendPost($path, $token, $json) {
    $req = New-Object System.Net.Http.HttpRequestMessage("POST", "$base$path")
    if ($token) { $req.Headers.Add("X-SaveLocker-Token", $token) }
    if ($null -ne $json) {
        $req.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
    }
    try {
        $res = $http.SendAsync($req).GetAwaiter().GetResult()
        return @{ Status = [int]$res.StatusCode; Body = $res.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
    } catch {
        return @{ Status = 0; Body = "" }
    }
}

# ---- A config carrying a machine key we can hunt for in every response ----
$cfgPath   = Join-Path $scratch "cfg.json"
$secretKey = "SECRET-MACHINE-KEY-DO-NOT-LEAK-12345"
@{
    ServerUrl   = "http://localhost:5179"
    MachineName = "LocalApiTest"
    ApiKey      = $secretKey
    Games       = @()
} | ConvertTo-Json | Set-Content -Path $cfgPath -Encoding utf8

$daemonArgs = @($dll, "daemon", "--port", "$port", "--config", $cfgPath)
$daemonProc = if ($onWindows) {
    Start-Process -FilePath $dotnet -ArgumentList $daemonArgs -PassThru -WindowStyle Hidden
} else {
    Start-Process -FilePath $dotnet -ArgumentList $daemonArgs -PassThru
}

foreach ($i in 1..40) {
    Start-Sleep -Milliseconds 700
    $probe = Send "/" $null $null $null
    if ($probe.Status -ne 0) { break }
}

try {
    # The token the agent minted, read the way only a local process with the right permissions can.
    $tokenPath = Join-Path $scratch "api-token"
    $token = if (Test-Path $tokenPath) { (Get-Content $tokenPath -Raw).Trim() } else { "" }

    # =================================================================================
    # 1. UNAUTHENTICATED CALLERS ARE REFUSED
    # =================================================================================
    $noToken = Send "/api/state" $null $null $null
    Check "no token is refused (401)"        ($noToken.Status -eq 401)

    $badToken = Send "/api/state" "not-the-real-token" $null $null
    Check "a wrong token is refused (401)"   ($badToken.Status -eq 401)

    $good = Send "/api/state" $token $null $null
    Check "the real token is accepted (200)" ($good.Status -eq 200)

    # =================================================================================
    # 2. THE MACHINE API KEY IS NEVER SERVED
    # =================================================================================
    Check "/api/state does not leak the machine key"  (-not $good.Body.Contains($secretKey))
    Check "/api/state has no apiKey field"            (-not ($good.Body -match '"apiKey"'))

    $cfg = Send "/api/config" $token $null $null
    Check "/api/config does not leak the machine key" (-not $cfg.Body.Contains($secretKey))
    Check "/api/config has no apiKey field"           (-not ($cfg.Body -match '"apiKey"'))

    # =================================================================================
    # 3. DNS REBINDING IS REFUSED
    # A rebinding page resolves its OWN name to 127.0.0.1: the socket is loopback, but the Host
    # header still carries the attacker's domain. A correct token must not rescue it.
    # =================================================================================
    $rebind = Send "/api/state" $token "evil.example.com" $null
    Check "a foreign Host is refused (403)"           ($rebind.Status -eq 403)

    $rebindUi = Send "/" $null "evil.example.com" $null
    Check "a foreign Host cannot fetch the UI either" ($rebindUi.Status -eq 403)

    # =================================================================================
    # 4. CROSS-ORIGIN PAGES ARE REFUSED
    # =================================================================================
    $crossOrigin = Send "/api/state" $token $null "http://evil.example.com"
    Check "a foreign Origin is refused (403)"         ($crossOrigin.Status -eq 403)

    $sameOrigin = Send "/api/state" $token $null $base
    Check "the agent's own Origin is accepted"        ($sameOrigin.Status -eq 200)

    # =================================================================================
    # 5. THE TOKEN REACHES THE UI, AND ONLY THE UI
    # =================================================================================
    $ui = Send "/" $null $null $null
    Check "the UI is served with the token injected"  ($ui.Status -eq 200 -and $ui.Body.Contains($token) -and $token.Length -ge 32)
    Check "the placeholder is never served raw"       (-not $ui.Body.Contains("__SAVELOCKER_TOKEN__"))

    if ($onWindows) {
        # No POSIX modes on Windows; ACL inheritance from %PROGRAMDATA% governs instead.
        Check "token file exists"                     (Test-Path $tokenPath)
    } else {
        $mode = (Get-Item $tokenPath).UnixMode
        Check "token file is 0600"                    ($mode -eq "-rw-------")
    }

    # =================================================================================
    # 6. THE PATH BROWSER IS ROOTED
    # /api/browse exists because a Deck has no folder dialog. It must not become a way to
    # enumerate the whole disk: anything outside $HOME + the Steam roots is refused, and the
    # refusal must survive traversal and a symlink pointing out of the tree.
    # =================================================================================
    $noToken = Send "/api/browse" $null $null $null
    Check "/api/browse needs the token"               ($noToken.Status -eq 401)

    # The save-folder routes (multiple save paths Group C) add, remove and map folders on this machine:
    # the same guard, and an unknown game is a 404 rather than a folder made for nothing.
    Check "/api/folder-suggestions needs the token"   ((Send "/api/folder-suggestions" $null $null $null).Status -eq 401)
    $sugg = Send "/api/folder-suggestions" $token $null $null
    Check "/api/folder-suggestions lists nothing for no games" ($sugg.Status -eq 200 -and $sugg.Body.Trim() -eq "[]")
    $fakeGame = [guid]::NewGuid()
    Check "adding a folder needs the token"           ((SendPost "/api/games/$fakeGame/paths" $null '{"path":"x"}').Status -eq 401)
    Check "adding a folder to an unknown game is 404" ((SendPost "/api/games/$fakeGame/paths" $token '{"path":"x"}').Status -eq 404)
    Check "answering a suggestion needs the token"    ((SendPost "/api/folder-suggestions/answer" $null "{`"gameId`":`"$fakeGame`"}").Status -eq 401)
    $delReq = New-Object System.Net.Http.HttpRequestMessage("DELETE", "$base/api/games/$fakeGame/paths/states")
    $delStatus = try { [int]$http.SendAsync($delReq).GetAwaiter().GetResult().StatusCode } catch { 0 }
    Check "removing a folder needs the token"         ($delStatus -eq 401)

    $roots = Send "/api/browse" $token $null $null
    $homeDir = if ($onWindows) { $env:USERPROFILE } else { $env:HOME }
    Check "/api/browse lists the roots"               ($roots.Status -eq 200 -and $roots.Body -match '"entries"')
    Check "the home directory is a root"              ($roots.Body.Contains(($homeDir -replace '\\','\\')))

    $inside = Send "/api/browse?path=$([uri]::EscapeDataString($homeDir))" $token $null $null
    Check "a path inside a root is listed"            ($inside.Status -eq 200)

    $outsideDir = if ($onWindows) { "C:\Windows\System32" } else { "/etc" }
    $outside = Send "/api/browse?path=$([uri]::EscapeDataString($outsideDir))" $token $null $null
    Check "a path outside every root is refused"      ($outside.Status -eq 400)

    # The containment check runs on the CANONICAL path, so climbing out with .. must not work.
    $climb = Join-Path $homeDir (".." + [IO.Path]::DirectorySeparatorChar + ".." + [IO.Path]::DirectorySeparatorChar + $(if ($onWindows) { "Windows" } else { "etc" }))
    $traversal = Send "/api/browse?path=$([uri]::EscapeDataString($climb))" $token $null $null
    Check "traversal out of a root is refused"        ($traversal.Status -eq 400)

    # A symlink INSIDE a root pointing outside it is the interesting case: the path looks
    # contained until it is resolved. It must therefore live under $HOME — putting it in the repo
    # scratch dir would make the test pass for the wrong reason (that path is outside the roots
    # already). Windows junctions need no elevation, so both OSes can run this.
    $linkPath = Join-Path $homeDir ".savelocker-verify-escape-link"
    Remove-Item $linkPath -Recurse -Force -ErrorAction SilentlyContinue
    $linkMade = $false
    try {
        New-Item -ItemType SymbolicLink -Path $linkPath -Target $outsideDir -ErrorAction Stop | Out-Null
        $linkMade = $true
    } catch {
        try { New-Item -ItemType Junction -Path $linkPath -Target $outsideDir -ErrorAction Stop | Out-Null; $linkMade = $true } catch { }
    }
    if ($linkMade) {
        $viaLink = Send "/api/browse?path=$([uri]::EscapeDataString($linkPath))" $token $null $null
        Check "a symlink out of the roots is refused" ($viaLink.Status -eq 400)
        Remove-Item $linkPath -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "SKIP: a symlink out of the roots is refused (could not create a link)"
    }

    # =================================================================================
    # 6b. A SYMLINK INSIDE THE ROOTS IS NOW LISTED AND TRAVERSABLE
    # Phase 1.2 loosened the display rule from "hide every link" to "hide only a link whose
    # target escapes the roots" — a Wine prefix reaches its save folders through in-prefix links.
    # Containment is unchanged (6 above still holds); this proves the loosening actually happened.
    # =================================================================================
    $insideDir  = Join-Path $homeDir ".savelocker-verify-inside"
    $insideReal = Join-Path $insideDir "real"
    $insideLink = Join-Path $insideDir "link"
    Remove-Item $insideDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $insideReal | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $insideReal "child") | Out-Null
    $insideLinkMade = $false
    try {
        New-Item -ItemType SymbolicLink -Path $insideLink -Target $insideReal -ErrorAction Stop | Out-Null
        $insideLinkMade = $true
    } catch {
        try { New-Item -ItemType Junction -Path $insideLink -Target $insideReal -ErrorAction Stop | Out-Null; $insideLinkMade = $true } catch { }
    }
    if ($insideLinkMade) {
        $listing = Send "/api/browse?path=$([uri]::EscapeDataString($insideDir))" $token $null $null
        Check "a link inside the roots is listed"     ($listing.Status -eq 200 -and $listing.Body -match '"link"')
        $intoLink = Send "/api/browse?path=$([uri]::EscapeDataString($insideLink))" $token $null $null
        Check "a link inside the roots is traversable" ($intoLink.Status -eq 200 -and $intoLink.Body -match '"child"')
    } else {
        Write-Host "SKIP: a link inside the roots is listed (could not create a link)"
    }
    Remove-Item $insideDir -Recurse -Force -ErrorAction SilentlyContinue

    # =================================================================================
    # 8. THE CANDIDATE SAVE-FOLDER SETTER
    # POST /api/candidates/{id}/folder is what Add Games writes a browsed path with (the candidate
    # cache is not the tracked-games list). An out-of-range id must be rejected, not silently
    # mutate anything; a valid id must actually stick. The positive leg needs a scanned candidate,
    # absent on a bare CI box, so it skips cleanly when the scan finds nothing.
    # =================================================================================
    $badId = SendPost "/api/candidates/999999/folder" $token '{"path":"/tmp"}'
    Check "an out-of-range candidate folder set is refused (400)" ($badId.Status -eq 400)

    $cands = Send "/api/candidates" $token $null $null
    if ($cands.Status -eq 200 -and $cands.Body -match '"id"\s*:\s*0\b') {
        $wanted = if ($onWindows) { $env:USERPROFILE } else { $env:HOME }
        $setOne = SendPost "/api/candidates/0/folder" $token (@{ path = $wanted } | ConvertTo-Json -Compress)
        Check "a valid candidate folder set is accepted (200)" ($setOne.Status -eq 200)
        $after = Send "/api/candidates" $token $null $null
        Check "the candidate folder set sticks in the cache" ($after.Body.Contains(($wanted -replace '\\','\\')))
    } else {
        Write-Host "SKIP: a valid candidate folder set is accepted (no candidates scanned on this box)"
    }

    # =================================================================================
    # 9. THE STEAM LAUNCH COMMAND
    # /api/launch-command puts install.sh's one-time banner in the UI. It is token-gated like
    # everything else; on Linux it resolves the real installed path, and on Windows it returns a
    # null command so the UI hides the card (the tray sets up sync through the installer instead).
    # =================================================================================
    $lcNoToken = Send "/api/launch-command" $null $null $null
    Check "/api/launch-command needs the token"       ($lcNoToken.Status -eq 401)

    # This harness always drives the Agent.Linux daemon (see header), so the endpoint is wired with
    # the Linux resolver on either OS and always returns a command. The Windows tray, which passes no
    # resolver and returns a null command, is not exercised here — its default is covered by types.
    $lc = Send "/api/launch-command" $token $null $null
    Check "launch-command resolves the run wrapper"   ($lc.Status -eq 200 -and $lc.Body -match 'run -- %command%')
}
finally {
    if ($daemonProc) { Stop-Process -Id $daemonProc.Id -Force -ErrorAction SilentlyContinue }
    $http.Dispose()
}

# =================================================================================
# 7. --lan IS GONE (checked after the daemon is down; it must refuse to start at all)
# =================================================================================
$lanOut = & $dotnet $dll daemon --lan --port $port --config $cfgPath 2>&1
Check "daemon --lan is refused" ($LASTEXITCODE -ne 0 -and "$lanOut" -match "removed")

# =================================================================================
# 10. CHANGING THE SERVER URL MOVES *ALL* DAEMON TRAFFIC (LA-01)
# The daemon caches an ApiClient (base URL + key + pin) inside its SyncEngine, built once at
# startup. CommandPoller rebuilds its client every tick, so a server change moved the POLLER
# immediately — while the folder-watcher pushes and the offline-queue drainer, which go through
# that cached engine, kept talking to the OLD server. Split-brain: save traffic to one host,
# control traffic to another, with no error anywhere.
#
# Two throwaway listeners stand in for the servers. They answer 404 to everything, which is all
# this needs: what is asserted is WHICH host was contacted, not that a push succeeded. The
# queued entry keeps the drainer retrying, so the engine's target is exercised repeatedly.
#
# Since WA-04 the traffic B receives is UNAUTHENTICATED: an origin change clears the machine key,
# id and TLS pin, because all three were issued by A and sending them to B would hand a live
# credential to a host that was never meant to see it. So the poller stays quiet until the machine
# re-registers and it is the drainer that reaches B here. That A's key never appears in a request
# to B is asserted in run-winagent-tests.ps1, which owns the WA-04 coverage.
# =================================================================================
$portA = 5197; $portB = 5198; $swPort = 5187
$urlA = "http://localhost:$portA"; $urlB = "http://localhost:$portB"

$swDir  = Join-Path $scratch "switch"
$swSave = Join-Path $swDir "save"
New-Item -ItemType Directory -Force $swSave | Out-Null
"progress" | Set-Content (Join-Path $swSave "slot1.sav") -Encoding utf8

$swGameId = [guid]::NewGuid().ToString()
$swCfg    = Join-Path $swDir "cfg.json"
@{
    ServerUrl   = $urlA
    MachineName = "SwitchTest"
    ApiKey      = "switch-test-key"
    MachineId   = [guid]::NewGuid().ToString()
    Games       = @(@{ GameId = $swGameId; Name = "SwitchGame"; SaveDirectory = $swSave })
} | ConvertTo-Json -Depth 5 | Set-Content -Path $swCfg -Encoding utf8

# Seed the offline queue so the drainer has work the moment it ticks (every 30 s). Written as
# literal JSON: PowerShell 5.1's ConvertTo-Json has no -AsArray and unwraps a single element.
$queuedAt = (Get-Date).ToUniversalTime().ToString("o")
"[{""GameId"":""$swGameId"",""GameName"":""SwitchGame"",""Force"":true,""QueuedAt"":""$queuedAt""}]" |
    Set-Content -Path (Join-Path $swDir "offline-queue.json") -Encoding utf8

$listenerA = [System.Net.HttpListener]::new(); $listenerA.Prefixes.Add("$urlA/")
$listenerB = [System.Net.HttpListener]::new(); $listenerB.Prefixes.Add("$urlB/")
$listenerA.Start(); $listenerB.Start()

# Accept and answer on both listeners for $seconds, returning how many requests each took.
function Serve($seconds) {
    $a = 0; $b = 0
    $deadline = (Get-Date).AddSeconds($seconds)
    $taskA = $listenerA.GetContextAsync(); $taskB = $listenerB.GetContextAsync()
    while ((Get-Date) -lt $deadline) {
        if ($taskA.Wait(100)) {
            $c = $taskA.Result; $c.Response.StatusCode = 404; $c.Response.Close(); $a++
            $taskA = $listenerA.GetContextAsync()
        }
        if ($taskB.Wait(100)) {
            $c = $taskB.Result; $c.Response.StatusCode = 404; $c.Response.Close(); $b++
            $taskB = $listenerB.GetContextAsync()
        }
    }
    return @{ A = $a; B = $b }
}

$swArgs = @($dll, "daemon", "--port", "$swPort", "--config", $swCfg)
$swProc = if ($onWindows) {
    Start-Process -FilePath $dotnet -ArgumentList $swArgs -PassThru -WindowStyle Hidden
} else {
    Start-Process -FilePath $dotnet -ArgumentList $swArgs -PassThru
}

try {
    $swBase = "http://localhost:$swPort"
    foreach ($i in 1..40) {
        Start-Sleep -Milliseconds 700
        try { Invoke-WebRequest "$swBase/" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { }
    }
    $swToken = (Get-Content (Join-Path $swDir "api-token") -Raw).Trim()
    $swHeaders = @{ "X-SaveLocker-Token" = $swToken }

    $state = Invoke-RestMethod "$swBase/api/state" -Headers $swHeaders
    Check "the daemon started on server A" ($state.serverUrl -eq $urlA)

    # Switch. The rebuild must land before this call returns.
    Invoke-RestMethod "$swBase/api/config" -Method Post -Headers $swHeaders `
        -Body (@{ serverUrl = $urlB } | ConvertTo-Json) -ContentType "application/json" | Out-Null

    # 75 s covers at least two poller ticks (20 s) and two drainer ticks (30 s).
    $hits = Serve 75
    Check "traffic reached the new server B"          ($hits.B -ge 1)
    Check "NO traffic reached the old server A"       ($hits.A -eq 0)
}
finally {
    if ($swProc) { Stop-Process -Id $swProc.Id -Force -ErrorAction SilentlyContinue }
    $listenerA.Stop(); $listenerA.Close()
    $listenerB.Stop(); $listenerB.Close()
}

Write-Host ""
Write-Host "$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
exit 0
