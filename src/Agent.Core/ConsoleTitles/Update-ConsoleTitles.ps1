<#
.SYNOPSIS
  Rebuilds console-titles.tsv.gz — game serial -> title, from libretro-database's Redump dats (CC BY-SA 4.0).

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
    @{ System = 'gc';  Dat = 'Nintendo - GameCube' },
    @{ System = 'wii'; Dat = 'Nintendo - Wii' }
)

$sha = (Invoke-RestMethod "https://api.github.com/repos/libretro/libretro-database/commits/$Commit" -Headers @{ 'User-Agent' = 'SaveLocker' }).sha

# The key a save names its game by: SLUS-21287 (Sony), the four-letter game code (DL-DOL-GALE-USA, RVL-R3ME-USA).
function Get-Keys([string]$system, [string]$serials) {
    foreach ($s in ($serials -split '[,\s]+' | Where-Object { $_ })) {
        if ($system -eq 'gc')      { if ($s -match '^DL-DOL-([A-Z0-9]{4})-') { $Matches[1] } }
        elseif ($system -eq 'wii') { if ($s -match '^RVL-([A-Z0-9]{4})-')    { $Matches[1] } }
        elseif ($s -match '^([A-Z]{4})-?(\d{5})') { "$($Matches[1])-$($Matches[2])" }
    }
}

$languages = '\s*\(([A-Z][a-z](?:-[A-Za-z]+)?(?:,[A-Z][a-z](?:-[A-Za-z]+)?)*)\)'
$dropTags = '\s*\((?:Rev [^)]*|v\d[^)]*|Disc \d+|EDC|Track \d+)\)'
# Redump's region names: the first parenthesised group made only of these is the release's region.
$regionNames = 'USA|Europe|Japan|World|Asia|Australia|Brazil|Canada|China|France|Germany|Hong Kong|Italy|Korea|' +
    'Netherlands|Spain|Sweden|Taiwan|UK|Russia|Scandinavia|Latin America|Portugal|Poland|Greece|Austria|' +
    'Switzerland|Belgium|Denmark|Finland|Norway|India|Ireland|Mexico|New Zealand|South Africa|Croatia|Turkey|' +
    'Israel|Argentina|United Arab Emirates|Czech|Hungary|Ukraine|Singapore|Thailand|Indonesia|Malaysia|Philippines|Unknown'
$region = "\s*\(((?:$regionNames)(?:, (?:$regionNames))*)\)"
$notRetail = '\((?:Demo|Beta|Proto|Kiosk|Sample|Trial|Taikenban|Preview|Promo|Debug|Not for Resale)[^)]*\)'

$lines = New-Object System.Collections.Generic.List[string]
foreach ($set in $sets) {
    $url = "https://raw.githubusercontent.com/libretro/libretro-database/$sha/metadat/redump/$([Uri]::EscapeDataString($set.Dat)).dat"
    $text = (Invoke-WebRequest $url -UseBasicParsing).Content
    $best = @{}
    foreach ($game in [regex]::Matches($text, '(?ms)^game \(\s*$(.*?)^\)')) {
        $body = $game.Groups[1].Value
        $name = [regex]::Match($body, '(?m)^\s*name "(.*)"\s*$').Groups[1].Value
        $serial = [regex]::Match($body, '(?m)^\s*serial "(.*)"\s*$').Groups[1].Value
        if (-not $name -or -not $serial) { continue }
        $rank = if ($name -match $notRetail) { 1 } else { 0 }
        $where = [regex]::Match($name, $region)
        $langs = [regex]::Match($name, $languages)
        $title = $name
        if ($where.Success) { $title = $title.Remove($where.Index, $where.Length) }
        $title = [regex]::Replace([regex]::Replace($title, $languages, ''), $dropTags, '')
        $title = ($title -replace '\s+', ' ').Trim()
        $row = "$title`t$(if ($where.Success) { $where.Groups[1].Value })`t$(if ($langs.Success) { $langs.Groups[1].Value })"
        foreach ($key in (Get-Keys $set.System $serial | Select-Object -Unique)) {
            $old = $best[$key]
            if ($null -eq $old -or $rank -lt $old.Rank -or
                ($rank -eq $old.Rank -and ($name.Length -lt $old.Name.Length -or
                    ($name.Length -eq $old.Name.Length -and [string]::CompareOrdinal($name, $old.Name) -lt 0)))) {
                $best[$key] = @{ Rank = $rank; Name = $name; Row = $row }
            }
        }
    }
    foreach ($key in ($best.Keys | Sort-Object -CaseSensitive)) { $lines.Add("$($set.System)`t$key`t$($best[$key].Row)") }
    Write-Host ("{0,-4} {1,6} serials" -f $set.System, $best.Count)
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
