# Hardening (Linux agent Phase 6) - 12 checks. Runs on BOTH Windows and Linux.
#
# These are SECURITY tests: each one must prove the ATTACK FAILS, not merely that ordinary saves
# still sync. Two of them delete-or-leak files outside the save folder if the fix regresses, so they
# assert on the OUTSIDE file, which is the only thing that actually matters.
#
#   1. Symlink escape (archive)  - a link inside a save folder must not drag its target INTO the archive.
#   2. Symlink escape (RESTORE)  - the restore's delete pass must not reach THROUGH a link and delete
#                                  files outside the save folder. This is the data-loss one.
#   3. Zip-slip                  - a malicious archive with a `../` entry must not write outside the target.
#   4. OneDrive regression guard - the fix must key on symlinks ONLY. OneDrive files-on-demand are also
#                                  reparse points, and skipping every reparse point would silently stop
#                                  archiving OneDrive saves (see Gotchas.md).
#
# Windows uses a JUNCTION (no admin needed; symlinks require elevation or Developer Mode) — which is
# the Windows form of exactly this bug. Linux uses a symlink.
#
# This suite owns its server on :5182. Usage: .\tests\run-hardening-tests.ps1 / pwsh tests/...

$ErrorActionPreference = "Continue"

$onWindows = if ($null -eq $IsWindows) { $true } else { $IsWindows }

$root    = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ".verify-harden"
$server  = "http://localhost:5182"

if ($onWindows) {
    $inProgramFiles = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    $dotnet = if (Test-Path $inProgramFiles) { $inProgramFiles } else { "dotnet" }
    $dll    = Join-Path $root "src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll"
} else {
    $dotnet = "dotnet"
    $dll    = Join-Path $root "src/Agent.Linux/bin/Debug/net10.0/savelocker.dll"
}
if (-not (Test-Path $dll)) { Write-Host "Agent not built: $dll"; exit 2 }

$serverDll = Join-Path $root "src/Server/bin/Debug/net10.0/SaveLocker.Server.dll"
if (-not (Test-Path $serverDll)) { Write-Host "Server not built: $serverDll"; exit 2 }

Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $scratch | Out-Null

$pass = 0; $fail = 0
function Check($name, $cond) {
    if ($cond) { Write-Host "PASS: $name"; $script:pass++ }
    else        { Write-Host "FAIL: $name"; $script:fail++ }
}
function Agent { & $dotnet $dll @args 2>&1 }

# A directory link, by whichever mechanism the OS gives us without elevation.
function New-DirLink($linkPath, $targetPath) {
    if ($onWindows) { New-Item -ItemType Junction -Path $linkPath -Target $targetPath | Out-Null }
    else            { New-Item -ItemType SymbolicLink -Path $linkPath -Target $targetPath | Out-Null }
}

# ---- Server (its own port + state; never touches the dev server) ----
$state = Join-Path $scratch "state"
New-Item -ItemType Directory -Force (Join-Path $state "archives") | Out-Null
$env:ASPNETCORE_URLS      = $server
$env:Storage__DbPath      = Join-Path $state "savelocker.db"
$env:Storage__ArchiveRoot = Join-Path $state "archives"
$env:Backup__Enabled      = "false"

# -WindowStyle is unsupported on PowerShell Core on Linux.
$serverProc = if ($onWindows) {
    Start-Process -FilePath $dotnet -ArgumentList $serverDll -PassThru -WindowStyle Hidden
} else {
    Start-Process -FilePath $dotnet -ArgumentList $serverDll -PassThru
}
foreach ($i in 1..40) {
    Start-Sleep -Milliseconds 700
    try { Invoke-RestMethod "$server/api/admin/status" -TimeoutSec 3 | Out-Null; break } catch { }
}

