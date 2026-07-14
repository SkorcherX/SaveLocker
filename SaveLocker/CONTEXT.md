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

Shipped-feature detail: `logs/shipped-2026-07.md` + `logs/sessions.md`. Open work: `Backlog.md`.

---

## Active backlog (priority order — see `Backlog.md`)
0b. **Security + hygiene follow-ups — DONE (2026-07-13), branch `sqlite-cve-2025-6965`.** Three items cleared:
   - **CVE-2025-6965 (High) fixed.** `SQLitePCLRaw.bundle_e_sqlite3` pinned to **3.0.3** — the whole family moves (core/provider/config/native lib). No patched 2.x exists; the fix needs SQLite ≥ 3.50.2. Verified by asking the engine: **`SELECT sqlite_version()` → 3.50.4**. **Do not remove that pin** — EF Core still resolves 2.1.11 on its own and would silently reintroduce the CVE (see `Gotchas.md`). Proven safe by making the **old** engine author a DB (real machines/games/versions/conflict/audit) and the **new** engine open it: 9/9, reads *and* writes, resolves the old engine's conflict.
   - **MinVer fixed** — `MinVerTagPrefix=v` was never set, so it never matched the `v0.1.5` tags and stamped `0.1.0`. Now correctly derives `0.1.6-alpha.N`.
   - **SYSLIB0060 fixed** — password hashing moved off the obsolete `Rfc2898DeriveBytes` ctor. Guarded by `tests/verify-password-compat.ps1`: a server built from an older ref hashes a password, then the current code must still verify it (6/6). **PBKDF2 params are part of the on-disk `v1:` format** — changing them invalidates every stored password.
0. **.NET 10 (LTS) upgrade — ✅ ALL 4 PHASES DONE (2026-07-13), on branch `dotnet-10` (PR #2), awaiting merge.** .NET 9 is STS and goes out of support 10 Nov 2026; .NET 10 is LTS to Nov 2028. Locked in `Decisions.md → Runtime: .NET 10 LTS`; full record in `tasks/dotnet-10-upgrade.md`.
   - **Verified:** Windows 10/10, Linux 10/10, fake-game harness 27/27, **cross-OS byte-compare green in CI** (so net10 did not change how saves hash or archive), exclude-globs unchanged, `web` + `agent-ui` build with **no dashboard source changes**, and **EF Core 10 opened a copy of the real production DB and changed nothing** (no migrations applied, schema and every row count identical).
   - **`global.json` now pins the SDK** — CI had been silently building the net9 targets with SDK 10.0.301 while the dev box used 9.0.315. The Dockerfile copies it in, so bump the pin and the `sdk:`/`aspnet:` image tags **together**.
   - **Security:** fixed `Microsoft.OpenApi` CVE-2026-49451 (introduced by the upgrade). **Did NOT** fix `SQLitePCLRaw` CVE-2025-6965 — pre-existing (net9 had it too), no patched 2.x exists, needs a major bump of the native provider under EF Core. Backlogged as its own change.
   - ✅ **Old net9 agents are compatible with the net10 server — tested, 12/12.** Built a real **v0.1.5** agent from the tag and drove it against the net10 server: registers, pushes, pulls, sees conflicts, and the **content hash agrees across runtimes in both directions** (a cross-version pull followed by a push reports "no local changes" — the agent confirming net9 and net10 hashed identical bytes identically). So `docker compose pull` is safe with the existing fleet still on v0.1.5; they can auto-update afterwards at their own pace.
   - ⚠️ **Deploy note:** merging PR #2 rebuilds the GHCR image on `aspnet:10.0`. Grab a `/data/backups/` snapshot before `docker compose pull` — EF Core 10 provably changed nothing on a *copy* of the prod DB, but that is not the same as testing yours in place.
1. **Device-verify 5e** on v0.1.5 (add `*.log` to a game → nested + root excluded; log-only change → no version). Both agents must be on the same version for consistent hashing.
2. **Device-verify the settle gate** — exit a slow-flushing game, confirm the agent log shows the wait then `save files settled.` before the push.
3. Code-sign the exe (SmartScreen warns for unsigned installers)
4. **Linux agent (Proton / Steam Deck)** — design locked in `Decisions.md`, phased plan in `tasks/linux-agent.md`. **Phases 0–3 ✅ done.** Phase 0: WSL2 (Ubuntu 24.04, .NET 9.0.315 in `~/.dotnet`, repo at `~/SaveLocker` on ext4). Phase 1: `Agent.Core` split — sync brain is net9.0 and platform-neutral. Phase 2: `src/Agent.Linux` → binary **`savelocker`** — shortcuts.vdf discovery, Proton prefix resolution, `run -- %command%` launch wrapper, `doctor`, headless daemon + `systemd --user` (27/27). Phase 3 (2026-07-13): **cross-OS round-trip in CI** — a Windows save and a Proton save are now *proven* interchangeable, byte-identical in both directions, hashes identical. **Next: Phase 4** (enrollment token + policy import). One phase at a time.
   - ⚠️ **Built ≠ verified on hardware.** No Steam Deck is owned. WSL + the harness cover everything *except* gamescope, the immutable rootfs, SD-card paths and suspend/resume — track this like the Windows device-verify items.

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
| Agent CLI | `web/src/help/cli-reference.md` (KB article) |
| Active backlog | `Backlog.md` |
| Session history | `logs/sessions.md` |
