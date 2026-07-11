# SaveLocker — Session Context

**What:** Self-hosted Windows game save sync. Hub-and-spoke: C#/WinForms tray agent on each PC syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard + embedded React agent UI.

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** main

**Current version:** v0.1.1 (tagged, GitHub Release published). Three open bugs in the auto-update pipeline — see `Backlog.md` and `tasks/001_v0.1.1_bugs.md`.

---

## Status (2026-07-11) — v0.1.1 shipped, 3 post-release bugs queued

| Area | State |
|------|-------|
| Tray agent (WinForms + React/WebView2) | ✅ done |
| Game scanning (Steam VDF + Ludusavi) | ✅ done |
| Server (REST API, EF/SQLite, leases, conflicts) | ✅ done |
| Admin dashboard (React + Tailwind, baked into Docker) | ✅ done |
| Agent auto-update (release CI, server-hosted installer) | ⚠️ 3 bugs — see below |
| CI/CD (push → Docker → GHCR; tag → GitHub Release) | ✅ done (Watchtower removed) |

**Open bugs (block v0.1.2):**
1. **Agent UI shows "AGENT V0.0.0"** — MinVer fails in CI, stamps `AssemblyVersion=0.0.0.0`. Passing `--property:Version=0.1.1` sets `FileVersion` but MinVer still overrides `AssemblyVersion` independently. Fix: also pass `--property:AssemblyVersion=$AppVersion` in `build-installer.ps1`.
2. **No auto-relaunch after silent update** — `skipifsilent` removed from `SaveLocker.iss [Run]` (committed, unverified). Needs a real update test.
3. **Uploaded installer lost on every Docker update** — `AgentInstallerService` defaults storage to `AppContext.BaseDirectory/data/agent-installer` (inside container), not the persistent `/data` volume. Fix: add `"AgentInstallerRoot": "/data/agent-installer"` to `appsettings.json` `Storage` block.

See `tasks/001_v0.1.1_bugs.md` for the full investigation brief.

---

## Active backlog (priority order)
1. Fix the 3 auto-update bugs above (see `tasks/001_v0.1.1_bugs.md`)
2. Code-sign the exe (SmartScreen warns for unsigned installers)
3. Per-game glob filters (include/exclude file patterns before archiving)
4. Save-in-use safety (5 s debounce may be too short for some games)

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
| v0.1.1 bug tasks | `tasks/001_v0.1.1_bugs.md` |
| Session history | `logs/sessions.md` |
