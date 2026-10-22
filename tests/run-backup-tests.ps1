# Server backups and encryption at rest (BK-01). Needs only the SERVER built in Debug — no agent.
#
# Ported from Marwanello/SaveLocker's run-console-security-tests.ps1 (BK-01), with the admin password
# header in place of that fork's admin sessions.
#
#   BK-01   backups               a zip of the database + each game's LATEST save only; its download is
#                                 admin-only, matched against the listing (traversal, absolute, encoded,
#                                 unlisted names are 404s), audited, `no-store`; the console's single-use
#                                 download ticket works once, with no password. The schedule (daily /
#                                 weekly, UTC) is validated, audited and wakes the scheduler. Restore:
#                                 refuses a non-backup before changing anything, takes a safety backup,
#                                 swaps the database, puts back a lost latest save, is audited and can
#                                 itself be undone; a database needing the startup fix-ups restores; a
#                                 failed backup leaves no .tmp; retention keeps the newest undo point.
#                                 Delete removes one listed backup, audited. The SteamGridDB key is
#                                 encrypted at rest (a legacy plain one at the next start) and no backup
#                                 carries it in the clear. A new build's first start backs up BEFORE
#                                 migrating, once.
#
# Database-level checks go through python's sqlite3 (native Python 3, else WSL's); without either they SKIP.
#
# Owns :5217 and .verify-backups. Usage:
#   .\tests\run-backup-tests.ps1 [-Port 5217] [-WslDistro Ubuntu]
param(
    [int]$Port = 5217,
    [string]$WslDistro = "Ubuntu"
)

$ErrorActionPreference = "Continue"
Add-Type -AssemblyName System.Net.Http

$root      = Split-Path $PSScriptRoot -Parent
$scratch   = Join-Path $root ".verify-backups"
$url       = "http://127.0.0.1:$Port"
$serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
$inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
$dotnet    = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
if (-not (Test-Path $serverDll)) { Write-Host "Server not built: $serverDll"; exit 2 }

foreach ($p in @($Port)) {
    if (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) {
        Write-Host "Port $p is already in use - a server or stub from a previous run is probably still up."
        Write-Host "Find it with: Get-NetTCPConnection -LocalPort $p -State Listen | % { Get-Process -Id `$_.OwningProcess }"
        exit 2
    }
}
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path $scratch) { Write-Host "Could not wipe $scratch - a server from a previous run may still hold it."; exit 2 }
New-Item -ItemType Directory -Force $scratch | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Skip($why) { Write-Host "SKIP: $why" }

