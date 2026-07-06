# Progress

Back to [[Home]]. Last updated: 2026-06-25 (session 4).

## Status: all 5 phases complete and verified ✅

| Phase | What | State |
|---|---|---|
| 0 | Scaffold solution (3 projects + refs) | ✅ done |
| 1 | Server core (EF/SQLite, REST, lease/conflict logic, Dockerfile) | ✅ done, verified |
| 2 | Detection library (Ludusavi manifest + path resolver) | ✅ done, verified |
| 3 | Agent core (tray + CLI, watchers, sync engine) | ✅ done, verified |
| 4 | Admin dashboard (overview, resolve, rollback, lease admin) | ✅ done, verified |
| 5 | Hardening (atomic restore, retention, per-machine tokens, docs) | ✅ done, verified |

## Verification performed
- **Server API — 19 checks:** register, auth (401), lease grant + denial,
  upload fast-forward / conflict / no-change, head NOT overwritten on conflict,
  list/resolve conflict, rollback.
- **Agent integration — 10 checks** (`tests/run-agent-tests.ps1`): manifest
  detection against a real `%APPDATA%` folder; two-machine push→pull with
  byte-identical restore; up-to-date no-op; full conflict path; status reflects it.
- **Dashboard data path:** overview shows conflict (head from PC); resolving
  flips head to Laptop and clears the conflict.
- **Retention:** 13 chained uploads → exactly 10 versions kept in DB *and* on
  disk; head still latest + downloadable.
- Full solution builds clean (use `--no-incremental` — see [[Gotchas]]).

## Environment
- .NET 9 SDK installed via winget (`Microsoft.DotNet.SDK.9`, 9.0.315). On the
  machine PATH but **not** in already-open shells — prepend
  `"$env:ProgramFiles\dotnet"` or open a new shell.
- EF Core pinned to **9.0.9** (10.x needs net10).
- Dev server picks port from launch profile (5179) unless run with
  `--no-launch-profile` + `ASPNETCORE_URLS`.

