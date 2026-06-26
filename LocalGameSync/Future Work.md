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
- ~~**Real admin auth** distinct from machine API keys~~ **DONE 2026-06-25 (session 2)** —
  `AdminPasswordFilter` + PBKDF2-SHA256 password, set from ConfigView.
- Background sweep to expire stale leases proactively (currently lazy on access).

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
  Verified on Wideboy (2026-06-25). *Pending:* code-sign the exe to avoid SmartScreen; auto-update.

## Detection
- Registry-based saves (manifest `registry:` section) — only `files:` handled.
- Multi-directory games (manifest can list several save paths; we take the set
  that exists but the sync engine tracks one `SaveDirectory` per game).

## Productization / branding (revisit before release)
- ~~**Final product name.** Workshop a shortlist.~~ **DONE (2026-06-22).** The official
  product name is **SaveLocker** (see [[Decisions]]). The codebase (`LocalGameSync.sln`,
  namespaces, the installer, mutex, paths) stays `LocalGameSync` until a deliberate
  **technical rename** (a separate task, sequenced here for later). See below.
- ~~**Installer art direction.**~~ **DONE (2026-06-25).** Branded wizard images:
  `installer/SaveLocker_WizardBg.png` (164×314 Welcome/Finish panel) and
  `installer/SaveLocker_WizardSmall.png` (55×58 inner pages). `#2A3238` background,
  `#129271` accent. Installer script renamed to `SaveLocker.iss`.
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
- ~~Dashboard: audit-log view~~ **DONE (2026-06-25)** — `GET /api/audit`, `AuditView.tsx`, "Audit Log" nav tab.
- ~~**Hero image downscaling**~~ **DONE 2026-06-25 (session 4)** — `SixLabors.ImageSharp 3.1.7`; max 920 px wide, JPEG q85. See [[Progress]].
- ~~**Per-game retention limits + manual version delete**~~ **DONE 2026-06-25 (session 4)** — `Game.RetainVersions`; Configuration page "Save retention" card; Delete button on versions. See [[Progress]].
- Per-game include/exclude globs before archiving.

See [[Progress]] for what *is* done.