# ---------------------------------------------------------------- HTTP helper
# Status, parsed body, and headers for BOTH success and error responses (Invoke-WebRequest throws on 4xx/5xx).
function Http($method, $path, $body = $null, $headers = @{}, $raw = $null) {
    $a = @{ Uri = "$url$path"; Method = $method; UseBasicParsing = $true; TimeoutSec = 120; Headers = $headers }
    if ($null -ne $raw)        { $a.Body = $raw; $a.ContentType = "application/json" }
    elseif ($null -ne $body)   { $a.Body = (ConvertTo-Json -InputObject $body -Depth 10 -Compress); $a.ContentType = "application/json" }
    try {
        $r = Invoke-WebRequest @a
        $status = [int]$r.StatusCode; $content = $r.Content; $hdrs = $r.Headers
    } catch {
        $resp = $_.Exception.Response
        if ($null -eq $resp) { throw }
        $status = [int]$resp.StatusCode
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $content = $reader.ReadToEnd(); $hdrs = $resp.Headers
    }
    $json = $null
    if ($content -isnot [byte[]]) { try { $json = $content | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Status = $status; Content = $content; Json = $json; Headers = $hdrs }
}
function PwHeader($pw)        { return @{ "X-Admin-Password" = $pw } }

# ---------------------------------------------------------------- server phases
$script:proc = $null
$script:state = $null
function Start-Phase($name, [hashtable]$extraEnv = @{}) {
    $script:state = Join-Path $scratch $name
    New-Item -ItemType Directory -Force (Join-Path $script:state "archives") | Out-Null
    $set = @{
        ASPNETCORE_URLS = $url; Storage__DbPath = (Join-Path $script:state "savelocker.db")
        Storage__ArchiveRoot = (Join-Path $script:state "archives"); Backup__Enabled = "false"
        Logging__EventLog__LogLevel__Default = "None"; Art__BackfillOnStartup = "false"
    }
    foreach ($k in $extraEnv.Keys) { $set[$k] = $extraEnv[$k] }
    foreach ($k in $set.Keys) { Set-Item -Path "Env:$k" -Value $set[$k] }
    $script:proc = Start-Process -FilePath $dotnet -ArgumentList @($serverDll) -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $script:state "out.log") -RedirectStandardError (Join-Path $script:state "err.log")
    foreach ($k in $set.Keys) { Remove-Item -Path "Env:$k" -ErrorAction SilentlyContinue }
    foreach ($i in 1..60) {
        Start-Sleep -Milliseconds 700
        try { Invoke-RestMethod "$url/api/admin/status" -TimeoutSec 3 | Out-Null; return } catch { }
    }
    Get-Content (Join-Path $script:state "err.log") -ErrorAction SilentlyContinue | Select-Object -Last 20
    Write-Host "FAIL: server ($name) did not start on $url"; Cleanup; exit 1
}
function Stop-Phase {
    if ($script:proc -and -not $script:proc.HasExited) { Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2   # the process keeps a lock on the SQLite file for a moment
}
function Cleanup {
    Stop-Phase
}

# ---------------------------------------------------------------- SQLite via python (server must be STOPPED)
# A native Python 3 (its standard library carries sqlite3) is preferred; WSL's is the fallback for a
# Windows box that has only that. Native is what CI has: a hosted Windows runner has Python and no WSL distro.
$script:pyNative = $null
foreach ($cmd in @(Get-Command python, python3 -CommandType Application -All -ErrorAction SilentlyContinue)) {
    # The WindowsApps "python.exe" is a Store launcher, not an interpreter, whether or not Python is installed.
    if ($cmd.Source -like "*\WindowsApps\*") { continue }
    $ver = try { (& $cmd.Source --version 2>&1) -join "" } catch { "" }
    if ($ver -match "^Python 3") { $script:pyNative = $cmd.Source; break }
}
function ConvertTo-WslPath($p) { $d = $p.Substring(0, 1).ToLower(); return "/mnt/$d" + ($p.Substring(2) -replace '\\', '/') }
function Invoke-Sqlite($sql, [switch]$Write, $Db = $null) {
    $py = Join-Path $scratch "query.py"; $sqlFile = Join-Path $scratch "query.sql"
    @'
import sqlite3, sys
db, sqlfile, mode = sys.argv[1], sys.argv[2], sys.argv[3]
sql = open(sqlfile, encoding="utf-8").read()
if mode == "rw":
    con = sqlite3.connect(db)
    con.executescript(sql)      # one call may carry several statements
    con.commit()
else:
    con = sqlite3.connect("file:" + db + "?mode=ro", uri=True)
    for row in con.execute(sql).fetchall():
        print("|".join("" if c is None else str(c) for c in row))
'@ | Set-Content -Path $py -Encoding utf8
    Set-Content -Path $sqlFile -Value $sql -Encoding utf8
    $db = if ($Db) { $Db } else { Join-Path $script:state "savelocker.db" }
    $mode = if ($Write) { "rw" } else { "ro" }
    if ($script:pyNative) { return (& $script:pyNative $py $db $sqlFile $mode 2>&1) }
    return (& wsl -d $WslDistro -- python3 (ConvertTo-WslPath $py) (ConvertTo-WslPath $db) (ConvertTo-WslPath $sqlFile) $mode 2>&1)
}
$wslOk = $false
# (No inline python here: PowerShell 5.1 strips the inner quotes before bash sees them.)
if (-not $script:pyNative) {
    try { $wslOk = ((& wsl -d $WslDistro -- python3 --version 2>&1) -join "") -match "^Python 3" } catch { }
}
$sqliteOk = [bool]$script:pyNative -or $wslOk
if (-not $sqliteOk) { Write-Host "NOTE: neither a native Python 3 nor WSL python3 ($WslDistro) is usable; database-level checks will be skipped." }


# A legacy "v1:salt:hash" password hash (100,000 iterations) — what every install before v2 stored.

$pw = "bk-test-password"

try {
Write-Host ""; Write-Host "==== BK-01 backups (database + latest saves), restore, encryption at rest ===="
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function ZipEntries($path) { $z = [System.IO.Compression.ZipFile]::OpenRead($path); try { @($z.Entries | ForEach-Object { $_.FullName }) } finally { $z.Dispose() } }
function SaveBytes($resp, $path) {
    if ($resp.Content -is [byte[]]) { [System.IO.File]::WriteAllBytes($path, $resp.Content) }
    else { [System.IO.File]::WriteAllText($path, "" + $resp.Content) }
}
function NewSaveZip($path, $text) {
    Remove-Item $path -ErrorAction SilentlyContinue
    $z = [System.IO.Compression.ZipFile]::Open($path, "Create")
    $e = $z.CreateEntry("slot1.sav"); $w = New-Object System.IO.StreamWriter($e.Open()); $w.Write($text); $w.Dispose(); $z.Dispose()
}
function Backups { @((Http GET "/api/admin/backups/status" $null $bk).Json.backups) }

$bkEnv = @{ Security__MaxFailedAttempts = "1000"; SAVELOCKER_VERSION = "9.0.0-bk-a" }
Start-Phase "backups" $bkEnv
Http POST "/api/admin/password" @{ password = $pw } | Out-Null
$bk = PwHeader $pw
$backupDir = Join-Path $script:state "backups"
$archDir = Join-Path $script:state "archives"
$bm = (Http POST "/api/machines/register" @{ name = "BK-M1" } (PwHeader $pw)).Json
$bgid = (Http POST "/api/games" @{ name = "BK Keep" } $bk).Json.id
$sz = Join-Path $scratch "bk-save.zip"
foreach ($n in 1..2) {
    NewSaveZip $sz "progress $n"
    $u = Invoke-WebRequest "$url/api/games/$bgid/upload?hash=bk-hash-$n" -Method Post -InFile $sz -ContentType "application/zip" `
        -Headers @{ "X-Api-Key" = $bm.apiKey } -UseBasicParsing
}
Check "setup: two versions uploaded for BK Keep" ((@((Http GET "/api/games/$bgid/versions" $null $bk).Json)).Count -eq 2)

Check "BK-01: status without the password -> 401" ((Http GET "/api/admin/backups/status").Status -eq 401)
Check "BK-01: Back up now without the password -> 401" ((Http POST "/api/admin/backup").Status -eq 401)
$made = Http POST "/api/admin/backup" $null $bk
$name = "" + $made.Json.backup.fileName
Check "BK-01: Back up now writes a zip named for its reason" ($made.Status -eq 200 -and $made.Json.ok -and $name -like "savelocker-*-manual.zip")
Check "BK-01: ... marked as holding saves" ($made.Json.backup.includesSaves -eq $true)
$st = (Http GET "/api/admin/backups/status" $null $bk).Json
Check "BK-01: status lists it as Manual" ((@($st.backups | Where-Object { $_.fileName -eq $name -and $_.reason -eq "Manual" })).Count -eq 1)
Check "BK-01: scheduled backups off (the suite's Backup__Enabled=false) -> no next run" ($st.enabled -eq $false -and $null -eq $st.nextRunAt)
Check "BK-01: the default schedule is weekly" ($st.frequency -eq "weekly")
Check "BK-01: status names the backup folder" ($st.backupRoot -eq $backupDir)
Check "BK-01: download without the password -> 401" ((Http GET "/api/admin/backups/$name").Status -eq 401)
Check "BK-01: download with a wrong password -> 401" ((Http GET "/api/admin/backups/$name" $null (PwHeader "forged")).Status -eq 401)
$dl = Http GET "/api/admin/backups/$name" $null $bk
$dlPath = Join-Path $scratch "bk-download.zip"; SaveBytes $dl $dlPath
$entries = @(ZipEntries $dlPath)
Check "BK-01: download with the password -> 200, a zip" ($dl.Status -eq 200 -and $entries.Count -ge 2)
Check "BK-01: ... holding the database and a manifest" ($entries -contains "savelocker.db" -and $entries -contains "manifest.json")
$saveEntries = @($entries | Where-Object { $_ -like "archives/*" })
Check "BK-01: ... and exactly the game's LATEST save, not its older version (1 archive)" ($saveEntries.Count -eq 1)
Check "BK-01: ... sent Cache-Control: no-store" (("" + $dl.Headers["Cache-Control"]) -match "no-store")
Check "BK-01: ... and audited as backup.download, naming the file" `
    ((@((Http GET "/api/audit?limit=500" $null $bk).Json | Where-Object { $_.action -eq "backup.download" -and $_.detail -eq $name })).Count -eq 1)
Check "BK-01: Back up now is audited too" ((@((Http GET "/api/audit?limit=500" $null $bk).Json | Where-Object { $_.action -eq "backup.manual" })).Count -ge 1)

# ---- the console's download path: a single-use ticket, so a plain link streams the file to disk
Check "BK-01: a download ticket without the password -> 401" ((Http POST "/api/admin/backups/$name/download-ticket").Status -eq 401)
Check "BK-01: a ticket for an unlisted name -> 404" ((Http POST "/api/admin/backups/..%2Fsavelocker.db/download-ticket" $null $bk).Status -eq 404)
$tk = Http POST "/api/admin/backups/$name/download-ticket" $null $bk
Check "BK-01: a ticket with the password -> 200, a link under /api/backup-download/" ($tk.Status -eq 200 -and ("" + $tk.Json.url) -like "/api/backup-download/*")
$td = Http GET ("" + $tk.Json.url)
$tdPath = Join-Path $scratch "bk-ticket.zip"; SaveBytes $td $tdPath
Check "BK-01: ... the link downloads the backup with no password (the ticket is the credential)" `
    ($td.Status -eq 200 -and (@(ZipEntries $tdPath)) -contains "savelocker.db" -and (("" + $td.Headers["Cache-Control"]) -match "no-store"))
Check "BK-01: ... once: the same link a second time -> 404" ((Http GET ("" + $tk.Json.url)).Status -eq 404)
Check "BK-01: ... a made-up ticket -> 404" ((Http GET "/api/backup-download/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef").Status -eq 404)
Check "BK-01: ... and issuing it is audited as a download" `
    ((@((Http GET "/api/audit?limit=500" $null $bk).Json | Where-Object { $_.action -eq "backup.download" -and $_.detail -eq $name })).Count -eq 2)
Set-Content -Path (Join-Path $backupDir "notes.txt") -Value "not a backup"
Set-Content -Path (Join-Path $backupDir "savelocker-20000101-000000.zip.tmp") -Value "half-written"
foreach ($bad in @("..%2Fsavelocker.db", "%2E%2E%2Fsavelocker.db", "..%5Csavelocker.db", "%2Fetc%2Fpasswd",
                   "C%3A%5CWindows%5Cwin.ini", "notes.txt", "savelocker-20000101-000000.zip.tmp",
                   "savelocker-20000101-000000.zip", ($name.ToUpperInvariant()))) {
    Check "BK-01: '$bad' -> 404" ((Http GET "/api/admin/backups/$bad" $null $bk).Status -eq 404)
}
Check "BK-01: nothing path-shaped ever returns the live database" (-not ((Http GET "/api/admin/backups/..%2F..%2Fsavelocker.db" $null $bk).Status -eq 200))

# ---- schedule: UTC, daily or weekly
Check "BK-01: settings without the password -> 401" ((Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 20; hourOfDay = 4 }).Status -eq 401)
Check "BK-01: keep 0 -> 400" ((Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 0; hourOfDay = 4 } $bk).Status -eq 400)
Check "BK-01: hour 24 -> 400" ((Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 20; hourOfDay = 24 } $bk).Status -eq 400)
Check "BK-01: frequency 'hourly' -> 400" ((Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 20; hourOfDay = 4; frequency = "hourly" } $bk).Status -eq 400)
Check "BK-01: day 7 -> 400" ((Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 20; hourOfDay = 4; frequency = "weekly"; dayOfWeek = 7 } $bk).Status -eq 400)
Check "BK-01: weekly on Wednesday at 04:00 UTC -> 204" `
    ((Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 20; hourOfDay = 4; frequency = "weekly"; dayOfWeek = 3 } $bk).Status -eq 204)
$next = $null
foreach ($i in 1..20) { Start-Sleep -Milliseconds 250; $s2 = (Http GET "/api/admin/backups/status" $null $bk).Json; if ($s2.nextRunAt) { $next = $s2; break } }
$nextUtc = if ($next) { ([DateTime]::Parse($next.nextRunAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)) } else { $null }
Check "BK-01: the scheduler wakes and plans the next run for a Wednesday, 04:00 UTC" `
    ($null -ne $nextUtc -and $nextUtc.Hour -eq 4 -and $nextUtc.DayOfWeek -eq [DayOfWeek]::Wednesday -and $nextUtc -gt [DateTime]::UtcNow -and $nextUtc -le [DateTime]::UtcNow.AddDays(7))