## Post-PoC refinements (live)
Proof of concept confirmed working with real saves ("Octopath Traveler 0",
machine ThunderHorse). Hardened during real use:
- **Pull guard:** `pull` refuses to overwrite local saves with un-pushed changes
  unless `--force` (prevents clobbering real progress on a machine's first sync).
  Verified.
- **Case-insensitive game matching:** PC + laptop map to one server game even with
  different casing (a case-sensitive duplicate bug was found + fixed).
- **New commands/endpoints:** `set-server`, `whoami`, `register` now prints the
  key, `pull --force`, and `DELETE /api/games/{id}` (delete-game). See
  [[CLI Reference]] / [[API Reference]].

## UX phase progress ([[UX Roadmap]])
- **Workstream 1 — Tray agent UX: first slice DONE & verified (user-confirmed).**
  - Tray right-click → **Settings…** (server URL, machine name, Save,
    **Register/Re-register** → shows API key, **Copy**, tracked-games list +
    Remove) and **View API key** dialog. `SettingsForm.cs`, `TrayApp.cs`.
  - Agent icon now the embedded `favicon.png` (`Assets/favicon.png` →
    `AppResources.cs`); used for tray + window title bars.
  - **Fixed:** clipboard crash — `[STAThread]` is ignored on an **async Main**, so
    the UI thread was MTA and Clipboard (OLE) threw. Made `Main` synchronous (CLI
    bridges to async via `GetAwaiter().GetResult()`); added resilient
    `AppResources.TryCopy`. See [[Gotchas]].
  - **Fixed:** Settings/API-key windows clipping → rebuilt with docked
    `TableLayoutPanel` + `AutoScaleMode.Dpi` so they can't clip at any scaling.
  - Engine + menu + folder watchers **rebuild after Settings closes** (no restart).
  - **WS1 polish DONE (2026-06-22):** first-run prompt + auto-start on login.
    - **First-run prompt** — `TrayContext.MaybeShowFirstRun()` (TrayApp.cs): on launch
      when unregistered (no `ApiKey`) and not yet completed, a Yes/No welcome MessageBox
      offers to open Settings to set the server + register; shown **once** — registering
      or dismissing sets the new `AgentConfig.FirstRunCompleted` flag so it never nags.
      Posted via the UI sync-context so the tray icon shows first.
    - **Auto-start on login** — `AutoStart.cs`: per-user `HKCU\…\CurrentVersion\Run`
      entry ("LocalGameSync"), no admin needed. `IsEnabled`/`SetEnabled`; the launcher
      path is `Environment.ProcessPath` except under `dotnet Agent.dll` (dev), where it
      maps to the apphost exe via `AppContext.BaseDirectory` (avoids `Assembly.Location`,
      which warns IL3000 in single-file). Surfaced as a **"Start with Windows"** checkbox
      in `SettingsForm` (initialised from `AutoStart.IsEnabled()`).
    - **Explicit consent (user-requested):** ticking the checkbox now shows a Yes/No
      dialog explaining it writes a per-user Run-key entry and that uninstall removes
      it; declining reverts the checkbox. No Run entry is ever created without consent.
    - Built clean (0 warnings). *Live UI eyeball check of the prompt+checkbox: pending.*

- **Workstream 2 — Game scanning: testable core DONE & verified on real machine.**
  - **Binary VDF reader** `SteamVdf.cs` — parses `shortcuts.vdf` (types 0x00 object
    / 0x01 string / 0x02 int32 / 0x08 end). Correctly read the real shortcuts file
    and found "OCTOPATH TRAVELER 0" with its resolved save dir.
  - **Text VDF reader** `SteamTextVdf.cs` — for `libraryfolders.vdf` + `appmanifest_*.acf`
    (quoted tokens + `{ }` blocks, `//` comments, escapes). Parses into the same
    `VdfObject` tree as the binary reader.
  - **`GameScanner.cs`** — Steam locator (registry `HKCU\…\Steam\SteamPath`, HKLM
    fallback) + three sources: non-Steam shortcuts, installed Steam games (flagged
    `HasSteamCloud`, with a non-game appid denylist e.g. 228980 redistributables),
    save-root heuristic (APPDATA/LocalLow/My Games/Saved Games matched to manifest).
    De-dupes by name, prefers candidates with a resolved save dir.
  - **CLI `scan` command** (`--no-cloud` to hide Steam-Cloud titles). Verified on the
    real machine: 17 candidates total, 4 with `--no-cloud` (Octopath shortcut +
    save-root matches). See [[CLI Reference]].
  - **Bug fixed:** `ManifestLoader.Parse` crashed (`An item with the same key…`) on
    Ludusavi entries differing only in case (e.g. "Afterlife"/"afterlife"); now adds
    with `TryAdd` (first wins) instead of the throwing ctor overload.
  - **Tray "Add games…" picker** `AddGamesForm.cs` (built & user-verified) —
    right-click tray → **Add games…**. Runs `GameScanner.ScanAsync()` on show,
    lists candidates in a `CheckedListBox` (name / source / Steam-Cloud flag /
    save dir), with **Rescan**, **Set save folder…** (FolderBrowserDialog for
    candidates with no resolved dir), **Hide/Show Steam Cloud** toggle, and
    **Enroll selected**. Enroll mirrors `add-game`: `CreateGameAsync` + add
    `TrackedGame` + `config.Save()`; skips already-tracked games; requires the
    save folder to be set first; needs registration (API key). Tray rebuilds the
    menu + folder watchers after enroll. DPI-safe docked `TableLayoutPanel`.
  - **WS2 complete** — picker verified working on the real machine (2026-06-22).
- **Workstream 4 — Dashboard as primary admin: server + UI built, endpoints verified.**
  - **New endpoints** (`Program.cs` / `SyncService`): `GET /api/machines`
    (`MachineDto`), `POST /api/games/{id}/enabled?value=` (enable/disable),
    `POST /api/games/{id}/set-latest?version=` (head move; audited `set_latest`,
    shares `SetHeadAsync` with rollback). See [[API Reference]].
  - **Dashboard `wwwroot/index.html` rebuilt** — per-game **Enable/Disable** + **Delete**
    buttons, `disabled` badge/dimming; detail pane adds a **Machines** table
    (last upload per machine + last-seen), an **initial-sync wizard** ("which
    machine has your real progress?" → set its newest save as Latest, shown when
    >1 machine has uploaded), and the version list relabeled **"Set as Latest"** with
    a green **Latest** badge on the head. Added **"+ Add game"** (name) in the header.
    HTML-escaped interpolations.
  - **Verified headlessly** against the real DB (key from `GamingPC` config):
    `/machines` (5 machines), `/overview`, enable→disable→enable round-trip, and
    set-latest swap + restore on "Octopath Traveler 0" (state left unchanged).
  - *Remaining WS4:* visual/interactive check of the new dashboard in the browser.
- **Workstream 5 — Agent command channel: built & verified end-to-end (polling model).**
  - **Why polling, not push:** keeps the server passive and works through tunnels/
    firewalls (agent only makes outbound calls). Answers the recurring "pull or push?"
    question: it's **agent-initiated polling**, ~20s.
  - **Server:** `AgentCommand` entity + queue. Endpoints `GET /api/agent/commands`
    (dequeue → Dispatched), `POST /api/agent/commands/{id}/result`,
    `POST /api/commands` (enqueue), `GET /api/commands` (activity log). New table is
    created additively at startup via `CREATE TABLE IF NOT EXISTS` because
    `EnsureCreated()` won't alter an existing DB. See [[API Reference]].
  - **Agent `CommandPoller.cs`** (wired into `TrayApp`): each tick (1) **reconciles
    games** — adopts server games not tracked locally (auto-maps save dir via manifest
    when possible, else leaves it blank + notifies), drops games deleted on the server;
    (2) runs queued **pull/push/sync/scan** and reports results. `ApiClient` gained
    `GetAgentCommandsAsync`/`ReportCommandAsync`.
  - **Dashboard:** per-machine **Pull/Push/Sync** buttons in a game's detail +
    a **Recent remote commands** table (status/result).
  - **Verified on the real machine:** dashboard-queued **Scan** → running agent polled,
    executed, reported `Done` ("found 16 candidates: …"). Game propagation confirmed —
    agent auto-adopted the dashboard-created "Octopath Traveller 0" (same GUID).
  - **Follow-up (DONE this session):** save-folder mapping — see below.
- **Save-folder mapping + console-suggested path (2026-06-22).**
  - **Agent folder+file picker** `SaveLocationDialog.cs` — a custom browser with a
    drive/folder **tree** + a **file list** (name/size/modified) so the user navigates
    and *confirms the save files* before choosing (the stock `FolderBrowserDialog`
    hides files). Lazy-loads the tree; pre-selects an initial path. Wired into
    `SettingsForm` (new **"Set save folder…"** for any tracked game; unmapped games now
    read "(no folder set…)") and `AddGamesForm` (replaces the old `FolderBrowserDialog`).
  - **Console-suggested save dir** — `Game.SuggestedSaveDir` (additive
    `ALTER TABLE Games ADD COLUMN`, guarded by a `pragma_table_info` check).
    `CreateGameRequest`/`GameDto` carry it; new `POST /api/games/{id}/save-dir?value=`.
    Dashboard: "+ Add game" now also prompts for an optional save folder, and each
    game's detail shows the suggested folder with an **Edit**.
  - **Agent propagation** — `CommandPoller` reconcile now resolves a game's save dir
    as **suggested-dir-if-it-exists-here → manifest → unmapped**, and **backfills**
    already-tracked games whose folder is empty when the server later gains a suggestion.
  - **Verified end-to-end on the real machine:** set Octopath's suggested dir in the
    console → agent's next poll **auto-mapped** it to that folder. Create-with-dir +
    `/save-dir` persistence confirmed. (Picker dialog itself: live UI check pending.)
  - **Caveat (by design):** the suggested path is single-valued server-side; a machine
    where it doesn't exist stays unmapped until the user picks a local folder. True
    per-machine path storage would need a machine×game mapping table (future).
- **Workstream 3 — SteamGridDB artwork: DONE & verified (cover confirmed on dashboard).**
  - **`ArtService.cs`** — search `search/autocomplete/{name}` → id, then fetch
    `grids|heroes|logos|icons/game/{id}` (first asset), download + cache under
    `wwwroot/art/{gameId}/{kind}{ext}`, store served (cache-busted) URLs on `Game`.
    Named `HttpClient` "steamgriddb" with `Bearer` from config `SteamGridDb:ApiKey`.
  - **`Game`** gained `GridUrl/HeroUrl/LogoUrl/IconUrl` (additive ALTER migration,
    folded into the pragma-guarded column loop). `GameDto` carries them.
  - **Endpoints:** `POST /api/games/{id}/art/refresh` (manual) + best-effort
    fetch-on-enroll in `POST /api/games`. Graceful 400 "API key not configured".
  - **Dashboard:** cover thumbnail on each card (`.cover`, "no art" placeholder) +
    a **Refresh art** button. **User-confirmed: Octopath cover renders on the dashboard.**
  - **Verified end-to-end** (key in `appsettings.Development.json`): refresh →
    "Updated art: grid, hero, logo, icon"; all four URLs on `GameDto`; files cached
    under `wwwroot/art/{gameId}/` (grid.jpg 804 KB, hero.png ~9.5 MB, logo.png, icon.png);
    grid serves HTTP 200 image/jpeg.
  - **Fix during verification:** asset images live on a separate CDN host that 401s the
    API bearer token — download them with a clean no-auth `HttpClient` (`_download`),
    only the api.steamgriddb.com calls carry the Bearer.
  - Build kept at **0 warnings** (suppressed EF1002 on the hardcoded-column ALTER).
  - *Future:* enter the SteamGridDB key in the dashboard (no config-file edits) — see
    [[Future Work]]; hero is full-res (~9.5 MB) but only the grid cover is shown.
- **Machine (user/key) deletion (2026-06-22).** Testing left many registered machines.
  `DELETE /api/machines/{id}` + `SyncService.DeleteMachineAsync` — removes the machine's
  leases + pending commands, **keeps** its `SaveVersion`s as history; **self-delete guard**
  (can't delete the machine whose key authenticated the call → 400). Dashboard gained a
  top **"Machines / API keys"** panel (name/registered/last-seen + Delete). Verified:
  register→delete round-trip (count 6→5), self-delete 400, dashboard panel serves.

## Agent installer (Inno Setup) — 2026-06-22
Because auto-start writes a registry entry, a manually-deleted exe would orphan it
([[Future Work]] reasoning from the user). So the agent now ships a real installer that
**owns and reverts every system change**.
- **Self-contained publish** — `src/Agent/Properties/PublishProfiles/win-x64.pubxml`:
  win-x64, `PublishSingleFile` + `SelfContained` (compressed) → one 48 MB
  `LocalGameSync.Agent.exe` that needs **no .NET runtime** on the target (matches the
  non-technical-user goal).
- **Single-instance mutex** — `Program.cs` acquires a named `Mutex("LocalGameSync.Agent")`
  in the tray path; a second launch (e.g. auto-start + manual) just exits. The same name
  is the installer's `AppMutex` so setup/uninstall detect a running agent and prompt to
  close it before replacing files. CLI one-shots are unguarded.
- **`installer/LocalGameSync.iss`** (Inno Setup 6, chosen by user): **per-user** install
  (no admin) to `%LOCALAPPDATA%\Programs\LocalGameSync`, `PrivilegesRequired=lowest`,
  Start-Menu shortcut, optional **"Start automatically when I log in"** task that writes
  the *same* HKCU Run value the in-app toggle uses (so they stay consistent). **Uninstall
  reverts everything:** `[Code] CurUninstallStepChanged` always `RegDeleteValue`s the Run
  entry (covers an app-created one even if the install task wasn't ticked), then **asks**
  whether to also delete `%PROGRAMDATA%\LocalGameSync` (config + API key) — *No* keeps it
  for a reinstall (user-chosen behaviour).
- **`installer/build-installer.ps1`** — publishes then runs `ISCC.exe` (searches PATH +
  Program Files + the per-user `%LOCALAPPDATA%\Programs\Inno Setup 6` install). Inno Setup
  installed via `winget install JRSoftware.InnoSetup` (6.7.3).
- **Install scope (revised 2026-06-22 after a real test):** **machine-wide to
  `C:\Program Files\LocalGameSync`** (`DefaultDirName={autopf}`), `PrivilegesRequired=admin`
  so it requests UAC **up front** — the first per-user attempt failed with "error 5: Access
  is denied" creating the dir under Program Files, and needed manual run-as-admin. Start-Menu
  shortcut is now `{autoprograms}` (all-users); post-install launch uses `runasoriginaluser`
  so the tray agent starts **de-elevated** as the real user (a tray app must not run elevated).
  Auto-start stays the installing user's HKCU Run key.
- **Verified:** compiles clean → `installer/dist/LocalGameSync-Agent-Setup-0.1.0.exe`
  (~43 MB, pdb excluded). *Interactive install→uninstall run on a real machine: user will
  test later (the running dev agent holds the AppMutex, which the installer will flag as expected).*
- **Productization TODO (user, 2026-06-22):** "LocalGameSync" is a prototype name — needs a
  final product name + real installer art (logo, wizard images) + code-signing before
  release. See [[Future Work]] "Productization / branding".

## SteamGridDB API key in the dashboard — 2026-06-22
Milestone #2: enter the SteamGridDB key from the console instead of editing
`appsettings`. Foundation for a broader server-settings panel.
- **Server settings store** — new `AppSetting { Key, Value }` entity + `Settings` DbSet
  (additive `CREATE TABLE IF NOT EXISTS "Settings"` at startup for existing DBs). New
  `SettingsService` with `GetEffectiveAsync` (**DB value wins, falls back to
  `IConfiguration`** for back-compat), `SetAsync` (clear when blank), and a masked
  dashboard DTO (`ServerSettingsDto` — never returns the raw key, shows `••••••••47ec`).
- **`ArtService` now resolves the key per call** via `SettingsService` and attaches the
  `Bearer` **per request** (was a startup-baked default header on the named client) — so a
  dashboard change takes effect **immediately, no restart**. Added `VerifyKeyAsync`
  (cheap authenticated call) for save-time feedback.
- **Endpoints:** `GET /api/settings` (status) + `POST /api/settings/steamgriddb-key`
  (`SetSteamGridDbKeyRequest`) — stores then verifies the key, returning `{ ok, message }`.
- **Dashboard:** a **Server settings** card (password field + Save / Clear, masked status,
  "from config file" hint when the key is only in `appsettings`). `saveSgdbKey`/`clearSgdbKey`.
- **Verified headlessly** (server on :5179, ThunderHorse key): GET shows config fallback
  (`fromConfig=true`, masked `••••••••47ec`); POST a bad key → `ok:false` "SteamGridDB
  rejected the key" **and** DB override flips `fromConfig=false`; POST the real key →
  `ok:true` "verified"; clear → falls back to config. Dashboard HTML serves the new panel.
  Left the DB clean (key still only in config). Build 0 warnings. *Live UI eyeball: pending.*
- **Note:** the SteamGridDB config key in `appsettings.Development.json` is under
  `"SteamGridDB"` (capital DB) but resolves fine — `IConfiguration` keys are case-insensitive.
  The file is now tracked with a blank `ApiKey` placeholder; set a real key locally for dev art fetches.

## Dashboard reorg — Configuration page (2026-06-22)
User: the admin tools (Machines/API keys + Server settings) shouldn't be in the home
page body. Split the single-page dashboard into two **tabs** in the header — **Games**
(home) and **Configuration**:
- Vanilla-JS view switch (no framework): `currentView` (persisted in `location.hash`,
  `#config`), a `render()` dispatcher, and `renderHome()` / `renderConfig()`. `load()` now
  fetches all data once (parallel `Promise.all`), stashes it, and re-renders the active
  view — tab switches don't refetch.
- **Configuration** holds the Server settings card + Machines/API keys panel; **Games**
  holds the game cards. "+ Add game" shows only on Games. Per-game detail still uses the
  machines/commands data (unchanged).
- Static-file change only (no rebuild); verified the running server serves the new markup
  (nav tabs, render funcs). *Live UI eyeball: pending (user refreshes the browser).*

## Product naming: SaveLocker (2026-06-22)
**Official product name locked: SaveLocker.** The codebase (`LocalGameSync.sln`, namespaces,
installer, mutex, paths) remains `LocalGameSync` until a deliberate technical rename (tracked
in [[Future Work]] and [[Console Redesign]]). Vault docs updated to use **SaveLocker** for the
product, `LocalGameSync` only when referring to actual code identifiers/paths. See [[Decisions]]
for the decision + [[Console Redesign]] for the long-term design + productization strategy.

## Agent UI revamp — SaveLocker branding (2026-06-24)
Replaced the native WinForms `AddGamesForm` + `SettingsForm` tray windows with a
fully browser-based agent UI (React + Vite + TypeScript) served locally by the agent.

**Architecture:**
- **`agent-ui/`** — new Vite 6 / React 19 / TypeScript app. Dev port 5177; proxies `/api`
  to `http://localhost:5178`. Build output → `agent-ui/dist/`.
- **`AgentApiServer.cs`** — .NET `HttpListener` on port 5178. Serves static `agent-ui/dist/`
  files (with SPA fallback `→ index.html`) and JSON API routes:
  `state`, `candidates`, `candidates/rescan`, `enroll`, `config` (GET/POST), `register`,
  `games` (GET), `games/{id}/remove`, `games/{id}/folder`, `folder-pick`,
  `candidates/{id}/folder-pick` (opens native dialog **and** updates the in-memory candidate
  cache in one call, so the subsequent enroll uses the correct path).
- **`AgentWindow.cs`** — WinForms form hosting a `WebView2` control. `OnFormClosing`
  **hides** instead of closing (cancel + `Hide()`) so the window can be re-shown from the
  tray. 900×600 fixed size.
- **`TrayApp.cs`** — creates `AgentApiServer` + `AgentWindow`. Tray menu now has a single
  **"Open SaveLocker…"** item instead of separate Add games / Settings items. Navigates the
  WebView2 to `http://localhost:5178`.
- **MSBuild targets** in `LocalGameSync.Agent.csproj`:
  - `BuildAgentUi` (`BeforeTargets="Build"`) — runs `npm run build` in `agent-ui/`.
  - `CopyAgentUiDist` (`AfterTargets="Build"`) — copies `dist/` → `$(OutputPath)agent-ui/`.
  - `CopyAgentUiDistOnPublish` (`AfterTargets="Publish"`) — same into `$(PublishDir)agent-ui/`
    so the installer pipeline includes the React app.

**UI — three-view SPA with sidebar navigation:**
- **Overview** — "Agent Running" with Cpu icon + 3 stat cards (Games Tracked, Saves Backed Up,
  Last Sync). Status header shows CONNECTED/DISCONNECTED + server URL.
- **Add Games** (default) — candidate checklist with source/SteamCloud badges, Rescan,
  "Set save folder…" (calls `candidates/{id}/folder-pick` → native FolderBrowserDialog on the
  STA thread via `SynchronizationContext.Post`), Hide Steam Cloud toggle, Enroll selected.
- **Settings** — server URL + Save, Register/Re-register, Machine Name input, read-only API
  Key + Copy (2 s flash), Start with Windows checkbox, Tracked Games list with
  "Set save folder…" + Remove (amber).

**Design tokens** from the SaveLocker design handoff:
`#2A3238` main bg, `#1E252A` surface, `#0d1114` deep bg, `#129271` accent-green,
`#f4a60d` amber, `#9CA3AF` muted, `#ECEFF1` primary text, `#494949` border.
SaveLocker logo rendered at 34×34px in the sidebar with `border-radius: 5px`.

**SaveLocker branding (user-visible strings, config, registry, installer):**
- `AgentConfig.DefaultDir` → `%PROGRAMDATA%\SaveLocker\` (was `LocalGameSync\`).
- `AutoStart.cs` registry value name → `"SaveLocker"` (was `"LocalGameSync"`).
- `Program.cs` single-instance mutex → `"SaveLocker.Agent"` (was `"LocalGameSync.Agent"`).
- All tray balloon / MessageBox titles use "SaveLocker".
- **Installer** (`installer/LocalGameSync.iss`) — `AppName`, `AppPublisher`, `DefaultDirName`,
  `OutputBaseFilename`, `AppMutex`, `RunValue`, data-dir path all changed to SaveLocker;
  `WizardSmallImageFile=SaveLocker_Logo.png` + `WizardImageFile=SaveLocker_Logo.png` added.
- **`installer/build-installer.ps1`** — step 1 runs `npm run build` in `agent-ui/` before publish.
- **`installer/SaveLocker_Logo.png`** — logo file added (copied from design handoff).

**Key technical notes:**
- Folder picker bridge: `HttpListener` callbacks are MTA thread-pool; `FolderBrowserDialog`
  requires STA. Solved with `TaskCompletionSource<string?>` resolved via
  `SynchronizationContext.Post()` to the WinForms STA thread.
- WebView2 NuGet: `Microsoft.Web.WebView2 1.0.3296.44` — benign MSB3277 warning about
  `WindowsBase` version conflict (WPF DLL vs .NET 9 WinForms; runtime harmless).
- Code namespace / solution file still `LocalGameSync` — deliberate (see [[Future Work]]
  "Technical codebase rename").

## Session log
- **2026-06-25 (session 4):** **Hero downscaling, storage display, per-game retention, version delete.**
  - **Hero image downscaling** — `ArtService`: added `ResizeHeroAsync` using `SixLabors.ImageSharp 3.1.7`
    (downgraded from 4.0.0 which required a paid license). Hero images resized to max 920 px wide,
    JPEG q85, stored as `hero.jpg` overwriting any previous extension. Other asset kinds unchanged.
    `SixLabors.ImageSharp` added to `LocalGameSync.Server.csproj`.
  - **Storage display** — `GameStateDto` gains `TotalStorageBytes: long = 0`. `GetOverviewAsync` does
    a single `GROUP BY` batch query for all game storage totals (no N+1). `GetGameStateAsync` accepts
    a pre-computed total or queries its own SUM. Frontend: sidebar header shows grand total MB; each
    sidebar row shows per-game MB; `GameDetail` card shows "total stored: X MB across N versions";
    versions table + head line changed from bytes to MB. `types.ts` adds `totalStorageBytes` to
    `GameSummary`. Pushed and deployed; all verified on the live server.
  - **Per-game retention limits** — `Game.RetainVersions` (nullable int); additive `ALTER TABLE Games
    ADD COLUMN RetainVersions INTEGER NULL` at startup (pragma-guarded). `PruneVersionsAsync` uses
    per-game limit when set, falls back to `_retainPerGame`. `SetGameRetentionAsync` + endpoint
    `POST /games/{id}/retain?value=N`. `GameDto` + `Mapping` carry the field. Frontend: `Game` type
    gains `retainVersions`; Configuration page gains "Save retention" card (games sorted by storage
    desc, number input per game, Save). `api.setRetention`.
  - **Manual version delete** — `DeleteVersionAsync` refuses head + open-conflict versions;
    `DELETE /games/{id}/versions/{versionId}` endpoint. `GameDetail` versions table: new Delete
    column with amber confirm-gated button on every non-head row. `api.deleteVersion`.
  - Commits: `57cd313` hero downscaling · `6e146f3` storage display · `8b65b54` retention + delete.

- **2026-06-25 (session 3):** **Offline / durable retry queue.**
  - `OfflineQueue.cs` — JSON-backed queue at `%PROGRAMDATA%\SaveLocker\offline-queue.json`.
    One entry per game (deduped by `GameId`); `force=true` is sticky if either the original
    or a duplicate enqueue was forced. Stores retry count + last-attempt timestamp. Thread-safe
    (internal lock); persists after every write; loads on construction (survives agent restarts).
  - `OfflineQueueDrainer.cs` — `System.Threading.Timer` on 30 s period. On each tick, if the
    queue is non-empty, calls `SyncEngine.PushAsync` for each queued game (via `Func<SyncEngine>`
    so it always gets the live engine after `RebuildEngine`). Removes entry on any non-null
    result (success, no-change, or conflict); removes if game was deleted from config or save dir
    gone; records attempt otherwise. Skips tick if a drain is already in progress.
  - `SyncEngine.PushAsync` — added `OfflineQueue? offlineQueue` to the constructor (last optional
    param; existing call sites unchanged). Catches `HttpRequestException` and
    `OperationCanceledException` (HttpClient internal timeout, not user cancel) inside the
    upload try block; logs "queued for retry" and enqueues instead of propagating — tray gets
    a useful message instead of a generic error balloon. Archive `finally` cleanup still runs.
  - `TrayContext` — `OfflineQueue` constructed at field init, passed into `RebuildEngine()` so
    it's shared across engine rebuilds. `OfflineQueueDrainer` started after `RebuildEngine()`,
    disposed in `Dispose()`. Used `System.Threading.Timer` explicitly to resolve the WinForms
    `Timer` ambiguity in the Agent project.
  - **Verified end-to-end** on the real machine: pushed while server unreachable → queue file
    appeared → server restored → drained and logged within 30 s. Commit `9baadf7`.

- **2026-06-25 (session 2):** **Admin password auth, favicon, git hygiene.**
  - **Admin password auth** — replaced the dashboard's `X-Api-Key` auth with a new `AdminPasswordFilter` checking `X-Admin-Password`. Route groups split: agent routes keep `ApiKeyFilter` (machine identity); all dashboard routes move to the new admin filter. Filter passes through freely when no password is configured (open state on first run). Password stored as PBKDF2-SHA256 (100k iterations, salted, `v1:{salt}:{hash}`) in `SettingsService` under `"Admin:PasswordHash"`. New `GET /api/admin/status` (public, returns `{ passwordRequired }`) and `POST /api/admin/password` (admin-gated). `ConfigView.tsx` gains an "Admin password" card (set/change/clear with confirmation). `App.tsx` now auto-loads on mount (no key gate); 401 shows a targeted "wrong password" message. Attribution on resolve/rollback/set-latest now records `"admin"` instead of `CurrentMachine().Name`. Self-delete guard on `DELETE /machines/{id}` removed (admin can delete any machine). See [[Future Work]] — "real admin auth" item now done.
  - **Favicon refresh** — replaced old `favicon.svg` + broken `favicon.png` reference with a full modern set: `favicon.ico` (universal), `favicon-32x32.png` + `favicon-16x16.png` (modern browsers), `apple-touch-icon.png` (iOS), `android-chrome-192x192/512x512.png` + `site.webmanifest` (PWA/Android). `web/index.html` updated with proper `<link>` tags. Old `favicon.svg` removed from tracking.
  - **Git hygiene** — removed 6 binary/stale files from tracking: `SaveLocker dashboard prototype.zip` (6.4 MB), `design_handoff_savelocker/assets/SaveLocker_logo_original.png` (5.3 MB), `src/Server/wwwroot/index.html` + `favicon.png` (stale local build artifacts — Dockerfile builds these fresh from `web/`), `.claude/settings.local.json` (local machine settings). Added `src/Server/wwwroot/` and `.claude/settings.local.json` to `.gitignore` to prevent recurrence. Clarified: the 700 MB the user sees is local artifacts (`node_modules`, `bin/obj`, `installer/dist`) — the git repo itself is only ~11.5 MB. Docker layer sizes (e.g. `node:22-alpine` at 52 MB) are base image pulls, cached between runs; nothing to optimize.
  - **Git push discipline** — established practice: batch commits locally, push once per session (or only for urgent fixes) since each push triggers a full Docker build + Watchtower deploy.
  - Commits: `adb48c5` admin password auth · `bfd608d` git cleanup · `4f30d8d` favicon.

- **2026-06-25 (continued):** **Agent UI polish — settings input clobber fix + header/footer border alignment.**
  - **Settings input clobber** — `SettingsView.tsx`: the 10-second `/api/state` poll was overwriting
    the user's in-progress `serverUrl` / `machineName` typing before they could click Save or Register.
    Fixed by tracking a `dirtyFields` `Set<string>` ref; the sync `useEffect` skips any field the user
    has typed in. Dirty flags are cleared on a successful Save or Register so future polls resume syncing.
  - **Header/footer border alignment** — the horizontal border lines at the top and bottom of the app
    window were misaligned between the sidebar and the content panel because the two independent flex
    columns had different natural heights (sidebar brand: 64 px; StatusHeader: 54 px min-height; footer
    content height vs empty div). Fixed by lifting both the header and the footer out of their respective
    columns into single **shared flex rows** that span the full window width. The border is now one element,
    making misalignment structurally impossible. Brand + status header → one top row; machine name + empty
    right cell → one bottom row. `Sidebar` and `StatusHeader` simplified accordingly (removed own
    borders/minHeight; brand + footer content now live in `App.tsx`).

- **2026-06-25:** **Cleanup, full user-visible rename, installer branding, per-machine paths, folder picker fix, audit log.**
  - Deleted dead WinForms files (`AddGamesForm.cs`, `SettingsForm.cs`, `SaveLocationDialog.cs`) — replaced by React agent UI.
  - All remaining "LocalGameSync" user-visible strings → "SaveLocker": health check, log/config path comments, default DB (`savelocker.db`) with auto-rename shim for existing installs.
  - `installer/LocalGameSync.iss` renamed to `installer/SaveLocker.iss`; branded wizard images generated: `SaveLocker_WizardBg.png` (164×314) and `SaveLocker_WizardSmall.png` (55×58).
  - **Per-machine save paths** — `MachineSavePaths` table (additive startup SQL), SyncService CRUD, new server endpoints, agent two-way sync (reads server path, reports local detection back). Dashboard "Save paths per machine" table. Verified on ThunderHorse and Wideboy with different usernames.
  - **Folder browser fix** — `ShowFolderPickerAsync` was dispatching `FolderBrowserDialog` to a ThreadPool (MTA) thread because `SynchronizationContext.Current` is null when `TrayApp` constructs (before `Application.Run` installs the WinForms pump). Fixed: spawn a dedicated STA thread per dialog, parent to `Application.OpenForms[0]`.
  - **Vite dev server LAN binding** — added `host: '0.0.0.0'` to `vite.config.ts`; dashboard now reachable at `192.168.68.58:5173` from Wideboy.
  - **Audit log view** — `GET /api/audit?limit=200` with LEFT JOIN on machines/games; `AuditView.tsx` with color-coded action badges; "Audit Log" nav tab added.

- **2026-06-24 (continued):** **CI/CD pipeline + unRAID deployment.**
  - **GitHub repo** created at https://github.com/SkorcherX/SaveLocker.
  - **Multi-stage Dockerfile (Phase 3)** — added Node 22 Alpine build stage; `npm ci` +
    `npm run build` in `web/`; `dist/` copied into `src/Server/wwwroot/` before .NET
    publish. React dashboard now baked into the production image.
  - **GitHub Actions** (`.github/workflows/docker-publish.yml`) — triggers on push to
    `main`; logs in to GHCR with `GITHUB_TOKEN`; builds and pushes
    `ghcr.io/skorcherx/savelocker:latest`. No extra secrets needed.
  - **Watchtower** — runs as a companion container on unRAID; polls GHCR every 5 min
    and auto-restarts `savelocker-server` on new image.
  - **Fixes during CI bringup:**
    - Removed unused `logoUrl` import (`ConfigView.tsx`) and `api` import (`GamesView.tsx`)
      that passed locally (Windows, non-strict) but failed `tsc -b` in Docker (Linux).
    - Fixed `**/data/` gitignore pattern silently excluding `src/Server/Data/` on Windows
      (case-insensitive match); changed to `/data/` (root-only). Force-added `Data/` to
      git. **Key gotcha — already in [[Gotchas]].**
    - Fixed unRAID port mapping: container was started with `5080→5080` instead of
      `5080→8080`; recreated via `docker compose` to get correct mapping.
  - **Result:** `git push` → Actions build (~3 min) → Watchtower picks up → zero manual
    steps. Dashboard live at `http://unraid-ip:5080`.


- **2026-06-24 (continued):** **Agent UI polish — tray icon, title bar theming, DPI fix.**
  - **SaveLocker tray/exe icon** — `SaveLocker.ico` added to `src/Agent/Assets/`, set as
    `EmbeddedResource` and `<ApplicationIcon>` in the csproj; `AppResources.cs` loads it with
    `new Icon(stream)` (replaces the old PNG→bitmap→handle roundtrip). The exe, tray icon,
    and `AgentWindow` title bar all show the new icon.
  - **DWM title bar theming** — `AgentWindow.OnHandleCreated` calls `DwmSetWindowAttribute`
    with `DWMWA_USE_IMMERSIVE_DARK_MODE` (Win10+), `DWMWA_CAPTION_COLOR` (#2A3238), and
    `DWMWA_TEXT_COLOR` (#ECEFF1) (Win11+). Title bar now blends with the sidebar colour;
    controls (X/min/max) are white-on-dark.
  - **WebView2 user-data folder** — `EnsureCoreWebView2Async` is now passed an explicit
    `CoreWebView2Environment` with `userDataFolder = %PROGRAMDATA%\SaveLocker\WebView2`.
    Prevents silent init failures when the default location (next to the exe) is locked or
    unwriteable, and catches exceptions via try/catch in `async void OnLoad` (previously
    swallowed silently, causing a black window + ghost tray icon).
  - **High-DPI sizing fix (key gotcha — see [[Gotchas]])** — WinForms `ClientSize` units are
    *physical pixels* even when `DeviceDpi > 96`. WebView2 divides physical px by
    `devicePixelRatio` (= DeviceDpi/96) to get CSS pixels. At 150% DPI (`DeviceDpi=144`),
    `ClientSize = 900×600` produced a 600×400 CSS viewport — the React 900px layout was
    centred at x=−150, showing only ~62px of the 212px sidebar. Fix: scale by `DeviceDpi/96`
    in the constructor: `ClientSize = new Size((int)(900 * DeviceDpi/96f), …)`.
    `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` is set in `TrayApp.Run` but makes
    no difference to the physical-pixel coordinate system in this scenario.
  - **Startup / crash logging** — `AgentLogger.Log` entries added at agent start,
    `AgentApiServer.Start`, and `WebView2` init stages; `async void OnLoad` now catches and
    logs WebView2 exceptions rather than silently dying.
- **2026-06-24:** **Agent UI revamp + SaveLocker branding.** See "Agent UI revamp" section above.
  Replaced WinForms tray windows with React/WebView2 SPA; built `AgentApiServer.cs`,
  `AgentWindow.cs`; added three-view UI (Overview, Add Games, Settings); applied design-handoff
  tokens; renamed all user-visible strings, config paths, registry keys, and installer to
  "SaveLocker"; added SaveLocker wizard logo to Inno installer; added `CopyAgentUiDistOnPublish`
  MSBuild target so the publish pipeline includes the React app. *(Live UI check: done — agent
  running, UI confirmed working on ThunderHorse at 150% DPI.)*
- **2026-06-24:** **Console redesign Phase 2 — React dashboard built, feature parity reached.**
  - Design handoff (`design_handoff_savelocker/`) reviewed and approved: two high-fidelity
    HTML prototypes (Games dashboard + Configuration), full design token spec.
  - **`web/` project created** — Vite 8 + React 19 + TypeScript + Tailwind CSS v4
    (`@tailwindcss/vite`). No shadcn/ui (design tokens are too custom; Tailwind inline
    styles match the handoff pixel-precisely).
  - **Components:** `NavBar`, `GamesSidebar` (220px, cover art + name + badges),
    `GamesView` (sidebar + detail panel), `GameDetail` (game card + Machines + Commands
    + Versions tables), `ConfigView` (SteamGridDB card + Machines/API keys table).
  - **API client** (`api.ts`) — typed, covers all endpoints the old console used: overview,
    conflicts, machines, commands, settings, per-game versions, and all mutations (refresh
    art, set-enabled, delete-game, add-game, set-save-dir, set-latest, force-release,
    resolve-conflict, queue-command, delete-machine, save/clear SGDB key).
  - **Vite proxy:** `/api` and `/art` → `:5179`. Dev server on `:5173`.
  - **Verified live** against the real DB (Octopath Traveler 0, real cover art via `/art`
    proxy, three machines visible, Recent Remote Commands + Versions tables populated).
  - Legacy `src/Server/wwwroot/index.html` — retired; file was untracked and is no longer present.
  - Configuration page logo removed (user request — 2026-06-24 same session).
  - **Phase 3 (Docker fold) remains:** `npm run build → dist/ → wwwroot/` + Dockerfile
    multi-stage node build. Lands with deployment-hardening milestone.
  - See [[Console Redesign]] for full plan + file structure.

- **2026-06-23:** First real-machine deployment on **Wideboy** (second PC).
  - **Installed agent** — `LocalGameSync-Agent-Setup-0.1.0.exe`. Game synced
    (Octopath Traveler 0) but pull from dashboard failed with access denied.
  - **Diagnosed OneDrive RestoreArchive bug** — `RestoreArchive` staged backup
    and staging dirs inside the OneDrive-managed parent (`Octopath_Traveler0\`).
    `Directory.Move` on the `Steam` save folder is blocked by OneDrive's reparse
    points even when OneDrive is not running. Fixed by adding a `stagingRoot`
    param to `RestoreArchive` and switching to file-by-file copy; `SyncEngine`
    passes `_tempDir` (`C:\ProgramData\LocalGameSync\tmp`). **Verified — sync
    succeeded on Wideboy.** See [[Gotchas]].
  - **File-based logging** — `AgentLogger.cs`: rolling 1 MB log at
    `C:\ProgramData\LocalGameSync\agent.log` (keeps one `.old`), full exception
    stack traces. Wired into `SyncEngine` (both tray and CLI), `TrayApp`
    `FireAndForget`, and `CommandPoller.TickAsync`. New `log` CLI sub-command
    tails the last 50 lines (`--n N` for more). Needed to diagnose the above
    bug; also replaces the unreliable WinExe stdout workaround. See [[Gotchas]].
  - **Console auto-refresh panel collapse fix** — dashboard was refreshing every
    15 s via `setInterval` and `load()` → `render()` replaced `#app` innerHTML,
    collapsing all open game detail panels. Fixed by tracking open panels in
    `openDetails` (a JS `Set`), splitting `toggle()` into toggle + `showDetail()`,
    and re-opening panels after each render. Detail data is re-fetched on each
    refresh (kept fresh).
  - **Discovered old installer binary lacked CLI** — `whoami`/`list`/`pull`
    produced no output because the installed binary predated the CLI commands;
    rebuilding and reinstalling resolved it.
  - Rebuilt and shipped a new installer that includes all of the above.

- **2026-06-22:** Built WS2 scanning core: binary + text VDF readers, GameScanner
  (Steam shortcuts + installed games + save-root heuristic), `scan` CLI. Verified
  against the real machine (found Octopath via shortcuts.vdf). Fixed manifest
  case-collision crash. Built the tray **"Add games…"** picker (`AddGamesForm.cs`),
  user-verified. Then started **WS4 (dashboard admin)**: added `/api/machines`,
  enable/disable, and `set-latest` endpoints; rebuilt the dashboard with
  enable/disable + delete + Machines table + initial-sync wizard + "Set as Latest"
  / Latest badge. Endpoints verified headlessly. Then built **WS5 (agent command
  channel)**: `AgentCommand` queue + agent/dashboard endpoints, agent `CommandPoller`
  (game-list reconciliation + pull/push/sync/scan execution), dashboard per-machine
  action buttons + command log. Verified end-to-end on the real machine (dashboard
  Scan → agent ran it → Done; agent auto-adopted the dashboard-created Octopath).
  Then built the **save-folder mapping** (agent `SaveLocationDialog` folder+file browser
  in `SettingsForm`/`AddGamesForm`; console-suggested `Game.SuggestedSaveDir` +
  `/save-dir` endpoint + dashboard add/edit; agent reconcile maps/ backfills it) — verified
  console→agent auto-map. Fixed the dashboard save-path Edit dropping backslashes.
  Finished **WS3 (SteamGridDB art)**: `ArtService` fetch/cache + art fields + refresh
  endpoint + dashboard covers — verified with a real key, **user-confirmed on dashboard**.
  Saved a memory to always stop/restart the running agent+server around builds (incl. the
  `dotnet run` apphost exe, which a `dotnet.exe`-only filter misses). Added **machine
  (user/key) deletion** in the console (delete endpoint + dashboard Machines panel +
  self-delete guard). Live UI pass user-confirmed (SaveLocationDialog, dashboard controls).
- **2026-06-21:** Built PoC (phases 0–5), verified end-to-end with real Octopath
  saves. Added pull-guard, case-insensitive game matching, delete-game,
  set-server/whoami. Planned UX phase + locked decisions ([[Decisions]]). Built &
  shipped WS1 tray Settings/Connect window; fixed DPI clipping + clipboard STA bug.

## Milestone queue — next to tackle
UX phase functionally complete and deployed. Queue, in priority order:

1. ~~**WS1 polish** — first-run prompt + auto-start on login~~ **DONE 2026-06-22.** ([[Future Work]])
1b. ~~**Agent installer** (Inno Setup)~~ **DONE 2026-06-22; real-machine tested 2026-06-23** on Wideboy. ([[Decisions]])
2. ~~**Server settings in the dashboard** — SteamGridDB API key~~  **DONE 2026-06-22.** ([[Future Work]])
2b. ~~**Console auto-refresh collapses detail panels**~~ **FIXED 2026-06-23** (`openDetails` Set + `showDetail()`).
2c. ~~**File-based agent logging**~~ **DONE 2026-06-23** (`AgentLogger`, `log` CLI command, rolling 1 MB).
2d. ~~**OneDrive RestoreArchive access denied**~~ **FIXED 2026-06-23** (file-by-file copy, staging in `_tempDir`).
3. ~~**EF Core migrations**~~ **DONE 2026-06-24** — `dotnet-ef` 9.0.9, `InitialSchema` migration captures
   full current schema; `Program.cs` uses `db.Database.Migrate()` with a bootstrap shim that seeds
   `__EFMigrationsHistory` on pre-migration DBs (ThunderHorse/Wideboy) so the first `Migrate()` call
   is a no-op rather than failing to recreate existing tables. 0 warnings. ([[Future Work]])
4. ~~**Console redesign Phase 3**~~ **DONE 2026-06-24** — multi-stage Dockerfile: Node
   `npm run build → dist/` baked into `src/Server/wwwroot/` before .NET publish. React
   dashboard now served by the production container with no separate dev server.
4b. ~~**GitHub repo + CI/CD pipeline**~~ **DONE 2026-06-24** — repo at
   https://github.com/SkorcherX/SaveLocker. GitHub Actions builds on every push to `main`
   and pushes image to GHCR (`ghcr.io/skorcherx/savelocker:latest`). Watchtower on unRAID
   auto-pulls every 5 min. Fixed gitignore `Data/` vs `data/` case collision (Linux
   Docker build was silently excluding `src/Server/Data/`). Server live on unRAID at
   port 5080 (`5080:8080` container mapping). `git push` is now the full deploy.
4c. ~~**Admin auth** — real password distinct from machine API keys~~ **DONE 2026-06-25 (session 2).**
   `AdminPasswordFilter`, PBKDF2-SHA256, set from ConfigView.
5. ~~**Per-machine save-path storage**~~ **DONE 2026-06-25** — `MachineSavePaths` table
   (additive startup SQL); `GET /api/games` injects this machine's stored path into each
   `GameDto`; new endpoints for dashboard set/clear; agent reconcile uses server path as
   highest priority and reports locally-detected paths back to the server (two-way sync).
   Dashboard "Save paths per machine" table replaces the old single-path row. **Verified
   on ThunderHorse and Wideboy** — separate per-user paths stored and used correctly.
6. ~~**Audit-log view**~~ **DONE 2026-06-25** — `GET /api/audit?limit=200` LEFT JOINs
   machines + games to resolve names server-side. New `AuditView.tsx` React component:
   timestamp, machine, game, action badge (color-coded by category), detail. New "Audit
   Log" nav tab in the dashboard. Verified — endpoint responds, UI renders all events.
7. ~~**Offline / durable retry queue**~~ **DONE 2026-06-25 (session 3).** `OfflineQueue.cs` +
   `OfflineQueueDrainer.cs`. `SyncEngine.PushAsync` catches `HttpRequestException` /
   timeout and enqueues to `%PROGRAMDATA%\SaveLocker\offline-queue.json` instead of
   bubbling an error. `OfflineQueueDrainer` background timer (30 s) drains the queue
   automatically when the server comes back. Deduped by `GameId`; `force=true` is sticky;
   retry count + last-attempt timestamp persisted. **Verified end-to-end:** push queued
   while server down → drained and logged on reconnect.
8. ~~**Hero image downscaling**~~ **DONE 2026-06-25 (session 4).** `ArtService.DownloadAsync`
   now resizes hero images to max 920 px wide (aspect-ratio preserved) and stores as JPEG
   quality 85 (~100–200 KB vs ~9.5 MB original). Uses `SixLabors.ImageSharp 3.1.7`
   (pure .NET, cross-platform — works in the Linux Docker container). Other asset kinds
   (grid, logo, icon) unchanged. Trigger **Refresh art** per game to replace old full-res files.
9. ~~**Per-game retention limits + manual version delete**~~ **DONE 2026-06-25 (session 4).**
   - `Game.RetainVersions` (nullable int): per-game override; null → server global default
     (`Storage:RetainVersionsPerGame`, default 10). Added via additive `ALTER TABLE` at
     startup (pragma-guarded column check on `Games`).
   - `PruneVersionsAsync` respects per-game limit when set.
   - `POST /games/{id}/retain?value=N` — set limit (blank/absent = reset to default, 0 = unlimited).
   - `DELETE /games/{id}/versions/{versionId}` — delete a specific version; refuses head and
     open-conflict versions.
   - **Configuration page** — new "Save retention" card: games sorted by storage used (desc),
     editable keep-count input per game (blank = default, 0 = unlimited), Save button.
   - **Game detail** — Delete button on every non-head version (amber, confirm-gated).
10. **Lease auto-renew / heartbeat** — leases are fixed at 6 h. If a play session runs
    longer the lease silently expires, letting another machine sync without a warning.
    Fix: renew the lease on a timer while the game process is still running. See [[Future Work]].
11. **Installer artwork polish** — branded wizard images exist as a first pass
    (`SaveLocker_WizardBg.png` 164×314, `SaveLocker_WizardSmall.png` 55×58); user wants
    polished final artwork. See [[Future Work]].
12. **Technical codebase rename** — namespaces, `.sln`, `.csproj` filenames, exe name
    still say `LocalGameSync`. User-visible strings are all `SaveLocker` now (done
    2026-06-25). Full rename is a productization-phase task (see [[Future Work]]).
13. **Code-signing** — installer + exe unsigned; SmartScreen warns on first run for
    other users. User unfamiliar with the process; will be walked through it when ready.

See [[UX Roadmap]] / [[Future Work]] for the full backlog.

## How to resume
- Run server: `cd src/Server && dotnet run` → API on http://localhost:5179.
- Run dashboard dev server: `cd web && npm run dev` → http://localhost:5173 (proxies `/api` to :5179). LAN-accessible at `http://192.168.68.58:5173` (host bound to `0.0.0.0`).
- Build installer: `.\installer\build-installer.ps1` → `installer/dist/SaveLocker-Agent-Setup-0.1.0.exe`.
- Build agent (dev): `dotnet build src/Agent/LocalGameSync.Agent.csproj` (use `--no-incremental`; **stop the running agent/server first** — they lock the DLLs).
- Re-read [[Gotchas]] before touching builds/paths. [[CLI Reference]] for commands.
