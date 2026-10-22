# Architecture

```
  ┌─────────────┐         ┌──────────────────────────┐         ┌─────────────┐
  │  Gaming PC  │         │   unRAID (Docker)         │         │  Steam Deck │
  │  Agent      │◄──────► │  Server API + Dashboard   │ ◄─────► │  Agent      │
  │ (tray .exe) │  HTTPS  │  + SQLite + archive store │  HTTPS  │ (headless   │
  └─────────────┘         └──────────────────────────┘         │  daemon)    │
                                                                 └─────────────┘
```

Hub-and-spoke (not peer-to-peer). See `Decisions.md` for why unRAID is the keystone. The Windows
agent (`src/Agent`) and Linux agent (`src/Agent.Linux`) both host the shared sync brain in
`src/Agent.Core` — see [[REPO_MAP]] for the full module layout.

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
- `Services/AgentInstallerService.cs` — stores agent installer binaries on disk (`Storage:AgentInstallerRoot`, default `data/agent-installer/`) in **platform-aware slots**: Windows at the root (`SaveLocker-Agent-Setup-*.exe`), Linux at `linux-x64/` (`savelocker-*-linux-x64.tar.gz`). Each slot holds a metadata file (`installer-info.json`) with version, digest, and size. `GET /api/agent/latest?platform=` checks here first; admin endpoints let the dashboard upload/delete/stream per platform. See **Agent auto-update** below.
- `Services/AgentInstallerPollerService.cs` — optional `BackgroundService` that checks the configured GitHub release at the dashboard-managed `AgentUpdate:AutoFetchHours` interval and refreshes the hosted installer only when a newer release is available.
- `Services/LeaseSweeperService.cs` — `BackgroundService` that runs hourly via `IServiceScopeFactory` and sweeps leases where `ExpiresAt < UtcNow`.
- `Services/SettingsService.cs` — DB-backed key/value store (`AppSetting` entity). DB value overrides `IConfiguration` (appsettings/env); used for SteamGridDB key, admin password hash, and installer auto-fetch interval.
- **OpenAPI** — `AddOpenApi()`/`MapOpenApi()` serve `/openapi/v1.json`; Swagger UI at `/swagger`. The web dashboard's types are generated from this doc (`openapi-typescript` → `web/src/api-types.ts`).
- **Dashboard** — React `web/` SPA. Docker `web` build stage copies `dist/` into `src/Server/wwwroot/`. Its `types.ts` is generated from the server's OpenAPI doc, so it can't drift from the DTOs.

### Agent.Core (`src/Agent.Core`)
The platform-neutral sync brain, shared by the Windows tray and the Linux daemon.
- `SyncEngine.cs` — push/pull/pre-launch/post-exit; per-game cross-process `flock` + in-process
  lock together (`Decisions.md` §8).
- `SaveSettler.cs` — settle gate before an automatic push; `FileLockProbe.cs` backs it
  (`FileShare` on Windows, `/proc/*/fd` on Linux).
- `ApiClient.cs` / `ServerTrust.cs` — typed HTTP client + TOFU pin of the server's TLS key.
- `LeaseWarningStore.cs` — durable `lease-warnings.json` beside the config; needed because the
  Linux launch wrapper is a separate short-lived process from the daemon, so an in-memory warning
  never reached any UI (24 h expiry).
- `AgentApiServer.cs` — loopback-only local management API on `:5178`, token-authenticated
  (`Decisions.md` §7).
- `Watchers.cs`, `CommandPoller.cs`, `OfflineQueue(Drainer).cs`, `Enroller.cs`, `SteamVdf.cs`,
  `SteamShortcuts.cs` — discovery, reconciliation, offline retry, enrollment.
- `Platform.cs` (`IAutoStart`, `IGameScanner`) — the seam each host implements.

