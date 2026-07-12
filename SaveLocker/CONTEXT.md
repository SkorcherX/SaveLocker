# SaveLocker — Session Context

**What:** Self-hosted Windows game save sync. Hub-and-spoke: C#/WinForms tray agent on each PC syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard + embedded React agent UI.

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** main

**Current version:** v0.1.3 (released — clean tag, asset ProductVersion `0.1.3` verified via `gh`). Ships the sync-toast reduction. v0.1.2 fully verified on device. Next: confirm the one-toast sync behavior on device.

---

## Status (2026-07-12) — v0.1.2 fully verified; sync-toast fix uncommitted-to-release

| Area | State |
|------|-------|
| Tray agent (WinForms + React/WebView2) | ✅ done |
| Game scanning (Steam VDF + Ludusavi) | ✅ done |
| Server (REST API, EF/SQLite, leases, conflicts) | ✅ done |
| Admin dashboard (React + Tailwind, baked into Docker) | ✅ done |
| Agent auto-update (release CI, server-hosted installer) | ✅ all 3 bugs verified on device |
| Fetch installer from GitHub (dashboard button) | ✅ done (2026-07-11) |
| Sync notifications (one toast + save date, not 4) | ✅ shipped in v0.1.3; device check pending |
| CI/CD (push → Docker → GHCR; tag → GitHub Release) | ✅ done (Watchtower removed) |

**v0.1.1 bugs — all fixed, shipped in v0.1.2:**
1. **Agent UI version wrong** — showed `0.0.0` then `0.1.0`. Real cause: MinVer assigns Version/FileVersion/AssemblyVersion **inside an MSBuild target**, which overrides command-line `--property` globals, so no `--property` ever won. Fixed with `MinVerVersionOverride` env var in `build-installer.ps1` (stamps all fields) + `UpdateChecker` now reads `FileVersion` via `Environment.ProcessPath` instead of `AssemblyVersion`. **✅ Verified on device — tray header shows `0.1.2`.**
2. **No auto-relaunch after silent update** — `skipifsilent` removed from `SaveLocker.iss [Run]`. **✅ Verified on device** — agent restarts silently after update.
3. **Uploaded installer lost on Docker update** — added `"AgentInstallerRoot": "/data/agent-installer"` to `appsettings.json` `Storage`. **✅ Verified on device** — installer survives `docker compose pull && up -d`.

**Sync toaster reduction (`777b9ab`, 2026-07-12):** dashboard sync fired 4 toasts (pull, push, folder-watcher auto-push, summary). Fixed by splitting `SyncEngine`'s callback into `log` (agent.log, always) and `notify` (toast); routine progress is log-only, only conflicts/blocks/offline-retries/lease warnings toast. Dashboard commands emit one summary with the save timestamp. Pre-launch/post-exit auto-syncs are now silent on success too (by design). Not yet in a tagged release.

**⚠️ v0.1.2 was force-retagged** onto the fix commits (tag moved 3×). The GitHub Release object couldn't be regenerated from here (no `gh`/token) — CI overwrites the installer asset, but the release *notes* may be stale.

---

## Active backlog (priority order)
1. **Verify sync-toast fix on device** (shipped in v0.1.3) — dashboard sync → one toast with save date; conflict still alerts
2. Scheduled GitHub installer auto-poll (background follow-up to the manual fetch button)
3. Code-sign the exe (SmartScreen warns for unsigned installers)
4. Per-game glob filters (include/exclude file patterns before archiving)

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
