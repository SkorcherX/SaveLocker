# Session Log (archived)

Condensed history — what shipped, in reverse-chronological order.
Full commit detail in `git log`. Active backlog in `Backlog.md`.

---

## 2026-07-11 (session 2) — v0.1.2 auto-update fixes + fetch-from-GitHub

**Commits:** `3902505`, `f8accb4`, `303fdfc` (fixes), `639bce1` (feature). Tag `v0.1.2` force-moved onto `303fdfc`.

- **Agent version bug (the hard one)** — UI showed `0.0.0`, then `0.1.0` after a first attempt. Root cause: MinVer assigns `Version`/`FileVersion`/`AssemblyVersion` **inside an MSBuild target**, and target property assignments override command-line `--property` globals — so neither `--property:Version` nor `--property:AssemblyVersion` ever won (FileVersion fell back to the `MinVerMinimumMajorMinor` floor `0.1.0`). Fixed two ways: (1) `build-installer.ps1` now sets the `MinVerVersionOverride` env var (MinVer's own escape hatch, stamps all fields); (2) `UpdateChecker.CurrentVersion` reads `FileVersion` via `Environment.ProcessPath` instead of `AssemblyVersion` (`Assembly.Location` is empty for single-file exes). Verified locally: `FileVersion=0.1.2.0`.
- **Installer persistence** — added `Storage:AgentInstallerRoot=/data/agent-installer` to `appsettings.json` (was defaulting inside the container, wiped on every Docker update).
- **Silent auto-relaunch** — `skipifsilent` already removed from `SaveLocker.iss [Run]`; still needs a real end-to-end test.
- **Fetch installer from GitHub (feature)** — new `POST /api/admin/agent-installer/fetch-github`: calls the GitHub Releases API, finds the `SaveLocker-Agent-Setup-*.exe` asset, downloads it, and hosts it via `AgentInstallerService.FetchLatestFromGitHubAsync` — automating the manual download+upload. Repo configurable via `AgentUpdate:GitHubRepo`. "Fetch latest from GitHub" button in Config → Agent Updates. `openapi.json` + `api-types.ts` regenerated.
- **Note:** `v0.1.2` tag was force-moved 3× as fixes landed. The GitHub Release object wasn't regenerated (no `gh`/token locally) — CI overwrites the installer asset but release notes may be stale.

---

## 2026-07-11 — MinVer versioning + release CI + server installer + console UI

**Commits:** `0a8f2fc`, `c9c6fee`, plus namespace/build fixes through `6cf06a6`

- **MinVer (Task A)** — removed hardcoded `<Version>0.1.0</Version>` from `SaveLocker.Agent.csproj`; added MinVer package. Version now derived from nearest git tag. `MinVerMinimumMajorMinor=0.1` floors untagged dev builds at `0.1.x-alpha`.
- **Release CI (Task B)** — `.github/workflows/release.yml`: triggers on `v*` tags, runs on `windows-latest`, installs Inno Setup via Chocolatey, runs `build-installer.ps1`, uploads exe to GitHub Release via `softprops/action-gh-release@v2`.
- **Server installer hosting (Task C)** — `AgentInstallerService` stores installer binary in `data/agent-installer/` with a sidecar `installer-info.json`. `GET /api/agent/latest` checks filesystem first before falling back to static config. New public `GET /api/agent/installer/download` streams the binary. Admin endpoints: `GET/POST/DELETE /api/admin/agent-installer`. `AgentInstallerStatus` record added to `Contracts.cs`. Kestrel body limit raised to 200 MB for installer uploads.
- **Console admin UI (Task D)** — "Agent Updates" card in Configuration page: shows hosted version/size/date, download link, Delete button, upload form (version auto-parsed from filename).
- **Live version in agent UI (Task E)** — `currentVersion` added to `/api/state`. Hardcoded `"Agent v1.0"` replaced with live value from state.
- **Namespace rename fix** — 42 source files with `LocalGameSync` → `SaveLocker` namespace changes were never committed; Docker builds were failing. Staged and committed all in `29e3237`. Also fixed Dockerfile and ci.yml csproj refs in `e95f1bb`.

---

## 2026-07-10 — Security patch + agent versioning + auto-update

**Commits:** `0bf04a1`, `809716b`

- **SixLabors.ImageSharp** bumped 3.1.7 → 3.1.12 (GHSA-rxmq-m78w-7wmc, GIF decoder DoS).
- **UpdateChecker.cs** (new) — queries `GET /api/agent/latest`, compares with `System.Version`, respects `AgentConfig.SkipVersion`, streams installer to `%TEMP%`, launches with `/SILENT /FORCECLOSEAPPLICATIONS /NORESTART`.
- **TrayApp.cs** — startup check 5 s after launch; 24 h `System.Threading.Timer` re-check; 24 h cooldown via `AgentConfig.LastUpdateCheck`. Tray menu item "Check for Updates" / "Update to vX.Y.Z…". Balloon → confirm dialog: Update Now / Skip This Version / Remind Me Later.
- **AgentApiServer.cs** — `GET /api/agent-version` → `{ currentVersion, latestVersion, updateAvailable }`.
- **Server** — `GET /api/agent/latest` reads `AgentUpdate:{LatestVersion,DownloadUrl}` from `appsettings.json`; returns 204 when unconfigured. `AgentVersionInfo` record added to `Contracts.cs`.

---

## 2026-07-08 — Hygiene #5a–#5d

See `logs/hygiene-2026-07-06.md` for the full review. Items shipped:

- **#5a** (`0015cda`) — `BackupService` + `BackupScheduler`: nightly `VACUUM INTO` SQLite snapshot, newest-N retention (default 7), `POST /api/admin/backup` + `GET /api/admin/backups`. Startup catch-up. Backups land at `/data/backups`.
- **#5b** (`1782367`) — OpenAPI contract (`AddOpenApi`/`MapOpenApi`, `/openapi/v1.json`, `/swagger`). Web dashboard types generated from it (`openapi-typescript` → `web/src/api-types.ts`). Snapshot committed at `src/Server/openapi.json`.
- **#5c** (`82d0f71`) — `LeaseSweeperService` (`BackgroundService`) runs hourly via `IServiceScopeFactory`, clears leases where `ExpiresAt < UtcNow`. Docker HEALTHCHECK added (`curl /health`).
- **#5d** (`ecf35b5`) — agent-ui toolchain bump (Vite 6→8, TS 5.8→6, react/types); `oxlint` added. New `.github/workflows/ci.yml`: three parallel jobs (build-dotnet, build-web, build-agent-ui) on every PR + main push.

---

## 2026-07-06 — Repo hygiene pass

See `logs/hygiene-2026-07-06.md` for the full plan + findings.

- **#1–3** (`2597cf1`, `14e3320`, `98d8f34`) — removed spent design prototypes; brought `.verify` tests + dev config into repo under `tests/`; docs refresh.
- **#4a** (`71f83ec`) — `MachineSavePaths` folded into EF (entity + `DbSet` + `AddMachineSavePaths` migration); raw SQL replaced with LINQ. Existing DBs adopted via migration stamp on startup.
- **#4b** (`bf67cc3`) — machine-key rotation guard: re-registering an existing name requires `X-Admin-Password` when a password is set. First-time registration stays open.

---

## 2026-06-26–27 — Lease heartbeat, installer artwork, bug fixes

- **Lease heartbeat** (`ee27a57`) — `SyncEngine` 3 h renew timer calls `POST /api/games/{id}/lease/renew`. Dashboard lease-conflict warning UI.
- **Installer artwork** (`21b0bb9`) — `WizardBg` (164×314) + `WizardSmall` (55×58) regenerated: `#1E252A` bg, logo centred, green separator, bold title.
- **Bug fixes** (`47f6a3b`, `d381f74`, `73e9100`, `8eae726`) — enrollment 401 (agent enrollment misrouted to admin group); stats/timezone mismatch; agent window black bars; art volume moved from `wwwroot/art/` to `/data/art/` (survives container updates).

---

## 2026-06-25 (session 4) — Hero downscaling, storage display, retention, version delete

**Commits:** `57cd313`, `6e146f3`, `8b65b54`

- **Hero downscaling** — `ImageSharp 3.1.7`; max 920 px wide, JPEG q85.
- **Storage display** — `GameStateDto.TotalStorageBytes`; dashboard shows per-game + grand total MB.
- **Per-game retention** — `Game.RetainVersions` (nullable); Configuration page "Save retention" card; `POST /games/{id}/retain`.
- **Manual version delete** — `DELETE /games/{id}/versions/{versionId}` (refuses head + open-conflict).

---

## 2026-06-25 (session 3) — Offline retry queue

**Commit:** `9baadf7`

- `OfflineQueue.cs` + `OfflineQueueDrainer.cs`. `SyncEngine.PushAsync` catches `HttpRequestException` and enqueues to `%PROGRAMDATA%\SaveLocker\offline-queue.json`. 30 s drain timer. Deduped by `GameId`; `force=true` sticky; retry count + last-attempt timestamp. Verified end-to-end.

---

## 2026-06-25 (session 2) — Admin password auth, favicon, git hygiene

**Commits:** `adb48c5`, `bfd608d`, `4f30d8d`

- **Admin auth** — `AdminPasswordFilter` + PBKDF2-SHA256 (100k iterations, salted). `GET /api/admin/status` (public). Route groups split: agent keeps `ApiKeyFilter`, dashboard uses `AdminPasswordFilter`. Set from ConfigView.
- **Favicon** — replaced broken set with full modern set: `.ico`, `32×32`, `16×16`, `apple-touch-icon`, PWA Android set + `site.webmanifest`.
- **Git hygiene** — removed 6 binary/stale files from tracking; added `src/Server/wwwroot/` to `.gitignore`.

---

## 2026-06-25 (session 1) — Cleanup, full user-visible rename, agent UI polish

- Deleted dead WinForms files (replaced by React agent UI).
- All remaining "LocalGameSync" user-visible strings → "SaveLocker".
- `installer/LocalGameSync.iss` → `installer/SaveLocker.iss`; branded wizard images.
- **Per-machine save paths** — `MachineSavePaths` table, SyncService CRUD, server endpoints, agent two-way sync, dashboard table. Verified on ThunderHorse + Wideboy.
- **Folder picker STA fix** — `ShowFolderPickerAsync` now spawns a dedicated STA thread; parents dialog to `Application.OpenForms[0]`.
- **Audit log view** — `GET /api/audit?limit=200`, `AuditView.tsx`, "Audit Log" nav tab.
- **Settings input clobber fix** — `dirtyFields` Set prevents the 10 s state poll from overwriting in-progress user input.

---

## 2026-06-24 — Agent UI revamp + CI/CD + SaveLocker branding

- **Agent UI** — replaced WinForms `AddGamesForm`/`SettingsForm` with React/WebView2 SPA (`agent-ui/`). `AgentApiServer.cs` (HttpListener :5178) + `AgentWindow.cs` (WinForms + WebView2). Three views: Overview, Add Games, Settings. Design tokens from SaveLocker handoff. MSBuild targets build + copy `agent-ui/dist/` on build and publish.
- **SaveLocker branding** — config dir, mutex, registry key, installer, tray/balloon text all renamed.
- **GitHub repo** created at https://github.com/SkorcherX/SaveLocker.
- **CI/CD** — `docker-publish.yml` builds + pushes `ghcr.io/skorcherx/savelocker:latest` on every `main` push. Watchtower on unRAID auto-deploys. Multi-stage Dockerfile bakes React `web/dist/` into `wwwroot/`.
- **React dashboard** (`web/`) — Vite 8, React 19, TypeScript, Tailwind v4. All API endpoints wired. Verified live against real DB. Dashboard at http://unraid-ip:5080.

---

## 2026-06-23 — Second machine (Wideboy) + real-world fixes

- Installed agent on Wideboy; diagnosed and fixed OneDrive `Directory.Move` access denied (file-by-file copy to `_tempDir`).
- `AgentLogger.cs` — rolling 1 MB log at `%PROGRAMDATA%\SaveLocker\agent.log`; `log` CLI sub-command to tail it.
- Dashboard auto-refresh collapse fix — `openDetails` Set preserves open panels across `render()`.

---

## 2026-06-22 — UX phase workstreams 2–5 + installer

- **WS2** — Steam VDF readers (`SteamVdf.cs`, `SteamTextVdf.cs`), `GameScanner.cs`, `scan` CLI, tray "Add games…" picker (`AddGamesForm.cs`).
- **WS3** — `ArtService.cs`; SteamGridDB search + fetch + cache; dashboard cover thumbnails. User-confirmed cover art rendering.
- **WS4** — `/api/machines`, enable/disable, set-latest; dashboard rebuilt with Machines table, initial-sync wizard, Set as Latest badge.
- **WS5** — `AgentCommand` queue; `GET /api/agent/commands` + result reporting; `CommandPoller.cs`; dashboard action buttons + command log. Verified end-to-end (dashboard Scan → agent ran it → Done).
- **Save-folder mapping** — `Game.SuggestedSaveDir`, `/save-dir` endpoint, agent reconcile auto-maps/backfills.
- **Machine deletion** — `DELETE /api/machines/{id}` with self-delete guard.
- **Inno Setup installer** — machine-wide, UAC, auto-start task, uninstall reverts Run key, asks about config dir.
- **Product name locked: SaveLocker.**

---

## 2026-06-21 — PoC complete (phases 0–5)

Built and verified end-to-end with real Octopath Traveler 0 saves. Phase 0: scaffold (3 projects). Phase 1: server (EF/SQLite, REST, lease/conflict, Dockerfile). Phase 2: Ludusavi manifest detection. Phase 3: agent (tray, CLI, watchers, sync engine). Phase 4: admin dashboard. Phase 5: hardening (atomic restore, retention, per-machine tokens). Tray WS1 first slice: Settings/Connect window, DPI fix, clipboard STA fix.
