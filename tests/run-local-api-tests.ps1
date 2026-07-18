# Local agent API security - 14 checks. Runs on BOTH Windows and Linux.
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
#   6. --lan is gone                              - it bound this API to every interface.
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
}
finally {
    if ($daemonProc) { Stop-Process -Id $daemonProc.Id -Force -ErrorAction SilentlyContinue }
    $http.Dispose()
}

# =================================================================================
# 6. --lan IS GONE (checked after the daemon is down; it must refuse to start at all)
# =================================================================================
$lanOut = & $dotnet $dll daemon --lan --port $port --config $cfgPath 2>&1
Check "daemon --lan is refused" ($LASTEXITCODE -ne 0 -and "$lanOut" -match "removed")

Write-Host ""
Write-Host "$pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
exit 0
