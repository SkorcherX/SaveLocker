# TOFU server pinning over real TLS (Linux agent Phase 4) - 6 checks. Windows + Linux.
#
# The plain-http harness in run-enrollment-tests.ps1 CANNOT test pinning: http has no server
# identity to pin, so it can only assert that the agent records nothing. This script starts the
# server over HTTPS so the pin-and-warn path runs for real.
#
# It starts its OWN server on :5443 with its OWN state dir, so it never fights the dev server on
# :5179 over the SQLite file.
#
# Prerequisite: the ASP.NET dev certificate must be trusted, or the TLS handshake fails and every
# check below fails with it:   dotnet dev-certs https --trust
#
# Usage:  .\tests\run-enrollment-tls-tests.ps1   /   pwsh tests/run-enrollment-tls-tests.ps1

$ErrorActionPreference = "Continue"

$onWindows = if ($null -eq $IsWindows) { $true } else { $IsWindows }

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify"
$server  = "https://localhost:5443"

if ($onWindows) {
    $inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    $dotnet = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
    $dll    = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
} else {
    $dotnet = "dotnet"
    $dll    = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
}

if (-not (Test-Path $dll)) { Write-Host "Agent not built: $dll"; exit 2 }

$stamp   = Get-Date -Format "HHmmss"
$state   = Join-Path $scratch "tls-state-$stamp"
New-Item -ItemType Directory -Force $state | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $dll @args 2>&1 }

# ---- Start an HTTPS server on its own state ----
$serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
if (-not (Test-Path $serverDll)) { Write-Host "Server not built: $serverDll"; exit 2 }

$env:ASPNETCORE_URLS         = $server
$env:Storage__DbPath         = Join-Path $state "savelocker.db"
$env:Storage__ArchiveRoot    = Join-Path $state "archives"
$env:Backup__Enabled         = "false"

$proc = Start-Process -FilePath $dotnet -ArgumentList $serverDll -PassThru -WindowStyle Hidden

try {
    $up = $false
    foreach ($i in 1..30) {
        Start-Sleep -Milliseconds 700
        try {
            Invoke-RestMethod -Uri "$server/api/admin/status" -TimeoutSec 3 | Out-Null
            $up = $true; break
        } catch { }
    }
    if (-not $up) {
        Write-Host "FAIL: HTTPS server did not come up on $server."
        Write-Host "      If the handshake was rejected, trust the dev cert: dotnet dev-certs https --trust"
        exit 1
    }

    # A game must exist before the mint, so the enrolled agent has one to ask the server about.
    # Without it `status` iterates an empty list, makes no request, completes no TLS handshake --
    # and the pin check below would pass vacuously by never running.
    Invoke-RestMethod -Uri "$server/api/games" -Method Post -ContentType "application/json" `
        -Body (@{ name = "TlsGame-$stamp"; manifestKey = $null; customPathsJson = $null } | ConvertTo-Json) | Out-Null

    # ---- 1. Enroll over HTTPS records the pin ----
    $mint = Invoke-RestMethod -Uri "$server/api/admin/enrollments" -Method Post `
        -ContentType "application/json" `
        -Body (@{ machineName = "tls-deck-$stamp"; ttlMinutes = 15 } | ConvertTo-Json)

    $policyFile = Join-Path $state "policy.json"
    $mint.policy | ConvertTo-Json -Depth 6 | Set-Content -Path $policyFile -Encoding utf8

    $cfg = Join-Path $state "agent-config.json"
    $out = Agent enroll --file $policyFile --config $cfg
    $saved = Get-Content $cfg -Raw | ConvertFrom-Json

    # SHA-256 of the SPKI, base64 -> always 44 chars ending in '='.
    Check "enroll over https succeeds"        ($LASTEXITCODE -eq 0)
    Check "enroll recorded a TLS pin"         ($saved.ServerPin -and $saved.ServerPin.Length -eq 44)
    Check "enroll reported the pin"           ("$out" -match "Pinned the server's TLS key")

    $realPin = $saved.ServerPin

    # ---- 2. `trust` shows the pin it recorded ----
    $trustOut = Agent trust --config $cfg
    Check "trust prints the pinned key"       ("$trustOut" -match [regex]::Escape($realPin))

    # ---- 3. A CHANGED server identity warns. This is the whole point of the pin. ----
    # Simulate the server's key changing by pinning a different one, which is indistinguishable to
    # the agent from the certificate actually having been swapped underneath it.
    $saved.ServerPin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
    $saved | ConvertTo-Json -Depth 6 | Set-Content -Path $cfg -Encoding utf8

    $warnOut = Agent status --config $cfg
    Check "a changed server key warns"        ("$warnOut" -match "TLS identity has CHANGED")

    # ---- 4. `trust --accept` re-pins after a legitimate renewal ----
    Agent trust --accept --config $cfg | Out-Null
    $repinned = Get-Content $cfg -Raw | ConvertFrom-Json
    Check "trust --accept re-pins the server"  ($repinned.ServerPin -eq $realPin)
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Remove-Item Env:ASPNETCORE_URLS, Env:Storage__DbPath, Env:Storage__ArchiveRoot, Env:Backup__Enabled -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Enrollment TLS: $pass passed, $fail failed."
if ($fail -gt 0) { exit 1 }
