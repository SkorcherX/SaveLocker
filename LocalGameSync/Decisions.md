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
1. **Dashboard auth:** rely on **CloudFlare Access with Google auth** — only
   email addresses in the access policy can reach the dashboard. No in-app login
   for now (personal project, single admin). Multi-admin "each with their own
   game dashboard" is a later exploration, not built now.
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
The official product/brand name is **SaveLocker**. The codebase remains named
`LocalGameSync` (namespaces, `LocalGameSync.sln`, repo path, installer `AppName`, the
single-instance mutex, the `%PROGRAMDATA%\LocalGameSync` config dir, the server database
`localgamesync.db`) until a deliberate **technical rename** scheduled for the productization
phase. Docs use **SaveLocker** for the product and `LocalGameSync` only when referring to
actual code identifiers or paths. The rename will touch: namespaces → `SaveLocker.*`, the
solution file, all project names, the installer, the mutex, paths, and the database. See
[[Future Work]] "Productization / branding" and [[Console Redesign]].

## Agent installer (locked 2026-06-22)
- **Tooling: Inno Setup 6** (over WiX/MSI and MSIX). Free, simple, full control over
  registry cleanup + uninstaller. MSIX rejected — its filesystem/registry virtualisation
  would interfere with the agent reading the Steam registry path + arbitrary save folders.
- **Per-user install** (`PrivilegesRequired=lowest`, `%LOCALAPPDATA%\Programs\…`): no
  admin prompt; auto-start is the per-user HKCU Run key anyway.
- **Why an installer at all (user reasoning):** auto-start writes a registry entry; a
  manually-deleted exe would orphan it. The uninstaller must own/revert every system
  change so users aren't left with dangling registry entries.
- **Uninstall keeps-or-removes config: ask the user.** The uninstaller prompts before
  deleting `%PROGRAMDATA%\LocalGameSync` (API key + tracked games); *No* preserves it for
  a reinstall. Auto-start (in-app toggle) also requires an explicit consent dialog first.

## Environment facts (user-provided)
- Games are **standalone builds**, not bought on Steam/Epic/etc. → save locations
  unpredictable, hence manifest-based detection + manual `--dir` fallback.
- User has a domain on CloudFlare and uses **CloudFlare Tunnel** for remote access.
- Sync trigger: **hybrid** (automatic background + manual override).

See [[Architecture]] for how these manifested in code.
