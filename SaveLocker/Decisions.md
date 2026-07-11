# Decisions

Back to [[Home]]. These were settled with the user before building; don't
re-litigate without a reason.

## Build philosophy — Hybrid
Reuse the open-source **Ludusavi manifest** (community DB mapping thousands of
games → save locations, from PCGamingWiki) for detection. Build our own agent +
server + dashboard for orchestration, leasing, and conflict handling. Do **not**
re-map save locations ourselves.

## Conflict prevention — Proactive lock/lease
Server tracks an active "checkout" per game (like Steam Cloud's "in use"). Agent
pulls latest before launch; the other machine is warned if saves are leased
elsewhere. Content-hash + parent-version lineage is the fallback detector.

## Tech stack — Single-language .NET 9
- Agent in C#/WinForms: best Windows integration (FileSystemWatcher, process
  watch, tray, single-file exe).
- Server in ASP.NET Core, runs in Docker on unRAID. One language end-to-end.

## unRAID as hub (vs peer-to-peer) — chosen
- Asynchronous decoupling: PC pushes; laptop pulls later even if PC is off.
- Single source of truth → trivial "who synced last" + conflict resolution.
- Versioned history/rollback in one place.
- Already always-on, has storage, Docker, internet-reachable via the user's
  existing **CloudFlare Tunnel**.
- Rejected raw Syncthing as primary transport: continuous sync risks copying
  mid-write; conflict files messy for binary saves.

## UX phase decisions (locked 2026-06-22)
1. **Dashboard auth:** ~~rely on CloudFlare Access with Google auth~~ **Deferred (2026-06-25).**
   CloudFlare Tunnel has a 100 MB file size limit that may conflict with large save archives.
   The desktop PC (primary hub) doesn't travel. Agents only need LAN access.
   The dashboard can be exposed manually if needed. Real admin auth (distinct from machine
   keys) is still on the backlog but not blocking.
2. **Enrollment model:** a game is defined **once on the server** (via the
   dashboard); each agent simply **maps its own local save dir** to that game.
   Scanners suggest candidates, but the server game is the single definition.
3. **"Latest" nomenclature:** the authoritative version an agent pulls on initial
   sync is called **"Latest"** in the UI. This is the server **head** pointer
   (see [[Architecture]]); the dashboard labels it "Latest" and the initial-sync
   action is **"Set as Latest"** (designate which machine's save is authoritative).
4. **Artwork:** **download/cache** SteamGridDB images on the server (offline-safe,
   survives upstream art changes) rather than storing only URLs.
5. **Build order:** start with [[UX Roadmap]] Workstream 1 (tray UX) + 2 (scanning).

## Product name: SaveLocker (locked 2026-06-22)
The official product/brand name is **SaveLocker**. As of 2026-06-25:
- **Already renamed (user-visible):** config dir `%PROGRAMDATA%\SaveLocker`, single-instance
  mutex `"SaveLocker.Agent"`, registry Run-key value `"SaveLocker"`, installer AppName/publisher/
  output filename, wizard images, health check endpoint, tray/window/balloon text, all log
  comments, default DB path `savelocker.db` (with rename shim for existing installs).
- **Still `LocalGameSync` (internal code identifiers):** namespaces (`LocalGameSync.*`),
  solution file (`LocalGameSync.sln`), project file names, installer file (`SaveLocker.iss`
  but csproj name unchanged), Docker container DB path (`/data/localgamesync.db` — explicit
  config, unaffected by rename shim).
- The technical rename of code identifiers is a productization-phase task (see [[Future Work]]).
  Docs use **SaveLocker** for the product and `LocalGameSync` only when referring to actual
  code identifiers or file paths.

## Agent installer (locked 2026-06-22)
- **Tooling: Inno Setup 6** (over WiX/MSI and MSIX). Free, simple, full control over
  registry cleanup + uninstaller. MSIX rejected — its filesystem/registry virtualisation
  would interfere with the agent reading the Steam registry path + arbitrary save folders.
- **Script:** `installer/SaveLocker.iss` (renamed from `LocalGameSync.iss` on 2026-06-25).
  Build via `.\installer\build-installer.ps1`. Output: `installer/dist/SaveLocker-Agent-Setup-0.1.0.exe`.
- **Machine-wide install** to `C:\Program Files\SaveLocker Agent`, `PrivilegesRequired=admin`.
- **Why an installer at all (user reasoning):** auto-start writes a registry entry; a
  manually-deleted exe would orphan it. The uninstaller must own/revert every system
  change so users aren't left with dangling registry entries.
- **Uninstall keeps-or-removes config: ask the user.** The uninstaller prompts before
  deleting `%PROGRAMDATA%\SaveLocker` (API key + tracked games); *No* preserves it for
  a reinstall. Auto-start (in-app toggle) also requires an explicit consent dialog first.

## Environment facts (user-provided)
- Games are **standalone builds**, not bought on Steam/Epic/etc. → save locations
  unpredictable, hence manifest-based detection + manual `--dir` fallback.
- User has a domain on CloudFlare and uses **CloudFlare Tunnel** for remote access.
- Sync trigger: **hybrid** (automatic background + manual override).

See [[Architecture]] for how these manifested in code.
