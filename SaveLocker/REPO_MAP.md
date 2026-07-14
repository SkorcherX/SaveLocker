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
│   ├── Server/                          # SaveLocker.Server.csproj (net10.0, Docker)
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
│   ├── Agent.Core/                      # SaveLocker.Agent.Core.csproj (net10.0 — NO WinForms)
│   │   │                               # The platform-neutral sync brain. Namespace stays
│   │   │                               # SaveLocker.Agent.*; the Linux agent will reuse this.
│   │   ├── SyncEngine.cs                # push / pull / pre-launch / post-exit
│   │   ├── SaveSettler.cs               # Settle gate: wait for the game to finish writing
│   │   ├── ApiClient.cs                 # Typed HTTP client for server API
│   │   ├── CommandPoller.cs             # 20 s poll: reconcile game list + run commands
│   │   ├── AgentApiServer.cs            # HttpListener on :5178 — JSON API + agent-ui static files
│   │   ├── AgentConfig.cs               # JSON config at %PROGRAMDATA%\SaveLocker\config.json
│   │   ├── Detection.cs                 # Ludusavi manifest wrapper
│   │   ├── UpdateChecker.cs             # Polls /api/agent/latest, downloads installer
│   │   ├── OfflineQueue.cs              # JSON retry queue: …\SaveLocker\offline-queue.json
│   │   ├── OfflineQueueDrainer.cs       # 30 s timer drains queue when server reachable
│   │   ├── AgentLogger.cs               # Rolling agent.log
│   │   ├── AgentCli.cs                  # Shared one-shot commands (register/push/pull/status/…)
│   │   ├── CliArgs.cs                   # Minimal command-line parser
│   │   ├── Enroller.cs                  # Candidate → server game + tracked game
│   │   ├── FileLockProbe.cs             # "Is anyone still writing?" — FileShare (Win) / /proc (Linux)
│   │   ├── SteamVdf.cs                  # Binary VDF parser (pure)
│   │   ├── SteamShortcuts.cs            # shortcuts.vdf reader + the signed→unsigned AppID trap
│   │   ├── Watchers.cs                  # Debounced FileSystemWatcher + ProcessWatcher
│   │   ├── ScanCandidate.cs             # Discovery DTO (scanning itself is platform-specific)
│   │   └── Platform.cs                  # IAutoStart, IGameScanner — impls injected by the host
│   │
│   ├── Agent/                           # SaveLocker.Agent.csproj (net10.0-windows, WinForms)
│   │   │                               # Windows host: UI + platform impls. → Agent.Core
│   │   ├── Program.cs                   # Entry: no args → tray; args → AgentCli
│   │   ├── TrayApp.cs                   # Tray icon, menu, engine lifecycle; injects the Windows impls
│   │   ├── AgentWindow.cs               # WinForms form hosting WebView2 (900×600, DPI-scaled)
│   │   ├── GameScanner.cs               # IGameScanner: shortcuts + Steam libraries + save-root heuristic
│   │   ├── AutoStart.cs                 # IAutoStart: HKCU Run-key toggle ("Start with Windows")
│   │   ├── FolderPicker.cs              # WinForms FolderBrowserDialog on an STA thread
│   │   ├── SteamTextVdf.cs              # Text VDF parser (libraryfolders.vdf, *.acf)
│   │   └── AppResources.cs             # Embedded icon + asset loader
│   │
│   └── Agent.Linux/                     # SaveLocker.Agent.Linux.csproj → binary `savelocker`
│       │                               # Headless Proton agent (net10.0). No tray, no toast:
│       │                               # the daemon serves the same React UI on :5178.
│       ├── Program.cs                   # daemon / run / doctor / autostart, else → AgentCli
│       ├── Daemon.cs                    # Headless host: API server + poller + drainer + watchers
│       ├── ProtonRun.cs                 # `savelocker run -- %command%` Steam launch wrapper
│       ├── LinuxGameScanner.cs          # IGameScanner: shortcuts.vdf; in-prefix + portable saves
│       ├── SteamRoots.cs                # Native + Flatpak Steam roots; compatdata lookup
│       ├── Doctor.cs                    # Diagnoses the whole chain (the only UI a Deck has)
│       └── SystemdAutoStart.cs          # IAutoStart: systemd --user unit
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
├── packaging/linux/
│   ├── build-linux.sh                   # Self-contained publish → tarball (build on OLDEST glibc)
│   ├── install.sh                       # Installs to ~/.local/share/SaveLocker — never /usr
│   └── savelocker.service               # systemd --user unit
│
├── tests/
│   ├── run-agent-tests.ps1             # 10 integration checks. Runs on BOTH OSes: PowerShell Core
│   │                                   #   runs on Linux, so the same script drives the Windows
│   │                                   #   agent and the Linux agent. Needs a server on :5179.
│   ├── verify-password-compat.ps1      # Builds a server from an older git ref, has it hash an
│   │                                   #   admin password, then asserts the CURRENT code still
│   │                                   #   verifies it (guards the PBKDF2 on-disk `v1:` format)
│   ├── cross-os/crossos.ps1            # THE cross-OS round-trip: -Leg author|roundtrip|confirm.
│   │                                   #   One leg per OS; CI chains them by handing the SERVER'S
│   │                                   #   OWN STATE (SQLite DB + archive store) between runners as
│   │                                   #   an artifact — runners cannot share a network.
│   └── linux/                          # Fake-game harness — no Steam/Proton/GPU/Deck needed
│       ├── run-linux-tests.sh          # 27 checks; starts its own server, fake HOME
│       ├── make-fixtures.py            # Builds compatdata tree + binary shortcuts.vdf
│       ├── slow-game.sh                # Game that flushes after exit (settle gate + lock probe)
│       └── manifest.yaml               # Fixture Ludusavi manifest
│
├── .github/workflows/
│   ├── ci.yml                           # PR + main push: build-dotnet, build-web, build-agent-ui
│   ├── docker-publish.yml               # main push → build + push ghcr.io/skorcherx/savelocker:latest
│   └── release.yml                      # v* tag → windows-latest build → installer → GitHub Release
│
├── SaveLocker.sln
├── global.json                         # Pins the .NET SDK (10.0.x). The Dockerfile COPIES this in,
│                                       #   so bump it and the sdk:/aspnet: image tags TOGETHER.
└── docker-compose.unraid.yml           # Server container for unRAID deployment
```

## Runtime / toolchain
- **net10.0** everywhere (`net10.0-windows` for the WinForms tray). .NET 10 is **LTS**; net9 was STS
  and goes out of support 10 Nov 2026. See `Decisions.md → Runtime: .NET 10 LTS`.
- EF Core tracks the framework at **10.0.x**.
- **`SQLitePCLRaw.bundle_e_sqlite3` is pinned to 3.x on purpose** in `SaveLocker.Server.csproj` —
  EF Core otherwise resolves 2.1.11, whose bundled SQLite carries **CVE-2025-6965**. Removing the pin
  silently reintroduces it. See `Gotchas.md`.

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
