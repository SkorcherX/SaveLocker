<#
.SYNOPSIS
  Rebuilds console-titles.tsv.gz — game serial -> title, from libretro-database's Redump and No-Intro dats (CC BY-SA 4.0).

.DESCRIPTION
  One line per serial: "<system>`t<serial>`t<title>`t<region>`t<languages>". The title is Redump's name without
  its region, language list, revision, disc number or EDC tag; the region ("USA", "Europe, Australia") and the
  languages ("En,Fr,De", empty when Redump lists none) are their own columns — a game's name says which game,
  the region which release, and a USA save and a European one are different saves (a PS2 game reads only the
  save folder of its own serial). A demo, beta or prototype only names a serial no retail disc has. Run it from
  anywhere; it writes next to itself and updates NOTICE.md's source commit.

.PARAMETER Commit
  The libretro-database commit to read (default: master, resolved to its SHA so NOTICE.md pins it).
#>
param([string]$Commit = 'master')

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$sets = @(
    @{ System = 'psx'; Dat = 'Sony - PlayStation' },
    @{ System = 'ps2'; Dat = 'Sony - PlayStation 2' },
    @{ System = 'ps3'; Dat = 'Sony - PlayStation 3' },
    @{ System = 'psp'; Dat = 'Sony - PlayStation Portable' },
    @{ System = 'gc';  Dat = 'Nintendo - GameCube' },
    @{ System = 'wii'; Dat = 'Nintendo - Wii' },
    @{ System = 'n3ds'; Dat = 'Nintendo - Nintendo 3DS'; Dir = 'no-intro' },
    @{ System = 'n3ds'; Dat = 'Nintendo - New Nintendo 3DS'; Dir = 'no-intro' },
    @{ System = 'n3ds'; Dat = 'Nintendo - Nintendo 3DS (Digital)'; Dir = 'no-intro' },
    @{ System = 'xbox360'; Dat = 'Microsoft - Xbox 360' },
    # Digital games after discs: a game on both keeps its disc's name.
    @{ System = 'xbox360'; Dat = 'Microsoft - Xbox 360'; Dir = 'no-intro'; Rank = 2 }
)

$sha = (Invoke-RestMethod "https://api.github.com/repos/libretro/libretro-database/commits/$Commit" -Headers @{ 'User-Agent' = 'SaveLocker' }).sha

# The key a save names its game by: SLUS-21287 (Sony), the four-letter game code (DL-DOL-GALE-USA, RVL-R3ME-USA,
# and a 3DS game's product code CTR-P-AREE, which its ROM's NCCH header carries).
function Get-Keys([string]$system, [string]$serials) {
    foreach ($s in ($serials -split '[,\s]+' | Where-Object { $_ })) {
        if ($system -eq 'gc')      { if ($s -match '^DL-DOL-([A-Z0-9]{4})-') { $Matches[1] } }
        elseif ($system -eq 'wii') { if ($s -match '^RVL-([A-Z0-9]{4})-')    { $Matches[1] } }
        elseif ($system -eq 'n3ds') { if ($s -match '^[A-Z]{3}-[A-Z]-([A-Z0-9]{4})$') { $Matches[1] } }
        # An Xbox 360 save is filed under the title ID, which a disc's serial spells out: MS-2505 is 'M' 'S' and 2505,
        # 4D5309C9 (Forza Horizon, the title ID Xenia saved it under on the maintainer's PC). No-Intro gives it outright.
        elseif ($system -eq 'xbox360') {
            if ($s -match '^TID-([0-9A-F]{8})$') { $Matches[1] }
            elseif ($s -match '^([A-Z]{2})-(\d{1,5})(?!\d)' -and [int]$Matches[2] -le 65535) {
                '{0:X2}{1:X2}{2:X4}' -f [int][char]$Matches[1][0], [int][char]$Matches[1][1], [int]$Matches[2]
            }
        }
        elseif ($s -match '^([A-Z]{4})-?(\d{5})') { "$($Matches[1])-$($Matches[2])" }
    }
}

$languages = '\s*\(([A-Z][a-z](?:-[A-Za-z]+)?(?:,[A-Z][a-z](?:-[A-Za-z]+)?)*)\)'
$dropTags = '\s*\((?:Rev [^)]*|v\d[^)]*|Disc \d+|EDC|Track \d+|eShop|XBLA|XBLIG|Games on Demand)\)'
# Redump's region names: the first parenthesised group made only of these is the release's region.
$regionNames = 'USA|Europe|Japan|World|Asia|Australia|Brazil|Canada|China|France|Germany|Hong Kong|Italy|Korea|' +
    'Netherlands|Spain|Sweden|Taiwan|UK|Russia|Scandinavia|Latin America|Portugal|Poland|Greece|Austria|' +
    'Switzerland|Belgium|Denmark|Finland|Norway|India|Ireland|Mexico|New Zealand|South Africa|Croatia|Turkey|' +
    'Israel|Argentina|United Arab Emirates|Czech|Hungary|Ukraine|Singapore|Thailand|Indonesia|Malaysia|Philippines|Unknown'
