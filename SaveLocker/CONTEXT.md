# SaveLocker — Session Context

**What:** Self-hosted Windows game save sync. Hub-and-spoke: C#/WinForms tray agent on each PC syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard + embedded React agent UI.

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** main

**Current version:** v0.1.5 (released — glob patterns now match at any depth, gitignore-style; asset ProductVersion `0.1.5` verified). v0.1.4 shipped 5e glob filters; v0.1.1–0.1.3 verified on device. Recently-shipped work indexed in `logs/shipped-2026-07.md`.

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
| Save-in-use safety (settle gate before auto-push) | ✅ built 2026-07-12 — ⏳ not yet device-verified |
| Cross-OS round-trip in CI (Windows agent ↔ Linux agent) | ✅ done 2026-07-13 — byte-identical both ways |
| **Runtime: .NET 10 (LTS)** | ✅ merged 2026-07-13 (PR #2). net9 was STS, EOL 10 Nov 2026 |
| **Known vulnerabilities** | ✅ **none** — `dotnet build` reports no NU1903 (PR #3) |

Shipped-feature detail: `logs/shipped-2026-07.md` + `logs/sessions.md`. Open work: `Backlog.md`.
Full record of the .NET 10 upgrade: `logs/2026-07-13_dotnet-10-upgrade.md`.

---

## ▶ NEXT ACTION: Linux agent **Phase 4** — enrollment token + policy import

Everything else is either shipped or a device-verify item that needs hardware.

- **Plan:** `tasks/linux-agent.md` → Phase 4. **Design is locked** in `Decisions.md` §4 — read it first; do not re-litigate the no-signing call.
- Console mints a **single-use, ~15-min enrollment token** (never a raw API key in a file); the agent redeems it for its machine key. `savelocker enroll --file <policy>`. The policy carries the server URL plus preselected games / exclude globs / settle delay. **No signing** (see the decision for why it would be theatre). **TOFU-pin** the server after enrollment and warn if its identity changes.
- **Phases 0–3 are done.** Phase 1 split the platform-neutral `Agent.Core`; Phase 2 built `src/Agent.Linux` → binary **`savelocker`** (shortcuts.vdf discovery, Proton prefix resolution, `run -- %command%` launch wrapper, `doctor`, headless daemon + `systemd --user`); Phase 3 proved a Windows save and a Proton save are **byte-identical**, in CI.
- **Phase 5 (agent health reporting) ships WITH Linux, not after** — a headless Deck cannot surface a conflict, so without it a Deck failure is invisible.
- ⚠️ **Built ≠ verified on hardware.** No Steam Deck is owned. WSL + the harness cover everything *except* gamescope, the immutable rootfs, SD-card paths and suspend/resume.
- Note the API change rule in `CLAUDE.md`: enrollment adds endpoints → **regenerate `web/src/api-types.ts` and commit the updated `src/Server/openapi.json`**.

---

## Open (not blocking Phase 4)

- ⚠️ **The unRAID server may still be running the OLD image.** `main` now builds on `aspnet:10.0` with SQLite 3.50.4. To deploy: **take a `/data/backups/` snapshot first**, then `docker compose pull && docker compose up -d`. Old **net9 agents are compatible** with the net10 server (tested 12/12 against a real v0.1.5 agent), so the fleet does not need a coordinated upgrade.
- **Device-verify 5e** on v0.1.5 (add `*.log` to a game → nested + root excluded; log-only change → no new version). Both agents must be on the same version for consistent hashing.
- **Device-verify the settle gate** — exit a slow-flushing game, confirm the agent log shows the wait, then `save files settled.` before the push.
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
| Linux fake-game harness | `tests/linux/run-linux-tests.sh` (27 checks; starts its own server) |
| Cross-OS round-trip | `tests/cross-os/crossos.ps1 -Leg author\|roundtrip\|confirm` — one leg per OS; CI chains them by passing the server's state as an artifact |
| Password-hash compat | `.\tests\verify-password-compat.ps1` — builds a server from an older git ref, has it hash an admin password, then asserts the current code still verifies it |

**Always use `--no-incremental` for server builds** — stale DLL reuse has masked changes before. Stop the running agent/server first (they lock the DLLs).

**CI (`ci.yml`) runs 8 jobs on every PR:** `build-dotnet`, `build-web`, `build-agent-ui`, `docker-build` (builds the server image — publishes nothing), `agent-tests-linux`, and the chained `crossos-author → crossos-roundtrip → crossos-confirm`. The cross-OS chain is the one that matters: it hands the **server's own state** (SQLite DB + archive store) between a Windows and an Ubuntu runner as an artifact, because runners cannot share a network.

### Toolchain (installed 2026-07-13 — a fresh session does not need to redo this)

| Where | What |
|------|------|
| Windows | .NET SDK **9.0.315 + 10.0.301** side by side. `global.json` pins **10.0.x** — that is why `dotnet --version` at the repo root says 10.0.301 and never silently picks another. Only **Windows PowerShell 5.1** (no `pwsh`), so scripts here must stay 5.1-compatible (no `??`, no `$IsWindows`, and a BOM-less `.ps1` is read as **ANSI** — keep them ASCII). |
| WSL (Ubuntu 24.04) | .NET SDK **9.0.315 + 10.0.301** in `~/.dotnet`; **pwsh 7.4.6** in `~/.local/pwsh` (symlinked to `~/.local/bin/pwsh`); **Node 22 via nvm** (`. ~/.nvm/nvm.sh`). Repo clone at `~/SaveLocker` on **ext4** — never build or test from `/mnt/*`. |

⚠️ **Inside WSL, `npm` resolves to the WINDOWS npm** on the shared PATH unless a Linux node is put first — the symptom is the baffling `error TS5083: Cannot read file 'C:/Windows/tsconfig.json'`. Source nvm and prepend it before running anything that shells out to npm (e.g. `packaging/linux/build-linux.sh`).

---

## Deployment
- **unRAID:** Docker on port 5080. `git push main` → Actions build → GHCR. To deploy: `docker compose pull && docker compose up -d`.
- **Tag a release:** `git tag v0.2.0 && git push origin v0.2.0` → `release.yml` builds installer → GitHub Release.

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
