# Architecture

```
  ┌─────────────┐         ┌──────────────────────────┐         ┌─────────────┐
  │  Gaming PC  │         │   unRAID (Docker)         │         │   Laptop    │
  │  Agent      │◄──────► │  Server API + Dashboard   │ ◄─────► │  Agent      │
  │ (tray .exe) │  HTTPS  │  + SQLite + archive store │  HTTPS  │ (tray .exe) │
  └─────────────┘         └──────────────────────────┘         └─────────────┘
                                      ▲
                                      │ CloudFlare Tunnel (remote access)
```

Hub-and-spoke (not peer-to-peer). See `Decisions.md` for why unRAID is the keystone.

## Components

### Shared (`src/Shared`)
- `Contracts.cs` — wire DTOs (records) used by both server and agent.
- `SaveArchive.cs` — deterministic content hashing of a save dir + zip create / **atomic restore** (staging dir → swap → rollback on failure).
- `ManifestLoader.cs` — downloads/caches the Ludusavi manifest (YAML), resolves a game's save dirs. `PathResolver.cs` — expands Windows path tokens (`<winAppData>`, `<winLocalAppDataLow>`, `<winSavedGames>`, `<winDocuments>`, …) and trims at the first wildcard to a concrete directory.

### Server (`src/Server`)
- `Program.cs` — minimal-API endpoints + DI. Schema via `db.Database.Migrate()` on startup; a bootstrap shim pre-stamps `InitialSchema` on existing DBs so `Migrate()` skips it. Two auth filter groups: `ApiKeyFilter` (agent routes, `X-Api-Key`) and `AdminPasswordFilter` (dashboard routes, `X-Admin-Password` when a password is set).
- `Data/Entities.cs`, `Data/AppDbContext.cs` — EF Core model. ⚠️ See `Gotchas.md` about the `Data/` vs `data/` folder collision.
- `Services/SyncService.cs` — all orchestration logic (registration, leasing, conflict-aware upload, download, resolve, rollback, **version pruning**).
- `Services/ArchiveStore.cs` — archive files on disk: `{root}/{gameId}/{versionId}.zip`.
- `Services/BackupService.cs` — nightly SQLite snapshots (`BackupScheduler` + `VACUUM INTO`, WAL-safe) with retention; the DB is the version graph, so it has its own on-box backup (`/data/backups`). See `API Reference.md`.
- `Services/AgentInstallerService.cs` — stores the agent installer binary on disk (`Storage:AgentInstallerRoot`, default `data/agent-installer/`) with a sidecar `installer-info.json`. `GET /api/agent/latest` checks here first; admin endpoints let the dashboard upload/delete/stream the installer. See **Agent auto-update** below.
- `Services/AgentInstallerPollerService.cs` — optional `BackgroundService` that checks the configured GitHub release at the dashboard-managed `AgentUpdate:AutoFetchHours` interval and refreshes the hosted installer only when a newer release is available.
- `Services/LeaseSweeperService.cs` — `BackgroundService` that runs hourly via `IServiceScopeFactory` and sweeps leases where `ExpiresAt < UtcNow`.
- `Services/SettingsService.cs` — DB-backed key/value store (`AppSetting` entity). DB value overrides `IConfiguration` (appsettings/env); used for SteamGridDB key, admin password hash, and installer auto-fetch interval.
- **OpenAPI** — `AddOpenApi()`/`MapOpenApi()` serve `/openapi/v1.json`; Swagger UI at `/swagger`. The web dashboard's types are generated from this doc (`openapi-typescript` → `web/src/api-types.ts`).
- **Dashboard** — React `web/` SPA. Docker `web` build stage copies `dist/` into `src/Server/wwwroot/`. Its `types.ts` is generated from the server's OpenAPI doc, so it can't drift from the DTOs.

