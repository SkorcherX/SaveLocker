# Future Work

Back to [[Home]]. Things deliberately not built yet, roughly by value.

> The [[UX Roadmap]] phase is now functionally complete (WS1–5 + save-folder mapping
> + SteamGridDB art — see [[Progress]]). What remains there is polish (first-run prompt,
> auto-start) plus the items below. This note is the catch-all backlog.
> Note: `set-server` is implemented (see [[CLI Reference]]).

## Agent robustness
- ~~**Durable offline/retry queue.**~~ **DONE 2026-06-25 (session 3).** `OfflineQueue` +
  `OfflineQueueDrainer`; JSON file at `%PROGRAMDATA%\SaveLocker\offline-queue.json`;
  drains automatically every 30 s when connectivity returns. See [[Progress]].
- ~~**Lease auto-renew / heartbeat**~~ **DONE (2026-06-26, `ee27a57`).** `SyncEngine`
  3 h renew timer; `POST /api/games/{id}/lease/renew`; dashboard lease-conflict warning
  UI. See [[Progress]].
- **Save-in-use safety:** gate auto-push strictly on process-exit or a longer
  quiet period to avoid archiving mid-write (debounce currently 5s).

## Server
- ~~**Settings UI in the dashboard for server config — incl. the SteamGridDB API key.**~~
  **DONE 2026-06-22.** DB-backed `SettingsService` (key/value, DB overrides config),
  `GET /api/settings` + `POST /api/settings/steamgriddb-key` (stores + verifies),
  dashboard **Server settings** card. `ArtService` resolves the key per request (no
  restart needed). See [[Progress]]. *Foundation laid for more server settings on the
  same `AppSetting` table + panel.*
- ~~**EF Core migrations**~~ **DONE 2026-06-24.** `InitialSchema` migration + `db.Database.Migrate()` with
  pre-migration DB bootstrap shim. See [[Progress]].
- ~~**Real admin auth** distinct from machine API keys~~ **DONE 2026-06-25 (session 2)** —
  `AdminPasswordFilter` + PBKDF2-SHA256 password, set from ConfigView.
- ~~**Guard machine-key rotation** — re-registering an existing name hijacks it~~
  **DONE 2026-07-06 (`bf67cc3`)** — `POST /api/machines/register` requires `X-Admin-Password`
  to re-register an existing name once a password is set; first-time registration stays open.
  See [[Progress]] / [[API Reference]].
- ~~**Fold `MachineSavePaths` into EF**~~ **DONE 2026-07-06 (`71f83ec`)** — entity + migration,
  stamped on existing DBs. See [[Architecture]].