### Agent (`src/Agent`)
- `Program.cs` — entry point: no args → tray (`TrayApp.cs`); args → CLI commands.
- `TrayApp.cs` — tray icon + menu, engine/poller lifecycle, update-check startup + 24 h timer.
- `AgentApiServer.cs` — ASP.NET Core minimal API on **localhost:5178** (loopback only, never LAN). Serves static `agent-ui/dist/` files (SPA fallback) and JSON API routes for the React agent UI. Every `/api/*` call requires the local token from `{configDir}/api-token`, which is injected into `index.html` when the SPA shell is served; Host and Origin are validated to defeat DNS rebinding. It is a *management* API — see `Decisions.md` §7.
- `AgentWindow.cs` — WinForms form hosting WebView2. Hides instead of closing (re-showable from tray). DPI-scaled `ClientSize` (see `Gotchas.md`).
- `SyncEngine.cs` — push (archive→hash→upload), pull (download→restore), pre-launch (lease + pull), post-exit (push + release).
- `UpdateChecker.cs` — polls `GET /api/agent/latest`, compares with `Assembly.GetEntryAssembly().GetName().Version`, respects `AgentConfig.SkipVersion`. Downloads installer to `%TEMP%`, launches with `/SILENT /FORCECLOSEAPPLICATIONS /NORESTART`.
- `CommandPoller.cs` — 20 s poll: (1) reconcile game list (adopt new server games, drop deleted ones); (2) run queued pull/push/sync/scan commands and report results.
- `ApiClient.cs` — typed HTTP client. `Detection.cs` — Ludusavi manifest wrapper.
- `Watchers.cs` — debounced `FileSystemWatcher` + process poll (`ProcessWatcher`).
- `SaveSettler.cs` — settle gate for **automatic** pushes (process-exit, folder-watch). A process exit does not mean the save is on disk, so before archiving it waits for the save folder to hold a stable fingerprint (file set + sizes + write times) with nothing open for writing, for `SettleQuietSeconds` (default 10, set in agent Settings → Sync Safety), capped by `SettleMaxWaitSeconds` (120) after which the push proceeds anyway. Manual/CLI pushes skip the gate. `PushAsync` serialises per game, since the folder watcher can fire while the exit push is still settling.
- `OfflineQueue.cs` / `OfflineQueueDrainer.cs` — `PushAsync` enqueues to `%PROGRAMDATA%\SaveLocker\offline-queue.json` on `HttpRequestException`; 30 s drain timer retries when the server is reachable.
- `AgentConfig.cs` — JSON config at `%PROGRAMDATA%\SaveLocker\config.json`. Tracks `SkipVersion` + `LastUpdateCheck` for update-check cooldown.

### Linux agent (`src/Agent.Linux`) — binary `savelocker`
Headless Proton-game agent; no tray, no toast. Scope is deliberately non-Steam Windows games run
under Proton (`Decisions.md`), discovered via `shortcuts.vdf`.
- `Program.cs` — `daemon` / `run` / `doctor` / `autostart`, else falls through to `AgentCli`.
- `Daemon.cs` — headless host: `AgentApiServer` + `CommandPoller` + `OfflineQueueDrainer` +
  folder watchers, all long-lived and holding `AgentConfig` in memory (the reason §8/§10 exist).
- `ProtonRun.cs` — `savelocker run -- %command%`, the Steam launch wrapper: reads
  `STEAM_COMPAT_DATA_PATH`/`SteamAppId`, pulls before launch, pushes after exit.
- `LinuxGameScanner.cs` / `SteamRoots.cs` — `IGameScanner` + native/Flatpak Steam root + compatdata
  resolution.
- `Doctor.cs` — end-to-end diagnostic chain; the only UI a headless Deck install has outside the
  Game Mode UI.
- `SystemdAutoStart.cs` — `IAutoStart` via `systemd --user`.
- `Ui/` — **`savelocker ui`**, the Game Mode-only gamepad surface (SDL + OpenGL + Dear ImGui), for
  when there is no browser to reach `:5178` from. Four screens (Status / Add game / Set save
  folder / Steam launch setup) call `Agent.Core` in-process — it is a view, not a second frontend.
  `Theme.cs` mirrors `web/src/index.css` so palette/type match the React UI. `Widgets.cs` paints
  gamepad-navigable components over `InvisibleButton`; `Icons.cs` draws the lucide glyph set as
  vector paths. `Gallery.cs`/`Screenshot.cs` back the WSLg dev loop (`tests/linux/run-ui-wslg.sh`).
  See `Decisions.md` and `Gotchas.md` for the ImGui-specific traps hit building this.

