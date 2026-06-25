# Progress

Back to [[Home]]. Last updated: 2026-06-24.

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
- **Agent integration — 10 checks** (`.verify/run-agent-tests.ps1`): manifest
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
  - Legacy `src/Server/wwwroot/index.html` untouched.
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
4. **Console redesign Phase 3** — fold `web/` build into the Docker image (multi-stage:
   Node `npm run build → dist/`, .NET `COPY dist/ src/Server/wwwroot/`). Lands with the
   deployment-hardening milestone below. See [[Console Redesign]].
4b. **Deployment hardening** — Dockerize the server + deploy to unRAID; CloudFlare Tunnel
   + Access (Google email allowlist) per [[Decisions]]; real admin auth distinct from
   machine keys.
5. **Stretch / nice-to-have** — per-machine save-path storage; hero-thumbnail vs full-res;
   audit-log view; durable offline retry queue ([[Future Work]]).

See [[UX Roadmap]] / [[Future Work]] for the full backlog.

## How to resume
- Run server: `cd src/Server && dotnet run` → dashboard http://localhost:5179.
- Build agent: `dotnet build src/Agent/LocalGameSync.Agent.csproj` (use
  `--no-incremental`; **stop the running agent/server first** — they lock the DLLs).
- Re-read [[Gotchas]] before touching builds/paths. [[CLI Reference]] for commands.
