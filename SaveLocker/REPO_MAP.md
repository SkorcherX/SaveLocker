# SaveLocker — Repo Map

Static layout. Update only when adding or removing entire modules.

```
SaveLocker/
├── src/
│   ├── Shared/                          # SaveLocker.Shared.csproj
│   │   ├── Contracts.cs                 # Wire DTOs — shared by server + agent
│   │   ├── SaveArchive.cs               # Content hashing + atomic zip restore
│   │   └── ManifestLoader.cs            # Ludusavi manifest downloader + path resolver
│   │
│   ├── Server/                          # SaveLocker.Server.csproj (net9.0, Docker)
│   │   ├── Program.cs                   # ALL minimal-API endpoints + DI wiring
│   │   ├── Data/
│   │   │   ├── AppDbContext.cs          # EF Core context
│   │   │   └── Entities.cs             # Machine, Game, SaveVersion, Lease, ConflictFlag,
│   │   │                               #   AuditLog, AgentCommand, AppSetting, MachineSavePath
│   │   ├── Migrations/                  # EF migrations (InitialSchema + incremental)
│   │   ├── Services/
│   │   │   ├── SyncService.cs           # Core: lease, upload, conflict, prune, resolve
│   │   │   ├── ArchiveStore.cs          # {root}/{gameId}/{versionId}.zip on disk
│   │   │   ├── ArtService.cs            # SteamGridDB fetch + cache to /data/art/
│   │   │   ├── BackupService.cs         # Nightly VACUUM INTO SQLite snapshots + retention
│   │   │   ├── AgentInstallerService.cs # Hosts installer binary for agent auto-update
│   │   │   ├── SettingsService.cs       # DB-backed key/value (DB overrides appsettings)
│   │   │   ├── LeaseSweeperService.cs   # Hourly BackgroundService: clear stale leases
│   │   │   └── Mapping.cs              # Entity → DTO mapping helpers
│   │   ├── Dockerfile                   # Multi-stage: Node (web/) + .NET SDK + aspnet runtime
│   │   ├── appsettings.json             # Storage, Backup, AgentUpdate config sections
│   │   └── openapi.json                 # Committed OpenAPI snapshot; regenerate after API changes
│   │
│   └── Agent/                           # SaveLocker.Agent.csproj (net9.0-windows, WinForms)
│       ├── Program.cs                   # Entry: no args → tray; args → CLI
│       ├── TrayApp.cs                   # Tray icon, menu, engine lifecycle, update checks
│       ├── AgentApiServer.cs            # HttpListener on :5178 — JSON API + agent-ui static files
│       ├── AgentWindow.cs               # WinForms form hosting WebView2 (900×600, DPI-scaled)
│       ├── SyncEngine.cs                # push / pull / pre-launch / post-exit
│       ├── UpdateChecker.cs             # Polls /api/agent/latest, downloads + launches installer
│       ├── CommandPoller.cs             # 20 s poll: reconcile game list + run commands
│       ├── ApiClient.cs                 # Typed HTTP client for server API
│       ├── Detection.cs                 # Ludusavi manifest wrapper
│       ├── GameScanner.cs               # Steam VDF readers + save-root heuristic
│       ├── SteamVdf.cs / SteamTextVdf.cs # Binary + text VDF parsers
│       ├── Watchers.cs                  # Debounced FileSystemWatcher + ProcessWatcher
│       ├── AgentConfig.cs               # JSON config at %PROGRAMDATA%\SaveLocker\config.json
│       ├── AutoStart.cs                 # HKCU Run-key toggle ("Start with Windows")
│       ├── OfflineQueue.cs              # JSON retry queue: …\SaveLocker\offline-queue.json
│       ├── OfflineQueueDrainer.cs       # 30 s timer drains queue when server reachable
│       └── AppResources.cs             # Embedded icon + asset loader
│
├── web/                                 # React admin dashboard
│   │                                   # Stack: Vite 8, React 19, TypeScript, Tailwind v4
│   ├── src/
│   │   ├── api.ts                       # Typed fetch client (all server endpoints)
│   │   ├── api-types.ts                 # Generated from /openapi/v1.json → npm run gen:api
│   │   ├── types.ts                     # Thin aliases over api-types.ts
│   │   └── components/
│   │       ├── NavBar.tsx               # Logo, Games/Config/Audit Log tabs, Connect/Refresh
│   │       ├── GamesSidebar.tsx         # 220 px left sidebar: cover art, name, badges
│   │       ├── GamesView.tsx            # Sidebar + detail panel layout
│   │       ├── GameDetail.tsx           # Game card, Machines, Commands, Versions, save paths
│   │       ├── ConfigView.tsx           # SteamGridDB, Machines/API keys, Agent Updates card
│   │       └── AuditView.tsx            # Audit log table with color-coded action badges
│   └── vite.config.ts                  # Proxy /api + /art → :5179; host 0.0.0.0 (LAN)
│
├── agent-ui/                            # React agent tray UI
│   │                                   # Stack: Vite, React 19, TypeScript (hand-typed, not generated)
│   └── src/
│       ├── App.tsx                      # Root, sidebar nav, 10 s state poll, live version string
│       ├── types.ts                     # AgentState, Candidate, TrackedGame (hand-written)
│       └── views/
│           ├── OverviewView.tsx
│           ├── AddGamesView.tsx
│           └── SettingsView.tsx
│
├── installer/
│   ├── SaveLocker.iss                   # Inno Setup 6: machine-wide, UAC, uninstall reverts all
│   └── build-installer.ps1             # dotnet publish + ISCC → installer/dist/SaveLocker-Agent-Setup-{ver}.exe
│
├── tests/
│   └── run-agent-tests.ps1             # Integration tests (server must be on :5179)
│
├── .github/workflows/
│   ├── ci.yml                           # PR + main push: build-dotnet, build-web, build-agent-ui
│   ├── docker-publish.yml               # main push → build + push ghcr.io/skorcherx/savelocker:latest
│   └── release.yml                      # v* tag → windows-latest build → installer → GitHub Release
│
├── SaveLocker.sln
└── docker-compose.unraid.yml           # Server container + Watchtower on unRAID
```

## Auth model
- **Agent routes** — `X-Api-Key: <machine key>` (issued at registration)
- **Admin routes** — `X-Admin-Password: <password>` (set in dashboard; open if unset)

## Data flow
```
Agent (push)  →  POST /api/games/{id}/upload  →  SyncService  →  ArchiveStore + SQLite
Agent (pull)  →  GET  /api/games/{id}/download →  SyncService  →  ArchiveStore → zip stream
Dashboard     →  GET  /api/overview            →  SyncService  →  GameStateDto[]
```

## Key config paths (runtime)
| Item | Path |
|------|------|
| Agent config | `%PROGRAMDATA%\SaveLocker\config.json` |
| Agent log | `%PROGRAMDATA%\SaveLocker\agent.log` |
| Offline queue | `%PROGRAMDATA%\SaveLocker\offline-queue.json` |
| Server DB (dev) | `src/Server/localstate/savelocker.db` |
| Server DB (Docker) | `/data/savelocker.db` (or `localgamesync.db` on existing deployments) |
| Archives (Docker) | `/data/archives/` |
| Art cache (Docker) | `/data/art/` |
| Backups (Docker) | `/data/backups/` |
| Installer store | `/data/agent-installer/` |