Check "BK-01: the change is audited in UTC, old and new" `
    ((@((Http GET "/api/audit?limit=500" $null $bk).Json | Where-Object { $_.action -eq "settings.backup" -and $_.detail -match "weekly on Wednesday at 04:00 UTC" -and $_.detail -match "was scheduled off" })).Count -eq 1)
Check "BK-01: daily at 22:00 UTC -> 204" ((Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 20; hourOfDay = 22; frequency = "daily"; dayOfWeek = 0 } $bk).Status -eq 204)
Start-Sleep -Milliseconds 800
$d2 = (Http GET "/api/admin/backups/status" $null $bk).Json
$dUtc = [DateTime]::Parse($d2.nextRunAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
Check "BK-01: ... a daily run is within a day, at 22:00 UTC" ($d2.frequency -eq "daily" -and $dUtc.Hour -eq 22 -and $dUtc -le [DateTime]::UtcNow.AddDays(1))

# ---- restore
$lostDir = Join-Path $archDir ($bgid -replace '-', '')   # the archive store names folders by the id without dashes
Http POST "/api/games" @{ name = "BK Later" } $bk | Out-Null
Get-ChildItem $lostDir -File -ErrorAction SilentlyContinue | Remove-Item -Force   # every archive of BK Keep "lost"
Check "setup: BK Keep's archives are gone from disk and BK Later exists" `
    (@(Get-ChildItem $lostDir -File -ErrorAction SilentlyContinue).Count -eq 0 -and
     (@((Http GET "/api/overview" $null $bk).Json | Where-Object { $_.game.name -eq "BK Later" })).Count -eq 1)
