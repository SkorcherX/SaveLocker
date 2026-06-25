# Build and Run

Back to [[Home]]. Full version in repo `README.md`.

## Prereqs
- .NET 9 SDK. If `dotnet` isn't found in a shell:
  `$env:Path = "$env:ProgramFiles\dotnet;" + $env:Path`

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
# Dashboard: http://localhost:5179   Health: /health
```
Dev state under `src/Server/localstate/` (db + archives). Deterministic test run:
```sh
$env:ASPNETCORE_URLS="http://localhost:5179"
$env:Storage__DbPath="...\some\path\db.sqlite"
$env:Storage__ArchiveRoot="...\some\path\archives"
dotnet run --no-build --no-launch-profile
```

## Deploy on unRAID (Docker)
```sh
docker build -t localgamesync -f src/Server/Dockerfile .
docker run -d --name localgamesync -p 8080:8080 \
  -v /mnt/user/appdata/localgamesync:/data localgamesync
```
Point a CloudFlare Tunnel public hostname at `http://localhost:8080`; put
CloudFlare Access in front for auth.

| Setting | Env var | Default |
|---|---|---|
| SQLite path | `Storage__DbPath` | `/data/localgamesync.db` |
| Archive root | `Storage__ArchiveRoot` | `/data/archives` |
| Versions kept/game | `Storage__RetainVersionsPerGame` | `10` |

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
Config: `%PROGRAMDATA%\LocalGameSync\config.json`. Set `ServerUrl` to the tunnel
hostname so the laptop syncs from anywhere.

> Run the agent CLI via `dotnet <path>\LocalGameSync.Agent.dll <args>` so console
> output attaches (the exe is a WinExe / GUI subsystem).

## Tests
`.verify/run-agent-tests.ps1` (server must be running on :5179). See [[Progress]].
