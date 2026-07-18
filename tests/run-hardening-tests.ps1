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
    # real RestoreArchive path runs on it.
    #
    # NOTE (2026-07-18): extraction IS now hand-rolled — `ExtractChecked` replaced
    # `ZipFile.ExtractToDirectory` so the uncompressed-size cap can be enforced against bytes actually
    # written. This check therefore stopped being a guard against someone doing that and became the
    # thing that proves the replacement still rejects zip-slip itself. Do not delete it.
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
    # 5. WRITE THROUGH A LINK ON THE COPY PASS (the restore's other half)
    # =================================================================================
    # The delete pass was made no-follow in Phase 6, but the COPY pass was not. If the save folder
    # already contains a symlinked directory and the archive carries a matching path, File.Copy
    # writes straight through the link and OVERWRITES a file outside the save folder with
    # attacker-chosen bytes. The archive picks those paths, and the archive comes from the network.
    #
    # This asserts on the OUTSIDE file, because that is the only thing that actually matters.
    $linkVictim = Join-Path $scratch "OUTSIDE_linkvictim"
    New-Item -ItemType Directory -Force $linkVictim | Out-Null
    "original contents" | Set-Content (Join-Path $linkVictim "secret.txt") -Encoding utf8

    $copySave = Join-Path $scratch "copy_save"
    New-Item -ItemType Directory -Force $copySave | Out-Null
    "a real save" | Set-Content (Join-Path $copySave "save.dat") -Encoding utf8
    New-DirLink (Join-Path $copySave "linkdir") $linkVictim

    $throughZip = Join-Path $scratch "through-link.zip"
    $builtThroughZip = $false
    try {
        $zip = [System.IO.Compression.ZipFile]::Open($throughZip, [System.IO.Compression.ZipArchiveMode]::Create)
        $entry = $zip.CreateEntry("linkdir/secret.txt")
        $sw = New-Object System.IO.StreamWriter($entry.Open())
        $sw.Write("PWNED THROUGH THE LINK"); $sw.Close()
        $zip.Dispose()
        $builtThroughZip = $true
    } catch {
        Write-Host "  (could not build the write-through-link zip: $($_.Exception.Message))"
    }
    Check "the write-through-link fixture was built" $builtThroughZip

    $copyCfg = Join-Path $scratch "copy.json"
    @{ ServerUrl = $server; Games = @() } | ConvertTo-Json | Set-Content $copyCfg -Encoding utf8
    Agent register --name "HardenCopy-$stamp" --config $copyCfg | Out-Null
    $copyGame = "CopyThroughGame-$stamp"
    Agent add-game --name $copyGame --dir $copySave --config $copyCfg | Out-Null

    # Take the id from the AGENT'S OWN CONFIG, not by matching names against /api/games. A name
    # lookup there returned TWO ids, so the upload URL became malformed and 404'd -- and because the
    # upload error was swallowed, every security assertion below passed VACUOUSLY against an archive
    # the server never had. Using the id the agent will actually pull guarantees they refer to the
    # same game.
    $copyKey = (Get-Content $copyCfg -Raw | ConvertFrom-Json).ApiKey
    $copyGameId = (Get-Content $copyCfg -Raw | ConvertFrom-Json).Games[0].GameId
    Check "the write-through-link target game resolved" ($null -ne $copyGameId)

    # The upload MUST be asserted. Swallowing its failure is how these checks passed vacuously:
    # with no archive on the server the pull answers "nothing to pull", the outside file is
    # untouched, and the security assertions look green while testing nothing at all.
    $uploadedThrough = $false
    try {
        Invoke-RestMethod "$server/api/games/$copyGameId/upload?hash=deadbeef2&force=true" -Method Post `
            -Headers @{ "X-Api-Key" = $copyKey } -ContentType "application/zip" `
            -InFile $throughZip | Out-Null
        $uploadedThrough = $true
    } catch { Write-Host "  (write-through-link upload failed: $($_.Exception.Message))" }
    Check "the hostile archive reached the server" $uploadedThrough

    $throughOut = Agent pull $copyGame --force --config $copyCfg

    $victimNow = Get-Content (Join-Path $linkVictim "secret.txt") -Raw
    Check "the file OUTSIDE the save folder was NOT overwritten" ($victimNow -match "original contents")
    Check "the restore was refused, not silently skipped"        ("$throughOut" -match "REFUSED|symlink|junction")

    # =================================================================================
    # 6. ARCHIVE EXHAUSTION (zip bomb) — refused BEFORE it fills the disk
    # =================================================================================
    # A few KB of zip can expand to terabytes. On a Deck that fills the disk with nobody watching.
    # The limits are env-overridable, which is what makes this testable without writing a 2 GB
    # fixture: the cap is lowered instead of the bomb being made bigger.
    $bombSave = Join-Path $scratch "bomb_save"
    New-Item -ItemType Directory -Force $bombSave | Out-Null
    "a real save" | Set-Content (Join-Path $bombSave "save.dat") -Encoding utf8

    $bombZip = Join-Path $scratch "bomb.zip"
    $builtBomb = $false
    try {
        $zip = [System.IO.Compression.ZipFile]::Open($bombZip, [System.IO.Compression.ZipArchiveMode]::Create)
        # 8 MB of zeros — compresses to a few KB, and blows a 1 MB cap decisively.
        $entry = $zip.CreateEntry("huge.bin")
        $stream = $entry.Open()
        $chunk = New-Object byte[] (1024 * 1024)
        foreach ($i in 1..8) { $stream.Write($chunk, 0, $chunk.Length) }
        $stream.Close()
        $zip.Dispose()
        $builtBomb = $true
    } catch {
        Write-Host "  (could not build the zip bomb: $($_.Exception.Message))"
    }
    Check "the zip-bomb fixture was built" $builtBomb

    $bombCfg = Join-Path $scratch "bomb.json"
    @{ ServerUrl = $server; Games = @() } | ConvertTo-Json | Set-Content $bombCfg -Encoding utf8
    Agent register --name "HardenBomb-$stamp" --config $bombCfg | Out-Null
    $bombGame = "BombGame-$stamp"
    Agent add-game --name $bombGame --dir $bombSave --config $bombCfg | Out-Null

    $bombKey = (Get-Content $bombCfg -Raw | ConvertFrom-Json).ApiKey
    $bombGameId = (Get-Content $bombCfg -Raw | ConvertFrom-Json).Games[0].GameId

    $uploadedBomb = $false
    try {
        Invoke-RestMethod "$server/api/games/$bombGameId/upload?hash=deadbeef3&force=true" -Method Post `
            -Headers @{ "X-Api-Key" = $bombKey } -ContentType "application/zip" `
            -InFile $bombZip | Out-Null
        $uploadedBomb = $true
    } catch { Write-Host "  (bomb upload failed: $($_.Exception.Message))" }
    Check "the zip bomb reached the server" $uploadedBomb

    $env:SAVELOCKER_MAX_RESTORE_MB = "1"
    $bombOut = Agent pull $bombGame --force --config $bombCfg
    Remove-Item Env:SAVELOCKER_MAX_RESTORE_MB -ErrorAction SilentlyContinue

    Check "an oversized archive is refused"        ("$bombOut" -match "REFUSED|limit")
    Check "the bomb's payload never landed"        (-not (Test-Path (Join-Path $bombSave "huge.bin")))
    Check "the existing save was left intact"      (Test-Path (Join-Path $bombSave "save.dat"))

    # Entry count is the other exhaustion axis: many tiny files are cheap to ship and expensive to
    # write, and they exhaust inodes rather than bytes.
    $manyZip = Join-Path $scratch "many.zip"
    try {
        $zip = [System.IO.Compression.ZipFile]::Open($manyZip, [System.IO.Compression.ZipArchiveMode]::Create)
        foreach ($i in 1..40) {
            $entry = $zip.CreateEntry("f$i.txt")
            $sw = New-Object System.IO.StreamWriter($entry.Open())
            $sw.Write("x"); $sw.Close()
        }
        $zip.Dispose()
    } catch { }

    $uploadedMany = $false
    try {
        Invoke-RestMethod "$server/api/games/$bombGameId/upload?hash=deadbeef4&force=true" -Method Post `
            -Headers @{ "X-Api-Key" = $bombKey } -ContentType "application/zip" `
            -InFile $manyZip | Out-Null
        $uploadedMany = $true
    } catch { Write-Host "  (many-entry upload failed: $($_.Exception.Message))" }
    Check "the many-entry archive reached the server" $uploadedMany

    $env:SAVELOCKER_MAX_RESTORE_ENTRIES = "10"
    $manyOut = Agent pull $bombGame --force --config $bombCfg
    Remove-Item Env:SAVELOCKER_MAX_RESTORE_ENTRIES -ErrorAction SilentlyContinue

    Check "an archive with too many entries is refused" ("$manyOut" -match "REFUSED|limit")
    Check "none of its entries landed"                  (-not (Test-Path (Join-Path $bombSave "f1.txt")))

    # And the limits must NOT reject an ordinary save — a security control that blocks real use gets
    # turned off, which is worse than not having it.
    $okOut = Agent pull $bombGame --force --config $bombCfg
    Check "a normal-sized archive still restores"       (-not ("$okOut" -match "REFUSED"))

    # =================================================================================
    # 7. PREFIX-ROOT SANITY (doctor names the real mistake)
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