- ~~**Server-side SQLite backup**~~ **DONE 2026-07-08 (`0015cda`, hygiene #5a)** — `BackupService`
  snapshots the live DB via `VACUUM INTO` (WAL-safe), newest-N retention (default 7);
  `BackupScheduler` runs nightly at `Backup:HourOfDay` + a startup catch-up; `POST /api/admin/backup`
  + `GET /api/admin/backups`. Backups land on `/data/backups`. See [[Progress]] / [[API Reference]].
- ~~**OpenAPI contract + generated dashboard types**~~ **DONE 2026-07-08 (`1782367`, hygiene #5b)** —
  server emits `/openapi/v1.json` + Swagger UI at `/swagger`; the web dashboard's `types.ts` is
  generated from it (`openapi-typescript`), so it can't drift from the C# DTOs. agent-ui stays
  hand-typed (its local `HttpListener` backend isn't OpenAPI-introspectable). See [[API Reference]].
- **Agent local API → generated agent-ui contract** *(deferred from #5b, larger swing)* — the
  agent UI talks to `AgentApiServer.cs`, a raw `HttpListener` returning anonymous objects, so its
  3 types (`AgentState`/`Candidate`/`TrackedGame`) stay hand-written. Rewriting that server as an
  ASP.NET Core minimal API would make it OpenAPI-introspectable and let agent-ui generate its types
  too — but it touches WPF/WebView2/STA folder-picker + `HttpListener` lifetime, so it's a separate
  task, not a quick win.
- ~~**Background sweep for stale leases**~~ **DONE 2026-07-08 (`82d0f71`, hygiene #5c)** —
  `LeaseSweeperService` (`BackgroundService`) runs hourly via `IServiceScopeFactory`. See [[Progress]].

## Agent UX
- ~~Settings UI in the tray~~ — **DONE** (WS1 → replaced by React/WebView2 agent UI).
  `AddGamesForm.cs`, `SettingsForm.cs`, `SaveLocationDialog.cs` were the old WinForms
  windows — all deleted (2026-06-25). `AgentApiServer.cs` + `agent-ui/` React SPA is the
  current implementation (three views: Overview, Add Games, Settings). See [[Progress]].
- ~~**First-run prompt** when unregistered~~ — **DONE** (2026-06-22): `MaybeShowFirstRun`
  welcome → Settings, shown once via `FirstRunCompleted`. See [[Progress]].
- ~~**Auto-start on login**~~ — **DONE** (2026-06-22): per-user HKCU Run-key entry via
  `AutoStart.cs` + "Start with Windows" checkbox in Settings. See [[Progress]].
- ~~**Agent installer**~~ — **DONE**: Inno Setup `installer/SaveLocker.iss` + self-contained
  publish + single-instance mutex; wizard branded with `SaveLocker_WizardBg.png` /
  `SaveLocker_WizardSmall.png`; uninstall reverts Run key and asks about config.
  Verified on Wideboy (2026-06-25). *Pending:* code-sign the exe to avoid SmartScreen.
- ~~**Agent versioning + auto-update**~~ — **DONE 2026-07-10.** `<Version>` in csproj;
  `GET /api/agent/latest` (server, admin-configured); `UpdateChecker` service; tray startup +
  24 h periodic checks + manual tray item; balloon → confirm/skip/remind dialog; installer
  launched with `/SILENT /FORCECLOSEAPPLICATIONS`. See [[Agent Auto-Update]] / [[Progress]].

## Detection
- Registry-based saves (manifest `registry:` section) — only `files:` handled.
- Multi-directory games (manifest can list several save paths; we take the set
  that exists but the sync engine tracks one `SaveDirectory` per game).

## Productization / branding (revisit before release)
- ~~**Final product name.** Workshop a shortlist.~~ **DONE (2026-06-22).** The official
  product name is **SaveLocker** (see [[Decisions]]). The codebase (`LocalGameSync.sln`,
  namespaces, the installer, mutex, paths) stays `LocalGameSync` until a deliberate
  **technical rename** (a separate task, sequenced here for later). See below.
- ~~**Installer artwork polish**~~ **DONE (2026-06-26, `21b0bb9`).** `WizardBg` (164×314)
  and `WizardSmall` (55×58) regenerated from `SaveLocker_Logo.png`: `#1E252A` background,
  logo centred, green separator, bold title + muted tagline. `WizardImageBackColor` DPI
  fallback added. See [[Progress]].
- *Remaining:* code-sign the exe to avoid SmartScreen warnings (currently unsigned).
- ~~**Technical codebase rename** (`LocalGameSync` → `SaveLocker`)~~ **DONE 2026-07-10.**
  Namespaces, solution file (`SaveLocker.sln`), project files (`SaveLocker.Agent/Server/Shared.csproj`),
  DB filenames (`savelocker.db`), installer `AppExe`, and all internal identifiers renamed.
  *Note:* existing server deployments with `/data/localgamesync.db` need to rename the file
  to `/data/savelocker.db` (or override `Db:DbPath` in environment). AppId GUID in the
  installer is unchanged (changing it breaks Windows upgrade detection).
- **Console redesign** — see [[Console Redesign]] for the full strategy (React + Tailwind
  + shadcn/ui, prototyped in Claude artifacts, then ported to a parallel frontend project,
  folded into the Docker build during deployment hardening). That's where SaveLocker's
  "fresh coat of paint" lands.

## Nice-to-have
- ~~Dashboard: audit-log view~~ **DONE (2026-06-25)** — `GET /api/audit`, `AuditView.tsx`, "Audit Log" nav tab.
- ~~**Hero image downscaling**~~ **DONE 2026-06-25 (session 4)** — `SixLabors.ImageSharp 3.1.7`; max 920 px wide, JPEG q85. See [[Progress]].
- ~~**Per-game retention limits + manual version delete**~~ **DONE 2026-06-25 (session 4)** — `Game.RetainVersions`; Configuration page "Save retention" card; Delete button on versions. See [[Progress]].
- Per-game include/exclude globs before archiving.

See [[Progress]] for what *is* done.
