# Build and Run

Full deployment reference also in `README.md`.

## Prereqs
- .NET 9 SDK. If `dotnet` isn't found in an open shell: `$env:Path = "$env:ProgramFiles\dotnet;" + $env:Path` (or open a new shell).
- Node.js 22 (for React dashboard dev server and agent UI builds).
- Inno Setup 6 (for installer builds): `winget install JRSoftware.InnoSetup`.

## Build

```sh
# Full solution
dotnet build SaveLocker.sln

# Force server recompile (use this when server edits seem ignored — see Gotchas.md)
dotnet build src/Server/SaveLocker.Server.csproj --no-incremental
```

**Stop the running agent/server before building** — they lock the DLLs.

## Run server (local dev)

```sh
cd src/Server
dotnet run
# API: http://localhost:5179   Health: /health   Swagger: /swagger
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

Server must be running for API calls to work. Two Vite processes will claim ports 5173 + 5174 — kill duplicates with `Get-Process node | Stop-Process`.

## Run agent (local dev)

```sh
dotnet build src/Agent/SaveLocker.Agent.csproj --no-incremental
# Then launch the built exe, or run via dotnet for console output:
dotnet src/Agent/bin/Debug/net9.0-windows/SaveLocker.Agent.dll
```

Agent UI served at http://localhost:5178.

## Deploy on unRAID (Docker / CI)

`git push` → GitHub Actions builds and pushes `ghcr.io/skorcherx/savelocker:latest`. To deploy on unRAID:

```sh
docker compose pull && docker compose up -d
```

Manual one-off build (no Compose):
```sh
docker build -t savelocker -f src/Server/Dockerfile .
docker run -d --name savelocker-server -p 5080:8080 \
  -v /mnt/user/appdata/savelocker:/data savelocker
```

Dashboard: `http://unraid-ip:5080`.

| Setting | Env var | Default (Docker) |
|---|---|---|
| SQLite path | `Storage__DbPath` | `/data/savelocker.db` |
| Archive root | `Storage__ArchiveRoot` | `/data/archives` |
| Art root | `Storage__ArtRoot` | `/data/art` |
| Backup root | `Storage__BackupRoot` | `/data/backups` |
| Installer root | `Storage__AgentInstallerRoot` | `data/agent-installer/` |
| Versions kept/game | `Storage__RetainVersionsPerGame` | `10` |

> **Existing Docker deployments** may have `/data/localgamesync.db` from before the rename. Either rename the file on the unRAID share or set `Storage__DbPath=/data/localgamesync.db`.

## Build agent installer

```sh
.\installer\build-installer.ps1
# Output: installer/dist/SaveLocker-Agent-Setup-{version}.exe (~47 MB, self-contained)
```

Stop the running agent first (holds the `SaveLocker.Agent` AppMutex). Version is derived by MinVer from the nearest git tag — push a `v*` tag to trigger the GitHub release CI instead of building locally for a real release.

## Tests

```sh
.\tests\run-agent-tests.ps1   # server must be on :5179
```

Scratch state written to `.verify/` (git-ignored).

## Regenerate OpenAPI types (web dashboard)

```sh
# 1. Start the server: cd src/Server && dotnet run
# 2. In another terminal:
cd web && npm run gen:api
# Regenerates web/src/api-types.ts from http://localhost:5179/openapi/v1.json
```

Commit the updated `src/Server/openapi.json` snapshot alongside any API changes.