Check "BK-01: restore without the password -> 401" ((Http POST "/api/admin/backups/$name/restore").Status -eq 401)
Check "BK-01: restore of an unlisted name -> 404" ((Http POST "/api/admin/backups/savelocker-20000101-000000.zip/restore" $null $bk).Status -eq 404)
Set-Content -Path (Join-Path $backupDir "savelocker-20000101-000001-manual.zip") -Value "not a zip at all"
$junk = Http POST "/api/admin/backups/savelocker-20000101-000001-manual.zip/restore" $null $bk
Check "BK-01: restoring something that is not a backup is refused (400) ..." ($junk.Status -eq 400)
Check "BK-01: ... before anything changed (BK Later still there, no safety backup taken)" `
    ((@((Http GET "/api/overview" $null $bk).Json | Where-Object { $_.game.name -eq "BK Later" })).Count -eq 1 -and
     (@(Backups | Where-Object { $_.reason -eq "BeforeRestore" })).Count -eq 0)
Remove-Item (Join-Path $backupDir "savelocker-20000101-000001-manual.zip") -Force
$rs = Http POST "/api/admin/backups/$name/restore" $null $bk
Check "BK-01: restore -> 200, naming the file and a safety backup" ($rs.Status -eq 200 -and $rs.Json.restoredFrom -eq $name -and $rs.Json.safetyBackup -like "*-before-restore.zip")
Check "BK-01: ... the latest save it held is back on disk (1 put back)" ($rs.Json.savesRestored -eq 1 -and @(Get-ChildItem $lostDir -File).Count -eq 1)
$ov = @((Http GET "/api/overview" $null $bk).Json)
Check "BK-01: ... the database is the backup's: BK Later (made after it) is gone, BK Keep is back" `
    ((@($ov | Where-Object { $_.game.name -eq "BK Later" })).Count -eq 0 -and (@($ov | Where-Object { $_.game.name -eq "BK Keep" })).Count -eq 1)
