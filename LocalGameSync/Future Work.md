# Future Work

Back to [[Home]]. Things deliberately not built yet, roughly by value.

> The [[UX Roadmap]] phase is now functionally complete (WS1–5 + save-folder mapping
> + SteamGridDB art — see [[Progress]]). What remains there is polish (first-run prompt,
> auto-start) plus the items below. This note is the catch-all backlog.
> Note: `set-server` is implemented (see [[CLI Reference]]).

## Agent robustness
- **Durable offline/retry queue.** Today the agent surfaces errors and retries on
  the next file-change or launch/exit event — *not* a persistent queue. If the
  server is unreachable at exit, that push is lost until the next trigger. Add a
  pending-operations queue persisted in config/state.
- **Lease auto-renew / heartbeat** for long play sessions (lease is 6h fixed).
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
- **Real admin auth** distinct from machine API keys (currently any registered
  machine's key can drive admin endpoints). Rely on CloudFlare Access meanwhile.
- Background sweep to expire stale leases proactively (currently lazy on access).

## Agent UX
- ~~Settings UI in the tray~~ — **DONE** (WS1: `SettingsForm` + Add games picker +
  `SaveLocationDialog` save-folder browser). See [[Progress]].
- ~~**First-run prompt** when unregistered~~ — **DONE** (2026-06-22): `MaybeShowFirstRun`
  welcome → Settings, shown once via `FirstRunCompleted`. See [[Progress]].
- ~~**Auto-start on login**~~ — **DONE** (2026-06-22): per-user HKCU Run-key entry via
  `AutoStart.cs` + "Start with Windows" checkbox (with a consent dialog) in Settings. See [[Progress]].
- ~~**Agent installer**~~ — **DONE** (2026-06-22): Inno Setup `installer/LocalGameSync.iss`
  + self-contained publish + single-instance mutex; uninstall reverts the Run key and asks
  about config. See [[Progress]] / [[Decisions]]. *Pending:* interactive install/uninstall
  test on a real machine; code-sign the exe to avoid SmartScreen; auto-update.

## Detection
- Registry-based saves (manifest `registry:` section) — only `files:` handled.
- Multi-directory games (manifest can list several save paths; we take the set
  that exists but the sync engine tracks one `SaveDirectory` per game).

## Productization / branding (revisit before release)
- ~~**Final product name.** Workshop a shortlist.~~ **DONE (2026-06-22).** The official
  product name is **SaveLocker** (see [[Decisions]]). The codebase (`LocalGameSync.sln`,
  namespaces, the installer, mutex, paths) stays `LocalGameSync` until a deliberate
  **technical rename** (a separate task, sequenced here for later). See below.
- ~~**Installer art direction.**~~ **DONE (2026-06-24).** SaveLocker logo added as Inno Setup
  `WizardSmallImageFile` + `WizardImageFile` (`installer/SaveLocker_Logo.png`). Installer
  `AppName`, `AppPublisher`, `DefaultDirName`, `OutputBaseFilename` all use "SaveLocker".
  *Remaining:* code-sign the exe to avoid SmartScreen warnings (currently unsigned).
- **Technical codebase rename** (`LocalGameSync` → `SaveLocker`): namespaces, solution
  file, project names, `AppId` GUID. *Partially done (2026-06-24):* all **user-visible**
  strings, config paths (`%PROGRAMDATA%\SaveLocker`), registry value name, single-instance
  mutex, and installer identifiers are already renamed. What remains is the internal code
  identifiers: `namespace LocalGameSync.*`, `LocalGameSync.sln`, project file names,
  `AppId` GUID. A find-replace + refactor task; low risk. Sequence this **late in
  productization** so it doesn't churn early — the console redesign can target the new
  names (see [[Console Redesign]]).
- **Console redesign** — see [[Console Redesign]] for the full strategy (React + Tailwind
  + shadcn/ui, prototyped in Claude artifacts, then ported to a parallel frontend project,
  folded into the Docker build during deployment hardening). That's where SaveLocker's
  "fresh coat of paint" lands.

## Nice-to-have
- Dashboard: audit-log view (data is captured in `AuditLog`, not yet surfaced).
- Per-game include/exclude globs before archiving.

See [[Progress]] for what *is* done.