## Agent auto-update
Versioning is **MinVer** (git-tag-driven). Tag `v0.1.0` → that build stamps `0.1.0` in all assembly version fields. Between tags → `0.1.0-alpha.N+hash`.

### Windows
**Release flow:**
1. `git tag v0.2.0 && git push origin v0.2.0`
2. `release.yml` builds on `windows-latest`, runs `build-installer.ps1`, creates a GitHub Release with the exe attached.
3. Admin uploads the installer in dashboard → Configuration → Agent Updates (Windows row, `POST /api/admin/agent-installer?platform=win-x64`).
4. Connected agents check within 24 h (or via tray "Check for Updates"); offered update via balloon → confirm dialog (Update Now / Skip / Remind Later).
5. Agent downloads from `GET /api/agent/installer/download?platform=win-x64`, verifies digest, launches silently, exits.

### Linux/Deck
**Release flow:**
1. `git tag v0.2.0 && git push origin v0.2.0`
2. `release.yml` builds on `ubuntu-latest` (glibc floor), runs `build-linux.sh`, attaches `savelocker-v0.2.0-linux-x64.tar.gz` + SHA-256 checksums + attestation.
3. Admin (or `AgentInstallerPollerService` if enabled) uploads the tarball to dashboard → Configuration → Agent Updates (Linux row, `POST /api/admin/agent-installer?platform=linux-x64`).
4. Connected agents check within 24 h; daemon **stages** (download, verify digest, unpack, smoke-test) without prompting.
5. Update **applies on the next daemon start** via `systemd` unit `ExecStartPre`, not mid-session. Old files are moved aside; a failed swap is rolled back automatically.
6. `AutoUpdate: false` in `config.json` stops the staging while keeping the checks (console still reports being behind).

See **Decisions.md** for the stage-now/apply-on-next-start rationale (cgroup safety, state-dir collision, smoke-test guarantee).

## Conflict model (the safety mechanism)
On upload the agent sends the version it last knew (`parent`):
- `parent == server head` → **fast-forward** (new head).
- Incoming content hash `== head` → **no-op**.
- Head moved on → **conflict** recorded; head untouched; admin resolves.

Leases prevent most conflicts up front; hashing/lineage is the safety net.

**Conflict pressure is capped, not infinite.** The first three rejected payloads on a game
preserve real divergent history; after that, further pushes report the conflict without
re-uploading bytes. A conflict overdue past **6 hours** is surfaced on every heartbeat so a
connected Windows tray can notify the user even when the stuck machine is a headless Deck.
**"Keep both"** doesn't fork into two heads: the chosen snapshot becomes the sole Latest, and
both conflicting snapshots get `SaveVersion.Protected` (exempt from retention) until an admin
explicitly unprotects them. Resolution refuses to promote anything older than the current head.

**Resolving a conflict in the console only edits a row** — it does not tell the agent anything.
A machine's local parent only advances on its own successful push or pull; a stuck agent needs a
guarded pull (safe: it short-circuits before any file write when content already matches head) —
use the CLI (`savelocker pull`, `force: false` by default) rather than the console's Pull button,
which sends `force: true` and bypasses the unpushed-changes guard.

