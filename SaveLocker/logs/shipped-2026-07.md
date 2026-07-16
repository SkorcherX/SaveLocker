# Shipped — migrated out of Backlog (July 2026)

Backlog items that are now complete, moved here so `Backlog.md` holds only
not-yet-done work. Full technical detail lives in `logs/sessions.md`; this is the
quick "what shipped, where" index.

| Item | Shipped in | Verified | Detail |
|------|-----------|----------|--------|
| Agent version display fix (MinVer overrode version fields in-target → `MinVerVersionOverride` + read `FileVersion`) | v0.1.2 | ✅ device | sessions 2026-07-12 |
| Silent auto-relaunch after update (`skipifsilent` removed) | v0.1.2 | ✅ device | sessions 2026-07-12 |
| Uploaded installer persistence across Docker update (`Storage:AgentInstallerRoot`) | v0.1.2 | ✅ device | sessions 2026-07-12 |
| Fetch agent installer from GitHub Releases — manual dashboard button (`POST /api/admin/agent-installer/fetch-github`) | main, 2026-07-11 | API-verified | sessions 2026-07-11 (session 2) |
| Sync toaster reduction — one summary toast with save date instead of 4 | v0.1.3 | ✅ device | sessions 2026-07-12 |
| Per-game exclude globs + configurable upload cap (hygiene 5e) | v0.1.4 (agent); server on main | ✅ device-verified: excluded logs absent; log-only change created no version | sessions 2026-07-12 (session 2); `logs/002_glob_filters.md` |
| Save-in-use settle gate before automatic pushes | main, 2026-07-12 | ✅ device-verified: settle wait completed before push; archive complete | `save-in-use-safety.md` |
| Agent local API → generated UI types | main, 2026-07-15 | Windows + Linux builds; live `/api/state`, static UI, OpenAPI, 404 behavior, and generated-type drift check | `AgentApiServer.cs`; `agent-ui/src/api-types.ts` |
| Console Help KB — dashboard Help tab, 8 static Markdown articles, full-text search, `#help/<slug>` deep-links, conflict card "Why did this happen?" link | main (`be54374`) | build-verified | 2026-07-11 |
| Scheduled GitHub installer auto-poll | main | server + dashboard builds | `AgentInstallerPollerService`, dashboard-configurable in Agent Updates; sessions 2026-07-12 |
| Linux agent Phases 1–3 — `Agent.Core` split, `src/Agent.Linux` (`savelocker`), **cross-OS round-trip in CI** | main (PR #1), 2026-07-13 | CI: Windows 10/10, Linux 10/10, harness 27/27, cross-OS **byte-identical both ways** | `logs/2026-07-14_linux-agent.md` |
| Linux agent **Phase 4** — enrollment: console mints a single-use 15-min policy file, agent runs `enroll --file`; **no API key ever copied by hand**. Unsigned by design; TOFU-pins the server (warns, never blocks) | main (PR #4), 2026-07-13 | enrollment 16/16 + **TOFU over real TLS 6/6**; the http harness could only prove the pin was *absent*, so the TLS suite is what actually tests it | `logs/2026-07-14_linux-agent.md` |
| Linux agent **Phase 5** — agent health reporting: a headless Deck cannot toast, so failures go to the console (problem badge + per-machine health). Events dedupe on (machine, game, code) and **auto-close when a machine recovers** | main (PR #5), 2026-07-14 | health 17/17, driving **real** failures — a real conflict, a real blocked pull, a real push against a **stopped server** (the durable report) | `logs/2026-07-14_linux-agent.md` |
| Linux agent **Phase 6** — hardening: `SaveDirSanity` ("that is a Wine PREFIX, not a save folder"), zip-slip **proven** by force-uploading a hostile archive, settle gate moved to a **monotonic** clock (wall-clock counted suspended hours as elapsed → a suspended Deck published a mid-flush save) | main (PR #6), 2026-07-14 | hardening **14/14 Linux** (real symlinks) / 13/13 Windows (junctions) | `logs/2026-07-14_linux-agent.md` |
| 🐛 **DATA LOSS — symlink escape (affected Windows too).** `Directory.EnumerateFiles(…, AllDirectories)` follows symlinks/junctions, and a Wine prefix is full of them. The archive **leaked** the link's target; far worse, `RestoreArchive`'s delete pass reached **through** the link and **deleted files outside the save folder**. ⚠️ The obvious fix (skip `FileAttributes.ReparsePoint`) is *itself* a silent data-loss bug — **OneDrive placeholders are reparse points too**; the check keys on `LinkTarget` | main (PR #6), 2026-07-14 | the harness was run against **pre-fix** code and correctly **failed 4 checks** (it archived a sibling file *and* deleted one) | `Gotchas.md` |
| 🐛 `ArchiveStore` persisted an OS-specific path separator → archives unreachable when a DB moved Windows→Docker (agent said *"server has no saves yet"* while the console showed a head) | main (PR #1), 2026-07-13 | found **by** the cross-OS test | `Gotchas.md` |
| **.NET 10 (LTS) upgrade** — net9 was STS (EOL 10 Nov 2026). Retarget, EF 10, Docker `aspnet:10.0`, `global.json` SDK pin, OpenAPI regen | main (PR #2), 2026-07-13 | Windows 10/10, Linux 10/10, harness 27/27, cross-OS green; **EF 10 changed nothing on a copy of the real prod DB**; net9 field agents compatible 12/12 | `logs/2026-07-13_dotnet-10-upgrade.md` |
| **CVE-2025-6965 (High)** — SQLitePCLRaw pinned to 3.x; SQLite 3.4x → **3.50.4** | main (PR #3), 2026-07-13 | `SELECT sqlite_version()` at runtime; old-engine DB opened by new engine 9/9 (reads *and* writes) | `Gotchas.md` |
| **CVE-2026-49451 (High)** — `Microsoft.OpenApi` pinned to 2.10.0 (introduced by the net10 bump) | main (PR #2), 2026-07-13 | NU1903 cleared | `logs/2026-07-13_dotnet-10-upgrade.md` |
| MinVer never matched the `v*` tags (`MinVerTagPrefix` unset → every build stamped `0.1.0`) | main (PR #3), 2026-07-13 | now stamps `0.1.6-alpha.N` | — |
| SYSLIB0060 — password hashing off the obsolete `Rfc2898DeriveBytes` ctor | main (PR #3), 2026-07-13 | `tests/verify-password-compat.ps1` 6/6 (an OLD-code hash still verifies) | `Gotchas.md` |

## Dropped from backlog — won't do (2026-07-12)

- **SteamGridDB key in agent UI** — the agent tray UI displays no game cover art (only the app logo); art is fetched server-side and shown only in the web dashboard. No plan to add art to the agent UI, so there's nothing for the key to power there. Console/dashboard config is sufficient.
- **CloudFlare Access / remote-access hardening** — SaveLocker is a LAN-only self-hosted tool by design. Exposing it over the internet is the operator's responsibility (Tailscale or another secure tunnel); we won't build in remote-access hardening or work around Cloudflare Tunnel's file-size limit.
