# Build and Run

Back to [[Home]]. Full version in repo `README.md`.

## Prereqs
- .NET 9 SDK. If `dotnet` isn't found in a shell:
  `$env:Path = "$env:ProgramFiles\dotnet;" + $env:Path`
- Node.js (for the React dashboard dev server and agent UI builds)

## Build
```sh
dotnet build LocalGameSync.sln
# If server edits seem ignored, force it (see [[Gotchas]]):
dotnet build src/Server/LocalGameSync.Server.csproj --no-incremental
```

## Run server (local dev)
```sh
cd src/Server
dotnet run
# API: http://localhost:5179   Health: /health
```
Dev state under `src/Server/localstate/` (db + archives). Deterministic test run:
```sh
$env:ASPNETCORE_URLS="http://localhost:5179"
$env:Storage__DbPath="...\some\path\db.sqlite"
$env:Storage__ArchiveRoot="...\some\path\archives"
dotnet run --no-build --no-launch-profile
```

## Run React dashboard (local dev)
```sh
cd web
npm run dev
# Dashboard: http://localhost:5173 (proxies /api + /art to :5179)
# LAN access: http://192.168.68.58:5173 (Vite bound to 0.0.0.0)
```
Server must be running for API calls to work. Two Vite processes will claim ports 5173 + 5174 — kill duplicates with `Get-Process node` + `Stop-Process`.

## Deploy on unRAID (Docker / CI)
`git push` → GitHub Actions builds the image → Watchtower auto-deploys on unRAID.
Manual build:
```sh
docker build -t savelocker -f src/Server/Dockerfile .
docker run -d --name savelocker-server -p 5080:8080 \
  -v /mnt/user/appdata/savelocker:/data savelocker
```
Dashboard: `http://unraid-ip:5080`.

| Setting | Env var | Default (Docker) |
|---|---|---|
| SQLite path | `Storage__DbPath` | `/data/localgamesync.db` |
| Archive root | `Storage__ArchiveRoot` | `/data/archives` |
| Versions kept/game | `Storage__RetainVersionsPerGame` | `10` |

> The default Docker DB is still `localgamesync.db` (explicit config in `appsettings.json`).
> Fresh local installs use `savelocker.db`; the rename shim auto-migrates the old name on first run.

## Build agent installer
```sh
.\installer\build-installer.ps1
# Output: installer/dist/SaveLocker-Agent-Setup-0.1.0.exe (~47 MB, self-contained)
```
Prereqs: Inno Setup 6 (`winget install JRSoftware.InnoSetup`). Stop the running agent first (holds the AppMutex).

## Agent (CLI + tray)
No args → tray app. With args → CLI. `--config <path>` overrides config location.
```sh
LocalGameSync.Agent register --name "GamingPC"
LocalGameSync.Agent add-game --name "Celeste" --manifest "Celeste"
LocalGameSync.Agent add-game --name "MyGame" --dir "C:\Saves\MyGame" --proc mygame
LocalGameSync.Agent pull all
LocalGameSync.Agent push all
LocalGameSync.Agent status
LocalGameSync.Agent resolve --manifest "Celeste"   # show detected save dir
```
Config: `%PROGRAMDATA%\SaveLocker\config.json`. Logs: `%PROGRAMDATA%\SaveLocker\agent.log`.

> Run the agent CLI via `dotnet <path>\LocalGameSync.Agent.dll <args>` so console
> output attaches (the exe is a WinExe / GUI subsystem).

## Tests
`.verify/run-agent-tests.ps1` (server must be running on :5179). See [[Progress]].