$region = "\s*\(((?:$regionNames)(?:, (?:$regionNames))*)\)"
$notRetail = '\((?:Demo|Beta|Proto|Kiosk|Sample|Trial|Taikenban|Preview|Promo|Debug|Not for Resale)[^)]*\)'

$lines = New-Object System.Collections.Generic.List[string]
$best = @{}
foreach ($set in $sets) {
    $dir = if ($set.Dir) { $set.Dir } else { 'redump' }
    $url = "https://raw.githubusercontent.com/libretro/libretro-database/$sha/metadat/$dir/$([Uri]::EscapeDataString($set.Dat)).dat"
    $text = (Invoke-WebRequest $url -UseBasicParsing).Content
    if (-not $best.ContainsKey($set.System)) { $best[$set.System] = @{} }
    $seen = $best[$set.System]
    foreach ($game in [regex]::Matches($text, '(?ms)^game \(\s*$(.*?)^\)')) {
        $body = $game.Groups[1].Value
        $name = [regex]::Match($body, '(?m)^\s*name "(.*)"\s*$').Groups[1].Value
        $serial = [regex]::Match($body, '(?m)^\s*serial "(.*)"\s*$').Groups[1].Value
        # No-Intro's Xbox 360 files are named by title ID and content type: 000D0000 an Arcade game, 00007000 Games on Demand.
        if (-not $serial -and $set.System -eq 'xbox360' -and $body -match 'rom \( name "([0-9A-F]{8})(?:000D0000|00007000)') { $serial = "TID-$($Matches[1])" }
        if (-not $name -or -not $serial) { continue }
        $rank = 10 * $(if ($set.Rank) { $set.Rank } else { 0 }) + 5 * $(if ($name -match $notRetail) { 1 } else { 0 })
        # One Xbox 360 title ID is every region's disc: its USA or World name, then a European one, over a translated title.
        if ($set.System -eq 'xbox360') { $rank += $(if ($name -match '\((?:USA|World)') { 0 } elseif ($name -match '\((?:Europe|UK)') { 1 } else { 2 }) }
        $where = [regex]::Match($name, $region)
        $langs = [regex]::Match($name, $languages)
        $title = $name
        if ($where.Success) { $title = $title.Remove($where.Index, $where.Length) }
        $title = [regex]::Replace([regex]::Replace($title, $languages, ''), $dropTags, '')
        $title = ($title -replace '\s+', ' ').Trim()
        # An Xbox 360 title ID is every region's release, so no region or languages are kept for it.
        $row = if ($set.System -eq 'xbox360') { "$title`t`t" }
               else { "$title`t$(if ($where.Success) { $where.Groups[1].Value })`t$(if ($langs.Success) { $langs.Groups[1].Value })" }
        foreach ($key in (Get-Keys $set.System $serial | Select-Object -Unique)) {
            $old = $seen[$key]
            if ($null -eq $old -or $rank -lt $old.Rank -or
                ($rank -eq $old.Rank -and ($name.Length -lt $old.Name.Length -or
                    ($name.Length -eq $old.Name.Length -and [string]::CompareOrdinal($name, $old.Name) -lt 0)))) {
                $seen[$key] = @{ Rank = $rank; Name = $name; Row = $row }
            }
        }
    }
}
# One system's dats (a 3DS game's cartridge and eShop lists) are merged before its rows are written.
foreach ($set in ($sets | ForEach-Object { $_.System } | Select-Object -Unique)) {
    $seen = $best[$set]
    foreach ($key in ($seen.Keys | Sort-Object -CaseSensitive)) { $lines.Add("$set`t$key`t$($seen[$key].Row)") }
    Write-Host ("{0,-4} {1,6} serials" -f $set, $seen.Count)
}

$out = Join-Path $PSScriptRoot 'console-titles.tsv.gz'
$bytes = (New-Object System.Text.UTF8Encoding $false).GetBytes(($lines -join "`n") + "`n")
$file = [IO.File]::Create($out)
try {
    $gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionLevel]::Optimal)
    $gzip.Write($bytes, 0, $bytes.Length)
    $gzip.Dispose()
} finally { $file.Dispose() }

$notice = Join-Path $PSScriptRoot 'NOTICE.md'
$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText($notice,
    ([IO.File]::ReadAllText($notice, $utf8) -replace '(?<=libretro-database/tree/)[0-9a-f]{40}', $sha), $utf8)
Write-Host "$($lines.Count) titles from libretro-database $sha -> $out"