### Agent (`src/Agent`)
- `Program.cs` — entry point: no args → tray (`TrayApp.cs`); args → CLI commands.
- `TrayApp.cs` — tray icon + menu, engine/poller lifecycle, update-check startup + 24 h timer.
- `AgentApiServer.cs` — `HttpListener` on port 5178. Serves static `agent-ui/dist/` files (SPA fallback) and JSON API routes for the React agent UI.
- `AgentWindow.cs` — WinForms form hosting WebView2. Hides instead of closing (re-showable from tray). DPI-scaled `ClientSize` (see `Gotchas.md`).
- `SyncEngine.cs` — push (archive→hash→upload), pull (download→restore), pre-launch (lease + pull), post-exit (push + release).
- `UpdateChecker.cs` — polls `GET /api/agent/latest`, compares with `Assembly.GetEntryAssembly().GetName().Version`, respects `AgentConfig.SkipVersion`. Downloads installer to `%TEMP%`, launches with `/SILENT /FORCECLOSEAPPLICATIONS /NORESTART`.
- `CommandPoller.cs` — 20 s poll: (1) reconcile game list (adopt new server games, drop deleted ones); (2) run queued pull/push/sync/scan commands and report results.
- `ApiClient.cs` — typed HTTP client. `Detection.cs` — Ludusavi manifest wrapper.
- `Watchers.cs` — debounced `FileSystemWatcher` + process poll (`ProcessWatcher`).
- `SaveSettler.cs` — settle gate for **automatic** pushes (process-exit, folder-watch). A process exit does not mean the save is on disk, so before archiving it waits for the save folder to hold a stable fingerprint (file set + sizes + write times) with nothing open for writing, for `SettleQuietSeconds` (default 10, set in agent Settings → Sync Safety), capped by `SettleMaxWaitSeconds` (120) after which the push proceeds anyway. Manual/CLI pushes skip the gate. `PushAsync` serialises per game, since the folder watcher can fire while the exit push is still settling.
- `OfflineQueue.cs` / `OfflineQueueDrainer.cs` — `PushAsync` enqueues to `%PROGRAMDATA%\SaveLocker\offline-queue.json` on `HttpRequestException`; 30 s drain timer retries when the server is reachable.
- `AgentConfig.cs` — JSON config at `%PROGRAMDATA%\SaveLocker\config.json`. Tracks `SkipVersion` + `LastUpdateCheck` for update-check cooldown.

## Agent auto-update
Versioning is **MinVer** (git-tag-driven). Tag `v0.1.0` → that build stamps `0.1.0` in all assembly version fields. Between tags → `0.1.0-alpha.N+hash`.

**Release flow:**
1. `git tag v0.2.0 && git push origin v0.2.0`
2. `release.yml` builds on `windows-latest`, runs `build-installer.ps1`, creates a GitHub Release with the exe attached.
3. Admin uploads the installer in dashboard → Configuration → Agent Updates (`POST /api/admin/agent-installer`).
4. Connected agents check within 24 h (or via tray "Check for Updates"); offered update via balloon → confirm dialog (Update Now / Skip / Remind Later).
5. Agent downloads from `GET /api/agent/installer/download`, launches silently, exits.

## Conflict model (the safety mechanism)
On upload the agent sends the version it last knew (`parent`):
- `parent == server head` → **fast-forward** (new head).
- Incoming content hash `== head` → **no-op**.
- Head moved on → **conflict** recorded; head untouched; admin resolves.

Leases prevent most conflicts up front; hashing/lineage is the safety net.

## Data model (SQLite via EF Core)
EF-managed entities: `Machine`, `Game` (holds `HeadVersionId`), `SaveVersion` (parent chain + `ContentHash`), `Lease` (one per game, unique index), `ConflictFlag`, `AuditLog`, `AgentCommand`, `AppSetting` (key/value store), `MachineSavePath` (composite key `(MachineId, GameId)` → a machine's stored save folder for a game).

> **"Latest" = the head.** `Game.HeadVersionId` is the authoritative version agents pull; the dashboard labels it **Latest**; the admin action to set it is **"Set as Latest"**.

## Retention
After a successful upload, `SyncService.PruneVersionsAsync` keeps the newest `Storage:RetainVersionsPerGame` (default 10) per game, deleting older archives. Never prunes the head or versions referenced by an open conflict. Per-game override: `Game.RetainVersions` (nullable — null uses global default; 0 = unlimited).
