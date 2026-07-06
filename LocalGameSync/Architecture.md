# Architecture

Back to [[Home]].

```
  ┌─────────────┐         ┌──────────────────────────┐         ┌─────────────┐
  │  Gaming PC  │         │   unRAID (Docker)         │         │   Laptop    │
  │  Agent      │◄──────► │  Server API + Dashboard   │ ◄─────► │  Agent      │
  │ (tray .exe) │  HTTPS  │  + SQLite + archive store │  HTTPS  │ (tray .exe) │
  └─────────────┘         └──────────────────────────┘         └─────────────┘
                                      ▲
                                      │ CloudFlare Tunnel (remote laptop)
```

Hub-and-spoke (not peer-to-peer). See [[Decisions]] for why unRAID is the keystone.

## Components

### Shared (`src/Shared`)
- `Contracts.cs` — wire DTOs (records) used by both server and agent.
- `SaveArchive.cs` — deterministic content hashing of a save dir + zip create /
  **atomic restore** (staging dir → swap → rollback on failure).
- `ManifestLoader.cs` — downloads/caches the Ludusavi manifest (YAML), resolves a
  game's save dirs. `PathResolver.cs` — expands Windows path tokens
  (`<winAppData>`, `<winLocalAppDataLow>`, `<winSavedGames>`, `<winDocuments>`, …)
  and trims at the first wildcard to a concrete directory.

### Server (`src/Server`)
- `Program.cs` — minimal-API endpoints + DI. Schema via `db.Database.Migrate()` on
  startup; a bootstrap shim pre-stamps `InitialSchema` on existing DBs so `Migrate()`
  skips it. Two auth filter groups: `ApiKeyFilter` (agent routes, `X-Api-Key`) and
  `AdminPasswordFilter` (dashboard routes, `X-Admin-Password` when a password is set).
- `Data/Entities.cs`, `Data/AppDbContext.cs` — EF Core model. ⚠️ See [[Gotchas]]
  about the `Data/` vs `data/` folder collision.
- `Services/SyncService.cs` — all orchestration logic (registration, leasing,
  conflict-aware upload, download, resolve, rollback, **version pruning**).
- `Services/ArchiveStore.cs` — archive files on disk: `{root}/{gameId}/{versionId}.zip`.
- Dashboard — React `web/` SPA; Docker `web` build stage copies `dist/` into
  `src/Server/wwwroot/` so ASP.NET serves it as static files.

### Agent (`src/Agent`)
- `Program.cs` — entry point: no args → tray (`TrayApp.cs`); args → CLI commands.
- `SyncEngine.cs` — push (archive→hash→upload), pull (download→restore),
  pre-launch (lease + pull), post-exit (push + release).
- `ApiClient.cs` — typed HTTP client. `Detection.cs` — manifest wrapper.
- `Watchers.cs` — debounced `FileSystemWatcher` + process poll (`ProcessWatcher`).
- `AgentConfig.cs` — JSON config at `%PROGRAMDATA%\LocalGameSync\config.json`.

## Conflict model (the safety mechanism)
On upload the agent sends the version it last knew (`parent`):
- `parent == server head` → **fast-forward** (new head).
- incoming content hash `== head` → **no-op**.
- head moved on → **conflict** recorded; head untouched; admin resolves.

Leases prevent most conflicts up front; hashing/lineage is the safety net.

## Data model (SQLite)
EF-managed: `Machine`, `Game` (holds `HeadVersionId`), `SaveVersion` (parent chain +
`ContentHash`), `Lease` (one per game, unique index), `ConflictFlag`, `AuditLog`,
`AgentCommand`, `AppSetting` (key/value store for server settings), `MachineSavePath`
(composite key `(MachineId, GameId)` → a machine's stored save folder for a game).

`MachineSavePath` was folded into EF (entity + `DbSet` + `AddMachineSavePaths`
migration); on existing deployments the migration is stamped as applied at startup
when the table already exists — mirroring the RetainVersions bootstrap-stamp — so the
old raw `CREATE TABLE` table is adopted rather than recreated.

> **"Latest" = the head.** `Game.HeadVersionId` is the authoritative version agents
> pull; the dashboard labels it **Latest** and the admin action to set it is
> **"Set as Latest"** (see [[Decisions]] / [[UX Roadmap]]).

## Retention
After a successful upload, `SyncService.PruneVersionsAsync` keeps the newest
`Storage:RetainVersionsPerGame` (default 10) per game, deleting older archives.
Never prunes the head or versions referenced by an open conflict.