$hd = Http GET "/api/games/$bgid/versions/$((@($ov | Where-Object { $_.game.name -eq 'BK Keep' }))[0].head.id)/download" $null $bk
Check "BK-01: ... and the restored latest save downloads again" ($hd.Status -eq 200)
Check "BK-01: ... the safety backup is listed as Before restore" ((@(Backups | Where-Object { $_.fileName -eq $rs.Json.safetyBackup -and $_.reason -eq "BeforeRestore" })).Count -eq 1)
Check "BK-01: ... and the restore is audited in the restored database" `
    ((@((Http GET "/api/audit?limit=500" $null $bk).Json | Where-Object { $_.action -eq "backup.restore" -and $_.detail -match [regex]::Escape($name) })).Count -eq 1)
$undo = Http POST "/api/admin/backups/$($rs.Json.safetyBackup)/restore" $null $bk
Check "BK-01: restoring the safety backup undoes it: BK Later is back" `
    ($undo.Status -eq 200 -and (@((Http GET "/api/overview" $null $bk).Json | Where-Object { $_.game.name -eq "BK Later" })).Count -eq 1)
Check "BK-01: ... and the undo's own safety backup never overwrote the backup it restored from (same second)" `
    ($undo.Json.safetyBackup -ne $undo.Json.restoredFrom -and (@(Backups | Where-Object { $_.fileName -eq $rs.Json.safetyBackup })).Count -eq 1)

# ---- delete one backup
$del = "" + $undo.Json.safetyBackup
$before = @(Backups).Count
Check "BK-01: delete without the password -> 401" ((Http DELETE "/api/admin/backups/$del").Status -eq 401)
foreach ($bad in @("..%2Fsavelocker.db", "notes.txt", "savelocker-20000101-000000.zip")) {
    Check "BK-01: delete '$bad' -> 404" ((Http DELETE "/api/admin/backups/$bad" $null $bk).Status -eq 404)
}
Check "BK-01: ... and the live database and the non-backup file are untouched" `
    ((Test-Path (Join-Path $backupDir "notes.txt")) -and (Http GET "/api/admin/backups/status" $null $bk).Status -eq 200)