try {
    $stamp = Get-Date -Format "HHmmss"

    # ---- Fixtures ----
    $pcSave   = Join-Path $scratch "pc_save";   New-Item -ItemType Directory -Force $pcSave   | Out-Null
    $deckSave = Join-Path $scratch "deck_save"; New-Item -ItemType Directory -Force $deckSave | Out-Null

    # "Outside" trees. Nothing the agent does may ever touch these.
    $secrets = Join-Path $scratch "OUTSIDE_secrets"; New-Item -ItemType Directory -Force $secrets | Out-Null
    "ssh private key"      | Set-Content (Join-Path $secrets "id_rsa")     -Encoding utf8
    $precious = Join-Path $scratch "OUTSIDE_precious"; New-Item -ItemType Directory -Force $precious | Out-Null
    "irreplaceable photos" | Set-Content (Join-Path $precious "photos.txt") -Encoding utf8

    # A real save file, plus a link pointing out of the save folder (a Wine prefix is full of these).
    "real save data" | Set-Content (Join-Path $pcSave "save.dat") -Encoding utf8
    New-DirLink (Join-Path $pcSave "link_to_secrets") $secrets

    $pcCfg   = Join-Path $scratch "pc.json"
    $deckCfg = Join-Path $scratch "deck.json"
    foreach ($c in @($pcCfg, $deckCfg)) {
        @{ ServerUrl = $server; Games = @() } | ConvertTo-Json | Set-Content -Path $c -Encoding utf8
    }
    Agent register --name "HardenPC-$stamp"   --config $pcCfg   | Out-Null
    Agent register --name "HardenDeck-$stamp" --config $deckCfg | Out-Null

    $game = "HardenGame-$stamp"
    Agent add-game --name $game --dir $pcSave   --config $pcCfg   | Out-Null
    Agent add-game --name $game --dir $deckSave --config $deckCfg | Out-Null

    # =================================================================================
    # 1. SYMLINK ESCAPE (archive): the link's target must not be pulled into the archive
    # =================================================================================
    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --config $deckCfg | Out-Null

    Check "the real save file syncs"              (Test-Path (Join-Path $deckSave "save.dat"))
    Check "linked-to file is NOT in the archive"  (-not (Test-Path (Join-Path $deckSave "link_to_secrets/id_rsa")))
    Check "the link itself is not recreated"      (-not (Test-Path (Join-Path $deckSave "link_to_secrets")))

    # The hash must agree: it is computed over the same file set, so a followed link would change it.
    $h = Agent hash --dir $pcSave --config $pcCfg
    $expected = Agent hash --dir $deckSave --config $deckCfg
    Check "hash ignores the link (same on both)"  ("$h".Trim() -eq "$expected".Trim())

    # =================================================================================
    # 2. SYMLINK ESCAPE ON RESTORE -- THE DATA-LOSS ONE
    # =================================================================================
    # Restore deletes target files that are absent from the archive. If it walks through a link, it
    # deletes files OUTSIDE the save folder. The assertion is on the outside file: it must survive.
    New-DirLink (Join-Path $deckSave "link_to_precious") $precious

    "pc progress 2" | Set-Content (Join-Path $pcSave "save.dat") -Encoding utf8
    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --force --config $deckCfg | Out-Null   # force: restore runs its delete pass

    Check "restore did NOT delete the outside file" (Test-Path (Join-Path $precious "photos.txt"))
    Check "restore did NOT delete the outside dir"  (Test-Path $precious)
    Check "restore left the user's link alone"      (Test-Path (Join-Path $deckSave "link_to_precious"))
    Check "restore still applied the real update"   ((Get-Content (Join-Path $deckSave "save.dat") -Raw).Trim() -eq "pc progress 2")

    # =================================================================================
    # 3. ONEDRIVE REGRESSION GUARD
    # =================================================================================
    # The tempting fix -- skip anything with FileAttributes.ReparsePoint -- would ALSO skip OneDrive
    # files-on-demand placeholders, silently ending OneDrive save sync. The fix must key on symlinks
    # only. A plain nested file is the closest thing this harness can assert without OneDrive itself:
    # it must still be archived, proving the walk did not become over-eager.
    $nested = Join-Path $pcSave "profiles/slot1"
    New-Item -ItemType Directory -Force $nested | Out-Null
    "nested save" | Set-Content (Join-Path $nested "slot.dat") -Encoding utf8

    Agent push $game --config $pcCfg | Out-Null
    Agent pull $game --force --config $deckCfg | Out-Null
    Check "ordinary nested files still sync"        (Test-Path (Join-Path $deckSave "profiles/slot1/slot.dat"))

    # =================================================================================
    # 4. ZIP-SLIP: a malicious archive must not write outside the target
    # =================================================================================
    # This runs LAST on purpose: it force-uploads a hostile archive, which becomes the server head.
    # Any sync check placed after it would be testing against a poisoned head and fail for reasons
    # that have nothing to do with the code under test.
    # Uploaded straight to the server (the agent would never build such a zip), then pulled, so the
    # real RestoreArchive path runs on it. `ZipFile.ExtractToDirectory` is expected to reject it --
    # this check exists to make sure nobody ever "optimises" that into a hand-rolled extractor.
    # BOTH assemblies: ZipFile lives in System.IO.Compression.FileSystem, ZipArchiveMode in
    # System.IO.Compression. Missing the second one throws a TERMINATING type error that skips the
    # rest of this try block -- i.e. it would silently skip the security checks below. So building the
    # fixture is itself asserted: a broken fixture must FAIL, never quietly disappear.
    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue

    $evilZip = Join-Path $scratch "evil.zip"
    $builtEvilZip = $false
    try {
        $zip = [System.IO.Compression.ZipFile]::Open($evilZip, [System.IO.Compression.ZipArchiveMode]::Create)
        $entry = $zip.CreateEntry("../../ESCAPED.txt")
        $sw = New-Object System.IO.StreamWriter($entry.Open())
        $sw.Write("pwned"); $sw.Close()
        $zip.Dispose()
        $builtEvilZip = $true
    } catch {
        Write-Host "  (could not build the malicious zip: $($_.Exception.Message))"
    }
    Check "the zip-slip fixture was actually built" $builtEvilZip

    # /api/games is an AGENT route: it answers 401 without X-Api-Key, and an unhandled 401 here is a
    # TERMINATING error that would skip the zip-slip checks entirely (Gotchas.md notes the same trap
    # in the readiness probes). So the key comes first, and the call carries it.
    $apiKey = (Get-Content $pcCfg -Raw | ConvertFrom-Json).ApiKey
    $gameId = (Invoke-RestMethod "$server/api/games" -Headers @{ "X-Api-Key" = $apiKey } |
               Where-Object { $_.name -eq $game }).id
    Check "the zip-slip target game resolved" ($null -ne $gameId)
    $escapeTarget = Join-Path $scratch "ESCAPED.txt"

    try {
        Invoke-RestMethod "$server/api/games/$gameId/upload?hash=deadbeef&force=true" -Method Post `
            -Headers @{ "X-Api-Key" = $apiKey } -ContentType "application/zip" `
            -InFile $evilZip | Out-Null
    } catch { }

    Agent pull $game --force --config $deckCfg | Out-Null
    Check "zip-slip wrote nothing outside the target" (-not (Test-Path $escapeTarget))
    Check "zip-slip did not land in the save folder"  (-not (Test-Path (Join-Path $deckSave "ESCAPED.txt")))

    # =================================================================================
    # 5. PREFIX-ROOT SANITY (doctor names the real mistake)
    # =================================================================================
    # Only the Linux agent has `doctor`. A save path that is really a Wine prefix must be NAMED as
    # such, not left to fail later as a baffling "your save is too big".
    if (-not $onWindows) {
        $fakePrefix = Join-Path $scratch "compatdata/12345"
        New-Item -ItemType Directory -Force (Join-Path $fakePrefix "pfx/drive_c") | Out-Null
        "junk" | Set-Content (Join-Path $fakePrefix "pfx/drive_c/windows.dll") -Encoding utf8

        $prefixCfg = Join-Path $scratch "prefix.json"
        @{ ServerUrl = $server; Games = @() } | ConvertTo-Json | Set-Content $prefixCfg -Encoding utf8
        Agent register --name "HardenPrefix-$stamp" --config $prefixCfg | Out-Null
        Agent add-game --name "PrefixGame-$stamp" --dir $fakePrefix --config $prefixCfg | Out-Null

        $doc = Agent doctor --config $prefixCfg
        Check "doctor names a prefix-root save path" ("$doc" -match "Wine PREFIX")
    } else {
        Write-Host "SKIP: doctor prefix check (Linux agent only)"
    }
}
finally {
    if ($serverProc -and -not $serverProc.HasExited) { Stop-Process -Id $serverProc.Id -Force }
    Remove-Item Env:ASPNETCORE_URLS, Env:Storage__DbPath, Env:Storage__ArchiveRoot, Env:Backup__Enabled -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Hardening: $pass passed, $fail failed."
if ($fail -gt 0) { exit 1 }
