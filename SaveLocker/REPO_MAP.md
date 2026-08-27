# SaveLocker — Repo Map

Static layout. Update only when adding or removing entire modules.

Test counts live in [[Build and Run]] → Suite baseline, deliberately not here — they move every
session and a stale number is worse than none.

```
SaveLocker/
├── src/
│   ├── Shared/                          # SaveLocker.Shared.csproj
│   │   ├── Contracts.cs                 # Wire DTOs — shared by server + agent
│   │   ├── AgentEventCodes.cs           # The fixed vocabulary of agent event codes (dedupe keys)
│   │   ├── SaveArchive.cs               # Content hashing + atomic zip restore + per-file manifest
│   │   │                               #   (`ComputeManifest`) for delta uploads
│   │   ├── ManifestLoader.cs            # Ludusavi manifest downloader + cloud/tag parsing
│   │   ├── PathResolver.cs              # Expands manifest placeholders (<winAppData>, <base>) to real
│   │   │                               #   dirs, trimmed at the first wildcard so there is a dir to watch
│   │   └── WinePrefix.cs                # drive_c + which Wine user the game runs as — needed before any
│   │                                   #   manifest token can be resolved inside a prefix
│   │
│   ├── Server/                          # SaveLocker.Server.csproj (net10.0, Docker)
│   │   ├── Program.cs                   # ALL minimal-API endpoints + DI wiring
│   │   ├── Data/
│   │   │   ├── AppDbContext.cs          # EF Core context
│   │   │   └── Entities.cs             # Machine, Game, SaveVersion, SaveVersionFile (per-version
│   │   │                               #   per-file delta-upload baseline), Lease, ConflictFlag,
│   │   │                               #   AuditLog, AgentCommand, AppSetting, MachineSavePath
│   │   ├── Migrations/                  # EF migrations (InitialSchema + incremental)
│   │   ├── Services/
│   │   │   ├── SyncService.cs           # Core: lease, upload, conflict, prune, resolve
│   │   │   ├── EnrollmentService.cs     # Single-use enrollment tokens → machine API key
│   │   │   ├── Tokens.cs                # Per-machine API keys + PBKDF2 password hashing. The on-disk
│   │   │   │                           #   `v1:` format is load-bearing — see Gotchas
│   │   │   ├── HealthService.cs         # Agent heartbeats + events; dedupes and auto-resolves
│   │   │   ├── ConflictEscalationPolicy.cs # When an open conflict becomes "escalated"
│   │   │   │                               #   (`Conflicts:EscalationAfterSeconds`, default 6 h)
│   │   │   ├── GlobConfig.cs            # Save-file exclude globs: per-game text + `Sync:DefaultExcludeGlobs`
│   │   │   ├── ArchiveStore.cs          # {root}/{gameId}/{versionId}.zip on disk
│   │   │   ├── ArtService.cs            # SteamGridDB fetch + cache to /data/art/
│   │   │   ├── BackupService.cs         # Nightly VACUUM INTO SQLite snapshots + retention
│   │   │   ├── AgentInstallerService.cs # Hosts installer binaries for agent auto-update. PLATFORM-
│   │   │   │                           #   SLOTTED: win-x64 at the root, linux-x64/ beside it
│   │   │   ├── AgentInstallerPollerService.cs # Polls GitHub for a newer installer, refreshes the
│   │   │   │                                  #   hosted copy when enabled
│   │   │   ├── PublicUrl.cs             # The URL an agent on ANOTHER machine should use — enrollment
│   │   │   │                           #   policy file + hosted-installer download link
│   │   │   ├── SettingsService.cs       # DB-backed key/value (DB overrides appsettings)
│   │   │   ├── LeaseSweeperService.cs   # Hourly BackgroundService: clear stale leases
│   │   │   └── Mapping.cs              # Entity → DTO mapping helpers
│   │   ├── BuildInfo.cs                 # What this build is: env (baked by CI) -> assembly -> "dev"
│   │   ├── Dockerfile                   # Multi-stage: Node (web/) + .NET SDK + aspnet runtime.
│   │   │                               #   Takes SAVELOCKER_VERSION/_COMMIT/_BUILT_AT build args
│   │   ├── appsettings.json             # Storage, Backup, AgentUpdate config sections
│   │   └── openapi.json                 # Committed OpenAPI snapshot; regenerate after API changes
│   │
│   ├── Agent.Core/                      # SaveLocker.Agent.Core.csproj (net10.0 — NO WinForms)
│   │   │                               # The platform-neutral sync brain, shared by both hosts.
│   │   ├── SyncEngine.cs                # push / pull / pre-launch / post-exit
│   │   ├── SaveSettler.cs               # Settle gate: wait for the game to finish writing
│   │   ├── GameActivity.cs              # THE one answer to "is this game running right now?" — every
│   │   │                               #   surface that could restore over a live save asks this
│   │   ├── ApiClient.cs                 # Typed HTTP client for server API. ApiClient.For(config)
│   │   │                               #   is the one every caller uses — it carries the TOFU pin
│   │   ├── ServerHttp.cs                # The single HttpClient factory, so TLS policy cannot differ
│   │   │                               #   between callers
│   │   ├── ServerTrust.cs               # TOFU pin of the server's TLS key: warn, never block
│   │   ├── ServerOrigin.cs              # What counts as "the same server" — what credentials bind to
│   │   ├── LocalAuth.cs                 # Token + Host/Origin guard for the agent's OWN API. That API
│   │   │                               #   manages the machine, so loopback alone is not a defence
│   │   ├── StateDirSecurity.cs          # Locks the state dir's ACL to its creator (WA-03). See Gotchas
│   │   ├── AtomicFile.cs                # Whole-file writes no reader can catch half-finished
│   │   ├── AgentStateLock.cs            # Cross-PROCESS state lock — the daemon and the launch wrapper
│   │   │                               #   are separate processes sharing every state file
│   │   ├── HealthReporter.cs            # Durable pending events + heartbeat. A headless box cannot
│   │   │                               #   toast, so failures go to the console (Decisions §2)
│   │   ├── SaveDirSanity.cs             # "That's a Wine PREFIX, not a save folder" + size backstop
│   │   ├── SavePathGuard.cs             # The hard floor: paths that can NEVER be a save folder,
│   │   │                               #   however they arrived
│   │   ├── CommandPoller.cs             # 20 s poll: reconcile game list + run commands
│   │   ├── AgentApiServer.cs            # ASP.NET minimal API on :5178 + OpenAPI + agent-ui files
│   │   ├── PathBrowser.cs               # Directory listing for the UI's path browser — a Deck has no
│   │   │                               #   folder dialog and no usable terminal in Game Mode
│   │   ├── AgentConfig.cs               # JSON config at %PROGRAMDATA%\SaveLocker\config.json
│   │   ├── Detection.cs                 # Ludusavi manifest wrapper
│   │   ├── UpdateChecker.cs             # Polls /api/agent/latest, downloads the platform's payload
│   │   ├── OfflineQueue.cs              # JSON retry queue: …\SaveLocker\offline-queue.json
│   │   ├── OfflineQueueDrainer.cs       # 30 s timer drains queue when server reachable
│   │   ├── LeaseWarningStore.cs         # Durable "another machine has this checked out". On DISK
│   │   │                               #   because the Linux launch wrapper is a separate,
│   │   │                               #   short-lived process from the daemon — held in memory,
│   │   │                               #   its warnings reached no UI at all. 24 h expiry.
│   │   ├── AgentLogger.cs               # Rolling agent.log
│   │   ├── AgentCli.cs                  # Shared one-shot commands (register/push/pull/status/…)
│   │   ├── CliArgs.cs                   # Minimal command-line parser
│   │   ├── Enroller.cs                  # Candidate → server game + tracked game
│   │   ├── FileLockProbe.cs             # "Is anyone still writing?" — FileShare (Win) / /proc (Linux)
│   │   ├── SteamLayout.cs               # The parts of Steam's on-disk layout that are identical on
│   │   │                               #   every platform. FINDING Steam is not here — that is a
│   │   │                               #   registry read on Windows, known dirs on Linux (IGameScanner)
│   │   ├── SteamVdf.cs                  # Binary VDF parser (pure)
│   │   ├── SteamTextVdf.cs              # Text VDF parser (libraryfolders.vdf, *.acf)
│   │   ├── SteamShortcuts.cs            # shortcuts.vdf reader + the signed→unsigned AppID trap
│   │   ├── Watchers.cs                  # Debounced FileSystemWatcher + ProcessWatcher
│   │   ├── ScanCandidate.cs             # Discovery DTO (scanning itself is platform-specific)
│   │   └── Platform.cs                  # IAutoStart, IGameScanner — impls injected by the host
│   │
│   ├── Agent/                           # SaveLocker.Agent.csproj (net10.0-windows, WinForms)
│   │   │                               # Windows host: UI + platform impls. → Agent.Core
│   │   ├── Program.cs                   # Entry: no args → tray; args → AgentCli
│   │   ├── TrayApp.cs                   # Tray icon, menu, engine lifecycle; injects the Windows impls
│   │   ├── UiDispatcher.cs              # The single owning thread for EVERY WinForms object — icon,
│   │   │                               #   menu, WebView2 window, message boxes, folder dialog
│   │   ├── AgentWindow.cs               # WinForms form hosting WebView2 (900×600, DPI-scaled)
│   │   ├── GameScanner.cs               # IGameScanner: shortcuts + Steam libraries + save-root heuristic
│   │   ├── AutoStart.cs                 # IAutoStart: HKCU Run-key toggle ("Start with Windows")
│   │   ├── FolderPicker.cs              # WinForms FolderBrowserDialog on an STA thread
│   │   └── AppResources.cs             # Embedded icon + asset loader
│   │
│   └── Agent.Linux/                     # SaveLocker.Agent.Linux.csproj → binary `savelocker`
│       │                               # Headless Proton agent (net10.0). No tray, no toast:
│       │                               # the daemon serves the same React UI on :5178.
│       ├── Program.cs                   # daemon / run / doctor / autostart / update, else → AgentCli
│       ├── Daemon.cs                    # Headless host: API server + poller + drainer + watchers
│       ├── ProtonRun.cs                 # `savelocker run -- %command%` Steam launch wrapper
│       ├── Updater.cs                   # Stage (download, verify, unpack, smoke-test); the SWAP runs
│       │                               #   from the unit's ExecStartPre next start — see Gotchas
│       ├── LinuxGameScanner.cs          # IGameScanner: shortcuts.vdf; in-prefix + portable saves
│       ├── SteamRoots.cs                # Native + Flatpak Steam roots; compatdata lookup
│       ├── HeroicRoots.cs               # Locates Heroic and answers the only question that matters
│       │                               #   per game: which Wine prefix does it run in?
│       ├── HeroicLibrary.cs             # Heroic's library files (legendary/gogdl/nile) → games
│       ├── DeckyPlugin.cs               # Keeps the Decky plugin updated, through the agent's own
│       │                                #   update channel. Decky hands a non-_root plugin's files
│       │                                #   to the desktop user and hot-reloads on change, so
│       │                                #   WRITING the files is the install. Every destination is
│       │                                #   proven writable BEFORE any byte lands — the plugin dir
│       │                                #   is root-owned 755 and plugin.json is root's outright
│       ├── Doctor.cs                    # Diagnoses the whole chain (the only UI a Deck has)
│       ├── SystemdAutoStart.cs          # IAutoStart: systemd --user unit
│       └── Ui/                          # `savelocker ui` — Game Mode surface (SDL + GL + ImGui)
│           ├── UiApp.cs                 # Window, gamepad nav glue, the four screens
│           ├── Theme.cs                 # THE source of truth for colour/type/metrics. Palette is
│           │                            #   lifted from web/src/index.css so console + agent UI +
│           │                            #   Deck stay in lockstep. No literal colour lives elsewhere
│           ├── Widgets.cs               # Component set, painted over InvisibleButton to keep
│           │                            #   gamepad nav while looking nothing like stock ImGui
│           ├── ImGuiInternal.cs         # Direct [DllImport("cimgui")] to the internal API ImGui.NET's
│           │                            #   binding omits. THE nav fix — do not "solve" it by
│           │                            #   upgrading the package (Gotchas)
│           ├── Icons.cs                 # lucide-equivalent glyphs as vector paths — no atlas
│           ├── Art.cs                   # Embedded images decoded + uploaded as GL textures
│           ├── Sound.cs                 # Interface sounds — a console UI that answers silently
│           │                            #   feels inert
│           ├── Wav.cs                   # Minimal RIFF/WAVE reader (loads SteamOS's own UI sounds)
│           ├── Gallery.cs               # `ui --gallery`: every widget in every state (dev only)
│           ├── NavDebug.cs              # `ui --nav-debug`: live read-out of where the nav cursor is
│           ├── Screenshot.cs            # `ui --screenshot`: framebuffer → PNG, hand-rolled encoder
│           ├── SettingsScreen.cs        # Settings: read-only connection, settle stepper,
│           │                             #   autostart toggle, remove tracked games
│           └── Fonts/                   # Inter + JetBrains Mono (embedded, SIL OFL)
│
├── web/                                 # React admin dashboard
│   │                                   # Stack: Vite 8, React 19, TypeScript, Tailwind v4
│   ├── src/
│   │   ├── api.ts                       # Typed fetch client (all server endpoints)
│   │   ├── api-types.ts                 # GENERATED from /openapi/v1.json → npm run gen:api
│   │   ├── types.ts                     # Thin aliases over api-types.ts
│   │   ├── releaseSeen.ts               # localStorage "have these notes been read?" for the dot
│   │   ├── versionSkew.ts               # Agent vs console version comparison. Only NEWER-than-
│   │   │                               #   console warns; a 9.9.9-ci tarball is a TEST BUILD
│   │   ├── help/                        # Help KB: one .md per article + index.ts registry
│   │   ├── releases/                    # Release notes: one .md per RELEASE + index.ts registry.
│   │   │                               #   Same file is the GitHub Release body (release.yml
│   │   │                               #   body_path) — written once, cannot drift.
│   │   └── components/
│   │       ├── NavBar.tsx               # Logo, Games/Config/Audit Log tabs, Connect/Refresh
│   │       ├── GamesSidebar.tsx         # 220 px left sidebar: cover art, name, badges
│   │       ├── GamesView.tsx            # Sidebar + detail panel layout
│   │       ├── GameDetail.tsx           # Game card, Machines, Commands, Versions, save paths
│   │       ├── ConfigView.tsx           # SteamGridDB, Console build card, Machines/API keys,
│   │       │                           #   Agent Updates (TWO rows since v0.5.5: win-x64 +
│   │       │                           #   linux-x64). Console card sits ABOVE Machines on
│   │       │                           #   purpose: console + fleet versions read together
│   │       ├── AuditView.tsx            # Audit log table with color-coded action badges
│   │       ├── HelpView.tsx             # Renders the help/ KB articles
│   │       └── WhatsNewView.tsx         # Release notes; flags a dev build as not-a-release
│   └── vite.config.ts                  # Proxy /api + /art → :5179; host 0.0.0.0 (LAN)
│
├── agent-ui/                            # React agent UI — served by BOTH hosts (tray WebView2 and
│   │                                   #   the Linux daemon). Stack: Vite, React 19, TypeScript
│   └── src/
│       ├── App.tsx                      # Root, sidebar nav, 10 s state poll, live version string
│       ├── api-types.ts                 # GENERATED from the AGENT's OpenAPI → npm run gen:api.
│       │                               #   CI runs `gen:api -- --check`, so a drifted file fails the
│       │                               #   build. Do not hand-edit. The script hardcodes :5178 —
│       │                               #   read Gotchas before regenerating
│       ├── types.ts                     # Thin aliases over api-types.ts
│       └── components/
│           ├── Sidebar.tsx · StatusHeader.tsx
│           ├── OverviewView.tsx · AddGamesView.tsx · SettingsView.tsx
│           ├── LaunchSetupCard.tsx      # The Steam launch-options command + Copy. Renders nothing
│           │                           #   on Windows. Target of logs/2026-08-15_decky-plugin.md
│           ├── DeckyPluginCard.tsx      # The optional Decky plugin: what it adds, and whether it
│           │                           #   is installed (GET /api/decky — local reads, no network).
│           │                           #   Collapsed by default: expanded it pushed the setup
│           │                           #   instructions off a Deck's screen
│           └── PathBrowserModal.tsx     # UI for PathBrowser — the Deck's only folder picker
│
│                                     # NOTE: the Decky plugin lives in its OWN repository,
│                                     #   github.com/SkorcherX/SaveLocker-Decky — Decky's plugin
│                                     #   database tracks plugins as submodules whose ROOT is the
│                                     #   plugin, so it cannot be a subdirectory here. The RULE it
│                                     #   depends on stays in Agent.Core/LaunchOptions.cs.
├── installer/
│   ├── SaveLocker.iss                   # Inno Setup 6: machine-wide, UAC, uninstall reverts all
│   └── build-installer.ps1             # dotnet publish + ISCC → installer/dist/SaveLocker-Agent-Setup-{ver}.exe
│
├── packaging/linux/
│   ├── build-linux.sh                   # Self-contained publish → tarball (build on OLDEST glibc)
│   ├── install.sh                       # Installs to ~/.local/share/SaveLocker — never /usr
│   └── savelocker.service               # systemd --user unit. ONE file: SystemdAutoStart generates
│                                       #   from this, so the packaged and generated units cannot drift
│
├── tests/                               # Counts + baselines: [[Build and Run]] → Suite baseline
│   ├── run-agent-tests.ps1             # Integration checks. Runs on BOTH OSes: PowerShell Core
│   │                                   #   runs on Linux, so the same script drives the Windows
│   │                                   #   agent and the Linux agent. Needs a server on :5179.
│   ├── run-winagent-tests.ps1          # Windows agent bug bounty (WA-*). Owns :5189 (+5190–5198)
│   │                                   #   and `.verify-winagent`. Drives two REAL tray processes,
│   │                                   #   so it needs an interactive desktop or it silently skips.
│   ├── run-server-bugbounty-tests.ps1  # Server-side bug bounty — auth, enrollment, update routes.
│   ├── run-delta-upload-tests.ps1      # Per-file delta upload: self-healing baseline, byte-exact
│   │                                   #   reconstruction across a full+full+delta chain, deletion,
│   │                                   #   the size/count floor, a diverged push staying full, and a
│   │                                   #   hostile delta payload refused. Own server on :5185.
│   ├── run-hardening-tests.ps1         # SECURITY. A symlink must not leak its target into the
│   │                                   #   archive, and the restore's delete pass must not reach
│   │                                   #   THROUGH one and delete files OUTSIDE the save folder.
│   │                                   #   Plus zip-slip. Run it against pre-fix code: it must
│   │                                   #   FAIL. Own server on :5182.
│   ├── run-local-api-tests.ps1         # The agent's OWN API (:5178). It rewrites config and
│   │                                   #   re-registers the machine, so an unauthenticated caller
│   │                                   #   owns the box — it used to answer every one of them.
│   ├── run-concurrency-tests.ps1       # Cross-PROCESS state: daemon vs. launch wrapper. The sharp
│   │                                   #   one is the lost update — a daemon writing stale config
│   │                                   #   erases a parent version another process just recorded.
│   ├── run-health-tests.ps1            # Heartbeat, real conflict, dedupe, self-healing, and a push
│   │                                   #   while the server is STOPPED (the durable report). Owns
│   │                                   #   its server on :5181 — it must stop it.
│   ├── run-enrollment-tests.ps1        # Mint → enroll → single-use / revoked / expired / forged all
│   │                                   #   refused. Runs on both OSes; in CI.
│   ├── run-enrollment-tls-tests.ps1    # The TOFU pin, over a real HTTPS server it starts itself.
│   │                                   #   Needs a trusted dev cert, so LOCAL only — http has no
│   │                                   #   identity to pin, so the suite above cannot cover it.
│   ├── verify-password-compat.ps1      # Builds a server from an older git ref, has it hash an
│   │                                   #   admin password, then asserts the CURRENT code still
│   │                                   #   verifies it (guards the PBKDF2 on-disk `v1:` format)
│   ├── cross-os/crossos.ps1            # THE cross-OS round-trip: -Leg author|roundtrip|confirm.
│   │                                   #   One leg per OS; CI chains them by handing the SERVER'S
│   │                                   #   OWN STATE (SQLite DB + archive store) between runners as
│   │                                   #   an artifact — runners cannot share a network.
│   ├── detection/                      # Save-detection accuracy sweep against the real manifest,
│   │   │                               #   plus pinned regression cases. NOT in CI (needs the
│   │   │                               #   manifest + a sample size); quote the sample with the score.
│   │   ├── run-detection-tests.sh · pinned-cases.tsv · SaveLocker.DetectionHarness/
│   └── linux/                          # Fake-game harness — no Steam/Proton/GPU/Deck needed
│       ├── run-linux-tests.sh          # Starts its own server, fake HOME
│       ├── make-fixtures.py            # Builds compatdata tree + binary shortcuts.vdf
│       ├── slow-game.sh                # Game that flushes after exit (settle gate + lock probe)
│       ├── run-ui-wslg.sh              # `savelocker ui` under WSLg (needs an explicit RID — Gotchas)
│       └── manifest.yaml               # Fixture Ludusavi manifest
│
├── .github/workflows/
│   ├── ci.yml                           # PR + main push: dotnet, web, agent-ui, docker, the Linux
│   │                                   #   package, the agent suites and the cross-OS chain.
│   │                                   #   paths-ignore skips vault-only commits — see Gotchas
│   ├── docker-publish.yml               # main push + v* tag → ghcr.io/skorcherx/savelocker:latest
│   └── release.yml                      # v* tag → TWO jobs: build-installer (windows) and
│                                       #   build-linux (tarball + checksums + attestation)
│
├── .agents/AGENTS.md · AGENTS.md · CLAUDE.md   # Agent instructions (paths-ignore'd by both workflows)
├── docs/screenshots/                    # README imagery
├── SaveLocker/                          # THIS Obsidian vault
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
- **ImGui.NET is 1.90.8.1 and stays there** (transitively, via `Silk.NET.OpenGL.Extensions.ImGui`).
  Upgrading was tried and reverted; the nav fix lives in `Ui/ImGuiInternal.cs`. See `Gotchas.md`.

## Auth model
- **Agent routes (server)** — `X-Api-Key: <machine key>` (issued at registration)
- **Admin routes (server)** — `X-Admin-Password: <password>` (set in dashboard; open if unset)
- **The agent's own API (:5178)** — `X-SaveLocker-Token`, from the 0600 `api-token` file beside
  `config.json`, **plus** a loopback `Host` check and an Origin check. Loopback is not by itself a
  defence: that API rewrites config and re-registers the machine, and any local process or web page
  can reach it. There is **no CORS policy on purpose**, so a foreign page cannot call it at all.

## Data flow
```
Agent (push)  →  POST /api/games/{id}/upload  →  SyncService  →  ArchiveStore + SQLite
Agent (pull)  →  GET  /api/games/{id}/download →  SyncService  →  ArchiveStore → zip stream
Dashboard     →  GET  /api/overview            →  SyncService  →  GameStateDto[]
```

## Key config paths (runtime)
| Item | Path |
|------|------|
| Agent config (Windows) | `%PROGRAMDATA%\SaveLocker\config.json` |
| Agent state (Linux) | `~/.local/share/SaveLocker/` — **also the install prefix**, which is why an update copies file-by-file and never swaps the directory ([[Gotchas]]) |
| Agent log | `<state dir>\agent.log` |
| Local API token | `<state dir>\api-token` (0600, beside the config; `--config` moves it) |
| Offline queue | `<state dir>\offline-queue.json` |
| Lease warnings | `<state dir>\lease-warnings.json` |
| Server DB (dev) | `src/Server/localstate/savelocker.db` |
| Server DB (Docker) | `/data/savelocker.db` (or `localgamesync.db` on existing deployments) |
| Archives (Docker) | `/data/archives/` |
| Art cache (Docker) | `/data/art/` |
| Backups (Docker) | `/data/backups/` |
| Installer store (Docker) | `/data/agent-installer/` (win-x64) + `/data/agent-installer/linux-x64/` |