Check "BK-01: delete a backup -> 204" ((Http DELETE "/api/admin/backups/$del" $null $bk).Status -eq 204)
Check "BK-01: ... it is gone from the listing and the disk, and only it" `
    ((@(Backups).Count -eq $before - 1) -and -not (Test-Path (Join-Path $backupDir $del)) -and (@(Backups | Where-Object { $_.fileName -eq $name })).Count -eq 1)
Check "BK-01: ... audited as backup.delete, naming the file" `
    ((@((Http GET "/api/audit?limit=500" $null $bk).Json | Where-Object { $_.action -eq "backup.delete" -and $_.detail -eq $del })).Count -eq 1)
Check "BK-01: deleting it again -> 404" ((Http DELETE "/api/admin/backups/$del" $null $bk).Status -eq 404)
# A file that cannot be deleted (held open without delete sharing) is a 409 with the reason, not a bare 500.
$heldBk = [System.IO.File]::Open((Join-Path $backupDir $name), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
try { $locked = Http DELETE "/api/admin/backups/$name" $null $bk } finally { $heldBk.Dispose() }
Check "BK-01: deleting a backup that is held open -> 409, saying why ..." ($locked.Status -eq 409 -and ("" + $locked.Content) -match "could not be deleted")
Check "BK-01: ... and it is still listed" ((@(Backups | Where-Object { $_.fileName -eq $name })).Count -eq 1)

# A restored database gets exactly what a start gives it, not Migrate() alone: a database whose MachineSavePaths
# table predates its migration row (made out-of-band, as old installs were) makes a bare Migrate() throw "table
# already exists" AFTER the live file was replaced. Built from the downloaded backup's database, as a legacy .db.
if ($sqliteOk) {
    $z = [System.IO.Compression.ZipFile]::OpenRead($dlPath); $legacy = Join-Path $backupDir "savelocker-20000102-000000.db"
    try { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($z.GetEntry("savelocker.db"), $legacy, $true) } finally { $z.Dispose() }
    Invoke-Sqlite "DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20260706022305_AddMachineSavePaths';" -Write -Db $legacy | Out-Null
    $lr = Http POST "/api/admin/backups/savelocker-20000102-000000.db/restore" $null $bk
    Check "BK-01: restoring a database that needs the startup fix-ups -> 200 (not a half-finished 500)" ($lr.Status -eq 200)
    Check "BK-01: ... and the server still answers from it (BK Keep back, BK Later gone)" `
        ((@((Http GET "/api/overview" $null $bk).Json | Where-Object { $_.game.name -eq "BK Keep" })).Count -eq 1 -and
         (@((Http GET "/api/overview" $null $bk).Json | Where-Object { $_.game.name -eq "BK Later" })).Count -eq 0)
} else { Skip "BK-01: the restore fix-up check needs Python 3" }

# A backup that fails part-way leaves no half-written .zip.tmp behind: hold BK Keep's latest archive open with
# no sharing, so zipping it throws.
$held = @(Get-ChildItem $lostDir -File)[0]
$lock = [System.IO.File]::Open($held.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
try { $failed = Http POST "/api/admin/backup" $null $bk } finally { $lock.Dispose() }
Check "BK-01: a backup that cannot read a save fails (ok=false) ..." ($failed.Status -eq 200 -and $failed.Json.ok -eq $false)
Check "BK-01: ... and leaves no temporary file behind" `
    ((@(Get-ChildItem $backupDir -Force -File -Filter "*.tmp" | Where-Object { $_.Name -ne "savelocker-20000101-000000.zip.tmp" })).Count -eq 0)
Check "BK-01: ... and the status reports the failure" ($null -ne (Http GET "/api/admin/backups/status" $null $bk).Json.lastError)

Http POST "/api/admin/backups/settings" @{ enabled = $true; retentionCount = 2; hourOfDay = 4; frequency = "weekly"; dayOfWeek = 3 } $bk | Out-Null
$newestUndo = "" + (@(Backups | Where-Object { $_.reason -eq "BeforeRestore" }))[0].fileName
1..3 | ForEach-Object { Start-Sleep -Milliseconds 1100; Http POST "/api/admin/backup" $null $bk | Out-Null }
$kept = @(Backups)
$undoPoint = @($kept | Where-Object { $_.reason -eq "BeforeRestore" })
Check "BK-01: retention prunes to the new keep (2) ..." ((@($kept | Where-Object { $_.reason -eq "Manual" })).Count -eq 2 -and $kept.Count -eq 3)
Check "BK-01: ... but always keeps the newest Before restore backup, the undo for the last restore" `
    ($undoPoint.Count -eq 1 -and $newestUndo -ne "" -and $undoPoint[0].fileName -eq $newestUndo)
Check "BK-01: ... and a successful backup clears the last failure" ($null -eq (Http GET "/api/admin/backups/status" $null $bk).Json.lastError)
Http POST "/api/admin/backups/settings" @{ enabled = $false; retentionCount = 20; hourOfDay = 3; frequency = "weekly"; dayOfWeek = 0 } $bk | Out-Null
Check "BK-01: no before-upgrade backup on a first start (nothing to protect yet)" ((@(Backups | Where-Object { $_.reason -eq "BeforeUpgrade" })).Count -eq 0)
Stop-Phase

# ---- encryption at rest: a plaintext secret written before encryption existed is encrypted at the next start
if ($sqliteOk) {
    Invoke-Sqlite "INSERT OR REPLACE INTO Settings([Key],[Value]) VALUES ('SteamGridDb:ApiKey','plain-sgdb-key-WXYZ');" -Write | Out-Null
}
Start-Phase "backups" $bkEnv
$bk = PwHeader $pw
Check "BK-01: restarting the SAME build takes no before-upgrade backup" ((@(Backups | Where-Object { $_.reason -eq "BeforeUpgrade" })).Count -eq 0)
if ($sqliteOk) {
    $sg = (Http GET "/api/settings" $null $bk).Json
    Check "BK-01: the stored SteamGridDB key still works after encryption (masked, ends WXYZ)" ($sg.steamGridDbConfigured -and ("" + $sg.steamGridDbKeyMasked).EndsWith("WXYZ"))
    $encName = "" + (Http POST "/api/admin/backup" $null $bk).Json.backup.fileName
}
Stop-Phase
if ($sqliteOk) {
    $raw = ("" + (Invoke-Sqlite "SELECT Value FROM Settings WHERE [Key]='SteamGridDb:ApiKey'")).Trim()
    Check "BK-01: the database holds the SteamGridDB key encrypted (enc:v1:), never in plain text" ($raw -like "enc:v1:*" -and $raw -notmatch "plain-sgdb-key")
    $encZip = Join-Path $backupDir $encName
    $z = [System.IO.Compression.ZipFile]::OpenRead($encZip); $dbx = Join-Path $scratch "bk-enc.db"
    try { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($z.GetEntry("savelocker.db"), $dbx, $true) } finally { $z.Dispose() }
    Check "BK-01: ... and so does a backup of it (no plain-text key anywhere in the file)" `
        (-not ([System.Text.Encoding]::GetEncoding(28591).GetString([System.IO.File]::ReadAllBytes($dbx)).Contains("plain-sgdb-key")))
    Check "BK-01: ... while the key ring that decrypts it is beside the database, not in the backup" `
        ((Test-Path (Join-Path $script:state "keys")) -and -not (@(ZipEntries $encZip) | Where-Object { $_ -like "*key*" }))
} else { Skip "BK-01: the encryption-at-rest checks need Python 3" }

Start-Phase "backups" @{ Security__MaxFailedAttempts = "1000"; SAVELOCKER_VERSION = "9.0.0-bk-b" }
$bk = PwHeader $pw
$up = @(Backups | Where-Object { $_.reason -eq "BeforeUpgrade" })
Check "BK-01: a NEW build's first start takes exactly one before-upgrade backup" ($up.Count -eq 1 -and $up[0].fileName -like "*-before-upgrade.zip")
Check "BK-01: ... listed as database only (it runs before migrations, without saves)" ($up.Count -eq 1 -and $up[0].includesSaves -eq $false)
Stop-Phase
if ($up.Count -eq 1 -and $sqliteOk) {
    $z = [System.IO.Compression.ZipFile]::OpenRead((Join-Path $backupDir $up[0].fileName)); $dbu = Join-Path $scratch "bk-up.db"
    try { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($z.GetEntry("savelocker.db"), $dbu, $true) } finally { $z.Dispose() }
    $snapVer = ("" + (Invoke-Sqlite "SELECT Value FROM Settings WHERE [Key]='Server:LastStartedVersion'" -Db $dbu)).Trim()
    Check "BK-01: ... and it predates the new start: it still records the OLD build" ($snapVer -eq "9.0.0-bk-a")
} else { Skip "BK-01: reading the backup's recorded build needs Python 3" }
Start-Phase "backups" @{ Security__MaxFailedAttempts = "1000"; SAVELOCKER_VERSION = "9.0.0-bk-b" }
$bk = PwHeader $pw
Check "BK-01: ... and only once (a second start of that build takes none)" ((@(Backups | Where-Object { $_.reason -eq "BeforeUpgrade" })).Count -eq 1)
Stop-Phase
}
finally {
    Cleanup
}

Write-Host ""
Write-Host "==== BACKUPS RESULT: $pass passed, $fail failed ===="
if ($fail -gt 0) { exit 1 }
