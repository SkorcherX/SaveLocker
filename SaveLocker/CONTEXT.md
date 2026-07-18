# SaveLocker — Session Context

**What:** Self-hosted Windows game save sync. Hub-and-spoke: C#/WinForms tray agent on each PC syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard + embedded React agent UI.

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** main

**Current version:** v0.1.7 (tagged 2026-07-14 — **fixes a v0.1.6 regression that broke silent auto-update**: the installer's enroll page validated in `NextButtonClick`, which Inno still calls under `/SILENT`, and returning False there aborts the update. See `Gotchas.md`). **v0.1.6 must not be hosted for auto-update.** v0.1.6 added installer GUI enrollment; v0.1.5 released glob depth-matching; v0.1.4 shipped 5e glob filters. Recently-shipped work indexed in `logs/shipped-2026-07.md`.

---

## Status (2026-07-13)

| Area | State |
|------|-------|
| Tray agent (WinForms + React/WebView2) | ✅ done |
| Game scanning (Steam VDF + Ludusavi) | ✅ done |
| Server (REST API, EF/SQLite, leases, conflicts) | ✅ done |
| Admin dashboard (React + Tailwind, baked into Docker) | ✅ done |
| Agent auto-update (version, silent relaunch, installer persistence) | ✅ verified on device (v0.1.2) |
| Fetch installer from GitHub — manual dashboard button | ✅ done (2026-07-11) |
| Scheduled GitHub installer auto-poll | ✅ dashboard-configurable in Agent Updates; persisted server-side and applied within a minute |
| Sync notifications (one toast + save date, not 4) | ✅ v0.1.3, verified on device |
| Per-game exclude globs + 200 MB upload cap (5e) | ✅ v0.1.4; depth-matching fix in v0.1.5 |
| CI/CD (push → Docker → GHCR; tag → GitHub Release) | ✅ done (Watchtower removed) |
| Console Help KB (8 articles, search, deep-links) | ✅ done (2026-07-11, `be54374`) |
| Save-in-use safety (settle gate before auto-push) | ✅ built and device-verified |
| Cross-OS round-trip in CI (Windows agent ↔ Linux agent) | ✅ done 2026-07-13 — byte-identical both ways |
| **Runtime: .NET 10 (LTS)** | ✅ merged 2026-07-13 (PR #2). net9 was STS, EOL 10 Nov 2026 |
| **Known vulnerabilities** | ✅ **none** — `dotnet build` reports no NU1903 (PR #3) |
| Linux agent **Phase 4** — enrollment token + policy import | ✅ done 2026-07-13 — 16/16 + 6/6 TLS (PR #4, merged) |
| Linux agent **Phase 5** — agent health reporting | ✅ done 2026-07-14 — 17/17 (PR #5, merged) |
| Linux agent **Phase 6** — hardening | ✅ done 2026-07-14 — 14/14. **Fixed a real data-loss bug** (below) |
| Agent local API + generated UI types | ✅ ASP.NET Core minimal API + OpenAPI; agent UI schemas generated from the live contract |
| **Local agent API hardening** | ✅ 2026-07-18 — 15/15 (`run-local-api-tests.ps1`, in CI). Token-auth + Host/Origin validation, no CORS, `--lan` withdrawn, machine key no longer served. `Decisions.md` §7. ⏳ **fleet keys still need rotating** |
| **Cross-process state safety** | ✅ 2026-07-18 — 12/12 (`run-concurrency-tests.ps1`, in CI). Fixed a **self-conflict bug**: a daemon's stale `config.json` write erased another process's parent version, so the next push was rejected as a conflict. `Decisions.md` §8 |
| **Installer GUI enrollment** (Windows) | ✅ v0.1.6 built the wizard page. 🐛 **v0.1.6 broke silent auto-update** (NextButtonClick fires under /SILENT → abort). ✅ **fixed in v0.1.7** (`WizardSilent` guard + `ShouldSkipPage` for enrolled machines). ✅ **silent upgrade of an enrolled agent device-verified on v0.1.7 (2026-07-14)** — the regression path. ⏳ fresh-install happy-path enroll (page shows server/name, machine goes online) still unverified on device |

Shipped-feature detail: `logs/shipped-2026-07.md` + `logs/sessions.md`. Open work: `Backlog.md`.
Full record of the .NET 10 upgrade: `logs/2026-07-13_dotnet-10-upgrade.md`.

---

## ▶ NEXT ACTION: **Finish device-verifying the fresh-install enroll path (v0.1.7)**

The 🚨 silent-upgrade regression — the one that could have broken every Windows machine — is **done**:
a silent auto-update of an already-enrolled agent to v0.1.7 completed cleanly on device (2026-07-14),
no prompt, config intact. What's left is the **fresh-install** side of the wizard (scenarios in the
archived task `logs/2026-07-14_installer-enrollment.md`):

- **Happy path:** on a machine where `%PROGRAMDATA%\SaveLocker` does *not* exist, run the installer,
  choose an enrollment file → the page shows the right server + machine name → install → the machine
  appears in **Configuration → Machines**, **online**, with its version.
- ⚠️ **The ACL trap** (verify right after the happy path): `icacls "%PROGRAMDATA%\SaveLocker"` — the
  interactive user needs **Modify**. The enroll runs via `ExecAsOriginalUser` precisely so the config
  dir is created de-elevated by the user that later rewrites it.
- Also: expired-token (page says so, install still succeeds), skip path (installs unenrolled), and the
  unattended switch `Setup.exe /SILENT /ENROLL="C:\path\policy.json"`.

**Do not host v0.1.6 for auto-update** — it aborts under /SILENT. Host **v0.1.7** or newer.

## Also queued: **Linux Help-KB articles** — `tasks/linux-kb-articles.md` (UNBLOCKED)

The "Installing the agent" article shipped 2026-07-14 (Windows + Linux + Deck, `Getting started`),
which covers that task's §1. What remains is `deck-supported-games`, `deck-troubleshooting`, and the
edits to the existing articles.

**All six phases of the Linux agent are done** (archived: `logs/2026-07-14_linux-agent.md`). The KB
task was explicitly blocked on Phases 4–6 and is now the natural next step — and it is **not
documentation-as-nicety**: a Deck is headless by design, so **the console + KB are its only support
surface**. A Windows user who hits a problem gets a balloon; a Deck user sees nothing.

Write it against what actually shipped, which changed two of its assumptions:
- **§1 setup is now the enrollment flow** — *download a policy file from the console → `savelocker enroll --file <policy>`* — **not** `set-server` + `register` (Phase 4).
- **§4's `conflicts.md` edit can now say conflicts ARE visible** on a Deck: Phase 5 landed health reporting, so the console shows a problem badge and per-machine health.

### ⚠️ Standing deferred risk: NO HARDWARE

Everything above is verified in WSL + CI and has **never run on a Steam Deck**. **gamescope / Game
Mode, the immutable rootfs, SD-card library paths and suspend/resume cannot be proven any other way.**
Validate on a borrowed/used Deck or on **Bazzite** (the practical SteamOS stand-in), or recruit a
Deck-owning beta tester, **before shipping to real users**. Same rule as the Windows device-verify
items: **built ≠ verified.** Any KB claim about Game Mode or the Launch Options UI is currently *from
documentation, not observation* — flag those rather than writing them confidently.

Other open work is in `Backlog.md` — fresh installer-enrollment verification, code-signing the exe,
and deploying the net10 server to unRAID.

### 🐛 Phase 6 fixed a REAL data-loss bug (2026-07-14) — worth knowing about

`Directory.EnumerateFiles(..., AllDirectories)` **follows symlinks**, and a Wine prefix is full of
them. The archive leak was the *lesser* half. The dangerous half: **`RestoreArchive` deletes target
files that are absent from the archive**, so walking through a link it **deleted files outside the
save folder entirely.** The pre-fix harness run reproduced both for real. **Windows was affected too**,
via junctions.

- Fixed with a no-follow walk (`SaveArchive.EnumerateFilesNoFollow`): links are never archived, never restored, never deleted.
- ⚠️ **Do not "simplify" the link test to `FileAttributes.ReparsePoint`.** OneDrive files-on-demand placeholders are *also* reparse points — that version silently stops archiving every OneDrive save. It keys on `FileSystemInfo.LinkTarget`, which is non-null only for symlinks and junctions. See `Gotchas.md`.
- Also landed: `SaveDirSanity` (names a Wine prefix mistaken for a save folder, surfaced by `doctor`), a proven zip-slip rejection, and a **monotonic** settle gate — wall-clock counted suspended hours as elapsed, so a suspended Deck woke up and published a possibly mid-flush save.

### Phase 5, shipped 2026-07-14 — how a Deck's failures reach you

**The console is the Deck's UI** (`Decisions.md` §2). A headless box cannot toast, so the agent
reports to the server and the dashboard surfaces it: a **problem badge** in the NavBar (absent when
the fleet is healthy) opening a list of events with Dismiss, plus per-machine health on the Machines
card — online / offline / **never reported**, agent version, platform, last sync, unmapped games,
queued pushes.

- **Scope, and it matters:** the server already knows what happens *server-side* (a conflict is a `ConflictFlag` the moment the upload lands). Agents report only what the server **cannot infer** — blocked pull, missing save folder, rejected upload, settle timeout, unreachable server. The conflict event exists solely to name **which machine is stuck**.
- **Events deduplicate** on (machine, game, code) while open — a persistent fault bumps a count, it does not write a row every 20 s. A game that **syncs cleanly auto-closes** that machine's events for it, so a Deck that recovers leaves no stale alarm.
- **Pending events persist to disk**, because the most important thing to report — "I cannot reach the server" — happens precisely when reporting is impossible. It is delivered on the first contact after the network returns.
- **Every sync path reports**, not just the daemon: the launch wrapper (`ProtonRun`) *is* the Deck's Proton sync path and has no poller, so it flushes before exiting; one-shot `push`/`pull` do too.
- The Windows tray **also** reports (it toasts *and* reports), so the console is one honest view of the whole fleet.

### Phase 4, shipped 2026-07-13 — how enrollment works now

A machine is set up with **one file and one command**, and **no API key is ever copied by hand**:
Console → *Configuration → Enroll a machine* mints a **single-use, 15-min token** wrapped in a
policy file (server URL + games + settle delay); the agent runs `enroll --file <policy>` and trades
the token for its own machine key. The token's **raw value exists only in that one download** — the
server stores a hash.

- **Unsigned, on purpose** (`Decisions.md` §4) — the threat is a *malicious server URL*, not a bogus token, and a fresh agent has no trust anchor to check a signature against. Do not "fix" this with a PKI.
- **TOFU pin:** the agent pins the server's TLS public key at enrollment and **warns (never blocks)** if it changes — a hard fail would take a headless Deck offline on a routine cert renewal. `trust` shows it; `trust --accept` re-pins.
- A token minted **for a machine name binds it** — `--name` cannot override it. Redeeming an existing name **rotates** that machine's key: that is the re-enrollment path for a wiped device.
- `enroll` lives in **`Agent.Core`**, so Windows and Linux run the same implementation.

---

## Open (not blocking Phase 4)

- ⚠️ **The unRAID server may still be running the OLD image.** `main` now builds on `aspnet:10.0` with SQLite 3.50.4. To deploy: **take a `/data/backups/` snapshot first**, then `docker compose pull && docker compose up -d`. Old **net9 agents are compatible** with the net10 server (tested 12/12 against a real v0.1.5 agent), so the fleet does not need a coordinated upgrade.
- **Code-sign the exe** — SmartScreen warns for unsigned installers.

**Gotcha surfaced 2026-07-12:** with two agents, saves diverge → dashboard conflict when the pushing machine's known head ≠ current server head (another machine advanced it). A "behind" machine keeps conflicting until resolved (dashboard resolve → pull, or tray Force Pull); the agent doesn't auto-advance its parent on conflict. Version/glob skew between agents guarantees this — keep both agents identical. (This is the seed for the Help KB "Understanding conflicts" article.)

**🐛 Real bug fixed 2026-07-13 (found by the Phase 3 cross-OS test):** `ArchiveStore` persisted the archive's store path into the DB using `Path.Combine`, so a **Windows-hosted server wrote `gameid\versionid.zip`**. On Linux a backslash is a filename character, not a separator — the archive becomes unreachable, `/download` 404s, and the agent reports **"server has no saves yet"** while `status` still shows a head. Production is fine (server only runs in Docker/Linux) but **the dev workflow runs the server on Windows**, so a DB or backup carried from a dev box to Docker has unreachable archives. Fixed: canonical `/` on write, either separator tolerated on read. Rule: `Path.Combine` is for *this* machine *now* — anything persisted gets a `/`. See `Gotchas.md`.

See `Backlog.md` for the full list.

---

## Dev quick-reference

| Task | Command |
|------|---------|
| Run server | `cd src/Server && dotnet run` → http://localhost:5179 |
| Run dashboard | `cd web && npm run dev` → http://localhost:5173 |
| Build agent | `dotnet build src/Agent/SaveLocker.Agent.csproj --no-incremental` |
| Build installer | `.\installer\build-installer.ps1` |
| Run tests (Windows) | `.\tests\run-agent-tests.ps1` (server must be on :5179) |
| Run tests (Linux) | `pwsh tests/run-agent-tests.ps1` — same script, drives the Linux agent |
| Enrollment tests | `.\tests\run-enrollment-tests.ps1` (16 checks; needs :5179). Run it **after** the agent suite — it adds a game + machine to the DB |
| Local agent API security | `.\tests\run-local-api-tests.ps1` (15 checks). Starts its own daemon on **:5188** via `daemon --port`, so it never collides with a real agent on :5178. Needs nothing running |
| Cross-process state | `.\tests\run-concurrency-tests.ps1` (12 checks; own server on **:5183**, own daemon on **:5189**). Daemon vs. a second process over `config.json`, the offline queue and health events. Verified to FAIL against pre-fix code |
| Health tests | `.\tests\run-health-tests.ps1` (17 checks). **Starts and stops its own server on :5181** — it has to, since one check pushes while the server is *down*. Needs nothing running |
| Hardening tests | `.\tests\run-hardening-tests.ps1` (14 on Linux / 13 on Windows; own server on :5182). Security: symlink escape on archive **and on restore-delete**, zip-slip. Windows uses junctions (no elevation); Linux uses symlinks |
| TOFU pin tests (TLS) | `.\tests\run-enrollment-tls-tests.ps1` (6 checks; starts its own HTTPS server on :5443). Needs `dotnet dev-certs https --trust` — local only, not in CI |
| Linux fake-game harness | `tests/linux/run-linux-tests.sh` (27 checks; starts its own server) |
| Cross-OS round-trip | `tests/cross-os/crossos.ps1 -Leg author\|roundtrip\|confirm` — one leg per OS; CI chains them by passing the server's state as an artifact |
| Password-hash compat | `.\tests\verify-password-compat.ps1` — builds a server from an older git ref, has it hash an admin password, then asserts the current code still verifies it |

**Always use `--no-incremental` for server builds** — stale DLL reuse has masked changes before. Stop the running agent/server first (they lock the DLLs).

**CI (`ci.yml`) runs 8 jobs on every PR:** `build-dotnet`, `build-web`, `build-agent-ui`, `docker-build` (builds the server image — publishes nothing), `agent-tests-linux` (agent **+ enrollment + health + hardening**), and the chained `crossos-author → crossos-roundtrip → crossos-confirm`. The cross-OS chain is the one that matters: it hands the **server's own state** (SQLite DB + archive store) between a Windows and an Ubuntu runner as an artifact, because runners cannot share a network.

### Toolchain (installed 2026-07-13 — a fresh session does not need to redo this)

| Where | What |
|------|------|
| Windows | .NET SDK **9.0.315 + 10.0.301** side by side. `global.json` pins **10.0.x** — that is why `dotnet --version` at the repo root says 10.0.301 and never silently picks another. Only **Windows PowerShell 5.1** (no `pwsh`), so scripts here must stay 5.1-compatible (no `??`, no `$IsWindows`, and a BOM-less `.ps1` is read as **ANSI** — keep them ASCII). |
| WSL (Ubuntu 24.04) | .NET SDK **9.0.315 + 10.0.301** in `~/.dotnet`; **pwsh 7.4.6** in `~/.local/pwsh` (symlinked to `~/.local/bin/pwsh`); **Node 22 via nvm** (`. ~/.nvm/nvm.sh`). Repo clone at `~/SaveLocker` on **ext4** — never build or test from `/mnt/*`. |

⚠️ **Inside WSL, `npm` resolves to the WINDOWS npm** on the shared PATH unless a Linux node is put first — the symptom is the baffling `error TS5083: Cannot read file 'C:/Windows/tsconfig.json'`. Source nvm and prepend it before running anything that shells out to npm (e.g. `packaging/linux/build-linux.sh`).

---

## Deployment
- **unRAID:** Docker on port 5080. `git push main` → Actions build → GHCR. To deploy: `docker compose pull && docker compose up -d`.
- **Tag a release:** `git tag v0.2.0 && git push origin v0.2.0` → `release.yml` builds **both** agents → GitHub Release:
  - **Windows:** `SaveLocker-Agent-Setup-<ver>.exe` (Inno Setup, built on `windows-latest`).
  - **Linux / Steam Deck:** `savelocker-<ver>-linux-x64.tar.gz` (self-contained, built on **`ubuntu-latest`** — see below).

### How a Steam Deck user installs the agent
```
tar -xzf savelocker-<ver>-linux-x64.tar.gz
./SaveLocker/install.sh
savelocker enroll --file <policy.json>      # from the console: Configuration → Enroll a machine
savelocker doctor
```
Installs to `~/.local/share/SaveLocker`, symlinks `~/.local/bin/savelocker`, enables a `systemd --user`
unit. **Never `/usr`** — SteamOS's rootfs is immutable and wiped on update (`Decisions.md` §5).

- ⚠️ **The Linux tarball MUST be built on `ubuntu-latest`.** A self-contained .NET binary binds to the **build host's glibc**, and an older-glibc build runs on newer systems but *never the reverse*. Ubuntu 24.04 (glibc 2.39) is older than SteamOS's rolling Arch, so Ubuntu → Deck is forward-compatible. Build it on anything newer and users get `GLIBC_2.4x not found` — an error you cannot reproduce on the machine that built it.
- CI's **`package-linux`** job builds the tarball on every PR and *installs it into a throwaway HOME*, so packaging cannot rot silently between releases (it is otherwise only exercised on a tag — i.e. too late).
- **Linux has no auto-update** (deliberate, not shipped). The update channel is installer-shaped and Windows-only; a Deck user re-runs `install.sh` from a newer tarball. See `Backlog.md`.

---

## Critical gotchas (read before touching builds or paths)
- Incremental builds can silently reuse stale DLLs → always `--no-incremental`
- Stop running agent/server before building (DLL file-lock)
- Dev storage uses `localstate/` not `data/` (Windows case-collision: `Data/` = source folder)
- `dotnet` may not be on PATH in an open shell after winget install — open a new shell
- OneDrive save paths block `Directory.Move` — RestoreArchive uses file-by-file copy to `_tempDir`
- PowerShell array splatting to native exes splits strings containing `:` character-by-character — always use `if/else` + `"--property:Key=Value"` long-form
- MinVer requires git access; silently fails to `0.0.0.0` on GitHub Actions Windows runners. CI must pass `--property:Version=$v --property:AssemblyVersion=$v` explicitly from `github.ref_name`
- See `Gotchas.md` for the full list with fixes

---

## Key files
| Topic | File |
|-------|------|
| Codebase map | `REPO_MAP.md` |
| System design | `Architecture.md` |
| Locked decisions | `Decisions.md` |
| Known traps | `Gotchas.md` |
| REST endpoints | `API Reference.md` |
| Dev build & run | `Build and Run.md` |
| Agent CLI | `web/src/help/cli-reference.md` (KB article) |
| Active backlog | `Backlog.md` |
| Session history | `logs/sessions.md` |
