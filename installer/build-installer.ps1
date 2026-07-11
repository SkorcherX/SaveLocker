# Builds the SaveLocker agent installer end to end:
#   1. builds the React agent UI (npm run build in agent-ui/)
#   2. publishes the agent self-contained (single exe, no .NET runtime needed), then
#   3. compiles installer\SaveLocker.iss with Inno Setup's ISCC.exe.
# Output: installer\dist\SaveLocker-Agent-Setup-<version>.exe
#
# Prerequisites: .NET 9 SDK, Node.js, Inno Setup 6 (winget install JRSoftware.InnoSetup).
# The Release publish output is separate from the dev Debug build, so a running
# (Debug) agent does not need to be stopped for this.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$agentProj = Join-Path $repo 'src\Agent\SaveLocker.Agent.csproj'
$agentUi   = Join-Path $repo 'agent-ui'

Write-Host '== Building SaveLocker agent UI (React) ==' -ForegroundColor Cyan
Push-Location $agentUi
try {
    & npm install --prefer-offline 2>&1 | Out-Null
    & npm run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
}

Write-Host '== Publishing agent (self-contained, single file) ==' -ForegroundColor Cyan
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
& $dotnet publish $agentProj -p:PublishProfile=win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed ($LASTEXITCODE)" }

Write-Host '== Compiling installer (Inno Setup) ==' -ForegroundColor Cyan
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) { throw 'ISCC.exe not found. Install Inno Setup 6 (winget install JRSoftware.InnoSetup).' }

$publishExe = Join-Path $repo 'src\Agent\bin\Release\net9.0-windows\win-x64\publish\SaveLocker.Agent.exe'
$appVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishExe).FileVersion
Write-Host "Agent version: $appVersion" -ForegroundColor Cyan

& $iscc "/DAppVersion=$appVersion" (Join-Path $PSScriptRoot 'SaveLocker.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Write-Host '== Done ==' -ForegroundColor Green
Get-ChildItem (Join-Path $PSScriptRoot 'dist') -Filter *.exe | Select-Object Name, Length, LastWriteTime