## Per-file delta upload
A normal push only sends the bytes of files that actually changed, instead of re-zipping and
re-uploading the whole save folder — the payoff is real for a game that keeps many largely-static
per-slot saves (Cyberpunk 2077, Fallout: New Vegas), where one changed slot used to cost a full
re-upload of every other unchanged one too. `SaveArchive.ComputeManifest` gives the agent a
per-file `(path, sha256, size)` list; `POST /upload/begin` hands it to the server, which diffs it
against a stored `SaveVersionFile` manifest for its *current* head (fetched fresh, not the agent's
memory of its own last push) and answers with exactly which paths need fresh bytes. The server then
reconstructs the new version's archive by copy-forwarding every unchanged entry straight from the
head's own zip and taking fresh bytes only for what changed — one complete zip per version on disk,
same as before, nothing about restore/download/pruning changes. See **API Reference** →
*Sync* for the wire shape and **Decisions.md** (2026-08-22) for why copy-forward was chosen over a
content-addressable store, and for the fast-forward-only scope boundary (`force`, a diverged push,
a first-ever push, and a head with no stored manifest yet all keep the plain full-archive path
unconditionally — a head self-heals a missing manifest the moment one push completes with one).
Every push that carries a manifest is audited as `upload.delta` (`"N of M file(s) needed fresh
bytes"`), which doubles as the answer to "why did a push I barely touched anything for still make a
new version" — see `tests/run-delta-upload-tests.ps1` for the end-to-end proof, including a
deliberately hostile delta payload that must be refused.

## Data model (SQLite via EF Core)
EF-managed entities: `Machine`, `Game` (holds `HeadVersionId`), `SaveVersion` (parent chain +
`ContentHash`; `Protected` exempts a snapshot from automatic retention), `SaveVersionFile`
(per-version per-file `(path, sha256, size)` baseline for the delta-upload diff above — cascades
with its `SaveVersion`, and a version with no rows here just has no stored baseline yet), `Lease`
(one per game, unique index), `ConflictFlag`, `AuditLog`, `AgentCommand`, `AppSetting` (key/value
store), `MachineSavePath` (composite key `(MachineId, GameId)` → a machine's stored save folder for
a game).

> **"Latest" = the head.** `Game.HeadVersionId` is the authoritative version agents pull; the dashboard labels it **Latest**; the admin action to set it is **"Set as Latest"**.

### Rules the console bug bounty settled (2026-07-27)
Each of these is a *durability or delivery* property rather than a feature; the "why" is in
`Decisions.md`, the traps in `Gotchas.md`.

- **`SaveVersion.MachineId` is nullable, `ON DELETE SET NULL`**, beside a `MachineName` snapshot
  taken at upload. Save history outlives the machine that produced it — deleting a machine used to
  cascade through every version it uploaded. `DeleteMachineAsync` detaches the rows explicitly
  rather than relying on the FK.
- **Head moves in exactly one place** (`SyncService.SetHeadAndPropagateAsync`). Where the *server*
  decided — automatic policy, manual resolution, rollback, Set as Latest — it queues a deduplicated
  **unforced** Pull for every live machine that syncs the game. An ordinary push does not fan out.
- **`AgentCommand` delivery is a visibility lease**, not a one-way `Dispatched`: `LeaseExpiresAt` +
  `ClaimToken` + `ClaimCount`, claimed by one atomic UPDATE. At-least-once, which is safe because
  every command type is safe to repeat.
- **Files are staged then published; rows are deleted before files.** Save archives land in
  `{ArchiveRoot}/.incoming/*.part` and the hosted installer in `{AgentInstallerRoot}/.incoming-*`;
  both publish by rename, both are swept at startup. Deletion commits the database first, because an
  orphaned file is recoverable and a live row with no archive is not.
- **Lease writes are single conditional statements**, so two machines launching together produce one
  winner and one ordinary `Granted=false` instead of a 500. `ActiveLeaseAsync` is a pure read.
- **URLs handed to other machines come from `PublicUrl.For`** — `Server:PublicBaseUrl`, else the
  request origin — and an *inferred* loopback address refuses to mint an enrollment file.
- **Credentials commit only on success:** the SteamGridDB key is verified before it replaces the
  stored one, and enrollment burns its token inside the same transaction that issues the machine key.

## Retention
After a successful upload, `SyncService.PruneVersionsAsync` keeps the newest
`Storage:RetainVersionsPerGame` (default 10) per game, deleting older archives. Never prunes the
head, versions referenced by an open conflict, or versions explicitly protected by an admin.
Per-game override: `Game.RetainVersions` (nullable — null uses global default; 0 = unlimited).
