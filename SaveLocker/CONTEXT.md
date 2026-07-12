# SaveLocker — Session Context

**What:** Self-hosted Windows game save sync. Hub-and-spoke: C#/WinForms tray agent on each PC syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard + embedded React agent UI.

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** main

**Current version:** v0.1.5 (releasing — glob patterns now match at any depth, gitignore-style). v0.1.4 shipped 5e glob filters; v0.1.1–0.1.3 verified on device. Recently-shipped work indexed in `logs/shipped-2026-07.md`.

---

## Status (2026-07-12)

| Area | State |
|------|-------|
| Tray agent (WinForms + React/WebView2) | ✅ done |
| Game scanning (Steam VDF + Ludusavi) | ✅ done |
| Server (REST API, EF/SQLite, leases, conflicts) | ✅ done |
| Admin dashboard (React + Tailwind, baked into Docker) | ✅ done |
| Agent auto-update (version, silent relaunch, installer persistence) | ✅ verified on device (v0.1.2) |
| Fetch installer from GitHub — manual dashboard button | ✅ done (2026-07-11) |
| Sync notifications (one toast + save date, not 4) | ✅ v0.1.3, verified on device |
| Per-game exclude globs + 200 MB upload cap (5e) | ✅ v0.1.4; depth-matching fix in v0.1.5 |
| CI/CD (push → Docker → GHCR; tag → GitHub Release) | ✅ done (Watchtower removed) |

Shipped-feature detail: `logs/shipped-2026-07.md` + `logs/sessions.md`. Open work: `Backlog.md`.

---

## Active backlog (priority order — see `Backlog.md`)
1. **Device-verify 5e** on v0.1.5 (add `*.log` to a game → nested + root excluded; log-only change → no version). Both agents must be on the same version for consistent hashing.
2. **Console Help page / Knowledge Base** — spec'd in `Backlog.md`: a `help` dashboard tab with curated Markdown articles (conflicts, syncing, multi-machine, glob patterns, troubleshooting). Static/bundled, no API changes. Not started.
3. Scheduled GitHub installer auto-poll (follow-up to the manual fetch button)
4. Code-sign the exe (SmartScreen warns for unsigned installers)
5. Save-in-use safety (5 s debounce may be too short for some games)

**Gotcha surfaced 2026-07-12:** with two agents, saves diverge → dashboard conflict when the pushing machine's known head ≠ current server head (another machine advanced it). A "behind" machine keeps conflicting until resolved (dashboard resolve → pull, or tray Force Pull); the agent doesn't auto-advance its parent on conflict. Version/glob skew between agents guarantees this — keep both agents identical. (This is the seed for the Help KB "Understanding conflicts" article.)

See `Backlog.md` for the full list.

---

## Dev quick-reference

| Task | Command |
|------|---------|
| Run server | `cd src/Server && dotnet run` → http://localhost:5179 |
| Run dashboard | `cd web && npm run dev` → http://localhost:5173 |
| Build agent | `dotnet build src/Agent/SaveLocker.Agent.csproj --no-incremental` |
| Build installer | `.\installer\build-installer.ps1` |
| Run tests | `.\tests\run-agent-tests.ps1` (server must be on :5179) |

**Always use `--no-incremental` for server builds** — stale DLL reuse has masked changes before. Stop the running agent/server first (they lock the DLLs).

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
| Agent CLI | `CLI Reference.md` |
| Active backlog | `Backlog.md` |
| Session history | `logs/sessions.md` |
