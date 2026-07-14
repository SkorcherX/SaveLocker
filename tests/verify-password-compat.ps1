# Does an admin password hashed by the OLD code still verify under the NEW code?
#
# Tokens.HashPassword/VerifyPassword moved off the obsolete `new Rfc2898DeriveBytes(...)`
# constructor (SYSLIB0060) onto the static `Rfc2898DeriveBytes.Pbkdf2(...)`. Same algorithm, same
# inputs, same bytes — so the stored "v1:salt:hash" strings must still authenticate.
#
# That claim is worth a test rather than a comment: if it is wrong, the user is locked out of their
# own dashboard, and the failure only appears in production, on the one machine that already has a
# password set. There is no unit-test project in this repo, so this proves the property the way it
# actually matters — end to end, through the real server:
#
#   1. an OLD server (a git worktree at <BaselineRef>) sets an admin password  -> hash written to DB
#   2. the NEW server opens that same DB and must:
#        - ACCEPT the correct password  (the old hash verifies under the new code)
#        - REJECT a wrong one           (it is not just blindly accepting everything)
#        - still be able to CHANGE the password, and accept the new one
#
# Usage:  .\tests\verify-password-compat.ps1  [-BaselineRef origin/main]
param(
    [string]$BaselineRef = "origin/main",
    [int]$Port = 5179
)

$ErrorActionPreference = "Stop"

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root "crossos-work\pwcompat"
$oldWt   = Join-Path $scratch "baseline"
$url     = "http://localhost:$Port"

$password    = "correct horse battery staple"
$wrongPass   = "Tr0ub4dor&3"
$newPassword = "a different password entirely"

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ } else { Write-Host "FAIL: $name"; $script:fail++ }
}

function Start-Server($dll, $db, $tag) {
    $env:ASPNETCORE_URLS      = $url
    $env:Storage__DbPath      = $db
    $env:Storage__ArchiveRoot = Join-Path $scratch "archives"
    $p = Start-Process -FilePath "dotnet" -ArgumentList @($dll) -PassThru -NoNewWindow `
        -RedirectStandardOutput "$scratch\$tag.out.log" -RedirectStandardError "$scratch\$tag.err.log"
    foreach ($i in 1..60) {
        try { Invoke-RestMethod "$url/api/admin/status" -TimeoutSec 2 | Out-Null; return $p }
        catch { Start-Sleep -Seconds 1 }
    }
    Get-Content "$scratch\$tag.err.log" -ErrorAction SilentlyContinue | Select-Object -Last 20
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    throw "server ($tag) did not start"
}

# /api/machines is admin-gated. Returns 401 when the password is wrong, 200 when right.
function Test-AdminPassword($pw) {
    try {
        Invoke-RestMethod "$url/api/machines" -TimeoutSec 15 -Headers @{ "X-Admin-Password" = $pw } | Out-Null
        return $true
    } catch { return $false }
}
# Read Settings['Admin:PasswordHash'] straight out of the SQLite file. Done via python3 in WSL
# because there is no sqlite3 CLI on the Windows box — written to a temp file rather than passed
# with -c, since the quoting survives neither PowerShell nor wsl.exe intact.
function ConvertTo-WslPath($p) {
    # E:\a\b  ->  /mnt/e/a/b   (drive letter lowercased, backslashes flipped)
    $drive = $p.Substring(0, 1).ToLower()
    return "/mnt/$drive" + ($p.Substring(2) -replace '\\', '/')
}

function Read-StoredPasswordHash($dbPath) {
    $py = Join-Path $scratch "read-hash.py"
    @'
import sqlite3, sys
con = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
row = con.execute("SELECT Value FROM Settings WHERE Key = 'Admin:PasswordHash'").fetchone()
print(row[0] if row else "(none)")
'@ | Set-Content -Path $py -Encoding utf8

    return (& wsl -d Ubuntu-24.04 -- python3 (ConvertTo-WslPath $py) (ConvertTo-WslPath $dbPath)).Trim()
}

function Set-AdminPassword($pw, $currentPw) {
    $headers = @{}
    if ($currentPw) { $headers["X-Admin-Password"] = $currentPw }
    Invoke-RestMethod "$url/api/admin/password" -Method Post -TimeoutSec 15 -Headers $headers `
        -ContentType "application/json" -Body (@{ password = $pw } | ConvertTo-Json) | Out-Null
}

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $scratch "archives") | Out-Null
$db = Join-Path $scratch "savelocker.db"
git worktree prune

# ---- 1. the OLD code writes the password hash ----
git worktree add --quiet --detach $oldWt $BaselineRef
try {
    Push-Location (Join-Path $oldWt "agent-ui"); npm ci --silent 2>&1 | Out-Null; Pop-Location
    dotnet build (Join-Path $oldWt "src\Server\SaveLocker.Server.csproj") --no-incremental -v quiet --nologo | Out-Null

    $srv = Start-Server (Join-Path $oldWt "src\Server\bin\Debug\net10.0\SaveLocker.Server.dll") $db "old"
    try {
        Set-AdminPassword $password $null
        Check "baseline ($BaselineRef) accepts the password it just hashed" (Test-AdminPassword $password)
    }
    finally { Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }

    # Read the stored hash straight out of SQLite, to show it really is the on-disk "v1:" format the
    # old constructor wrote — not something the new code quietly re-hashed on startup.
    $stored = Read-StoredPasswordHash $db
    Check "the baseline actually stored a v1: hash" ($stored -like "v1:*")
    Write-Host "     stored by the OLD constructor: $($stored.Substring(0, [Math]::Min(28, $stored.Length)))..."

    # ---- 2. the NEW code must verify that same stored hash ----
    $srv = Start-Server (Join-Path $root "src\Server\bin\Debug\net10.0\SaveLocker.Server.dll") $db "new"
    try {
        Check "NEW code ACCEPTS the password hashed by the OLD constructor" (Test-AdminPassword $password)
        Check "NEW code REJECTS a wrong password (not blindly accepting)"   (-not (Test-AdminPassword $wrongPass))

        # And the new code must still be able to rotate the password.
        Set-AdminPassword $newPassword $password
        Check "NEW code can CHANGE the password"            (Test-AdminPassword $newPassword)
        Check "the OLD password no longer works after that" (-not (Test-AdminPassword $password))
    }
    finally { Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
}
finally {
    git worktree remove --force $oldWt 2>&1 | Out-Null
    git worktree prune
}

Write-Host ""
Write-Host "==== PASSWORD HASH COMPAT: $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
