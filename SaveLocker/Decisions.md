# Decisions

Settled choices. Don't re-litigate without a reason.

## Build philosophy — Hybrid
Reuse the open-source **Ludusavi manifest** (community DB mapping thousands of games → save locations, from PCGamingWiki) for detection. Build our own agent + server + dashboard for orchestration, leasing, and conflict handling. Do **not** re-map save locations ourselves.

## Conflict prevention — Proactive lock/lease
Server tracks an active "checkout" per game (like Steam Cloud's "in use"). Agent pulls latest before launch; the other machine is warned if saves are leased elsewhere. Content-hash + parent-version lineage is the fallback detector.

## Tech stack — Single-language .NET 9
- Agent in C#/WinForms: best Windows integration (FileSystemWatcher, process watch, tray, single-file exe).
- Server in ASP.NET Core, runs in Docker on unRAID. One language end-to-end.

## unRAID as hub (vs peer-to-peer)
- Asynchronous decoupling: PC pushes; laptop pulls later even if PC is off.
- Single source of truth → trivial "who synced last" + conflict resolution.
- Versioned history/rollback in one place.
- Already always-on, has storage, Docker, internet-reachable via **CloudFlare Tunnel**.
- Rejected raw Syncthing: continuous sync risks copying mid-write; conflict files messy for binary saves.

## UX phase decisions (locked 2026-06-22)
1. **Dashboard auth:** real admin auth shipped (2026-06-25) — `AdminPasswordFilter` + PBKDF2-SHA256, set from ConfigView. CloudFlare Access + Google deferred; blocked by Cloudflare Tunnel's 100 MB file limit (conflicts with large save archives).
2. **Enrollment model:** a game is defined **once on the server** (via the dashboard); each agent **maps its own local save dir**. Scanners suggest candidates; the server game is the single definition.
3. **"Latest" nomenclature:** the authoritative version agents pull is called **"Latest"** in the UI — this is the server **head** pointer. The dashboard labels it "Latest"; the admin action is **"Set as Latest"**.
4. **Artwork:** **download/cache** SteamGridDB images on the server (offline-safe, survives upstream art changes) rather than storing only URLs.

## Product name: SaveLocker (locked 2026-06-22)
The official product/brand name is **SaveLocker**. Rename is complete (2026-07-10):
- **User-visible:** config dir `%PROGRAMDATA%\SaveLocker`, single-instance mutex `"SaveLocker.Agent"`, registry Run-key `"SaveLocker"`, installer AppName/publisher, wizard images, health check, tray/window/balloon text, log paths, DB path `savelocker.db` (with rename shim for existing installs on `localgamesync.db`).
- **Code identifiers:** namespaces (`SaveLocker.*`), solution (`SaveLocker.sln`), project files (`SaveLocker.Agent/Server/Shared.csproj`) — all renamed 2026-07-10.
- **Note for existing Docker deployments:** the server DB at `/data/localgamesync.db` needs to be renamed to `/data/savelocker.db` (or override `Storage__DbPath`). The rename shim handles this automatically on the agent side.

## Agent installer (locked 2026-06-22)
- **Tooling: Inno Setup 6** (over WiX/MSI and MSIX). Free, full control over registry cleanup + uninstaller. MSIX rejected — its virtualisation would interfere with the agent reading the Steam registry + arbitrary save folders.
- **Script:** `installer/SaveLocker.iss`. Build via `.\installer\build-installer.ps1`. Output: `installer/dist/SaveLocker-Agent-Setup-{version}.exe`.
- **Machine-wide install** to `C:\Program Files\SaveLocker Agent`, `PrivilegesRequired=admin` (UAC up front).
- **Why an installer:** auto-start writes a registry entry; a manually-deleted exe would orphan it. The uninstaller must own and revert every system change.
- **Uninstall:** prompts before deleting `%PROGRAMDATA%\SaveLocker` (API key + tracked games config); *No* preserves it for a reinstall.

## Environment facts (user-provided)
- Games are **standalone builds**, not bought on Steam/Epic → save locations unpredictable, hence manifest-based detection + manual `--dir` fallback.
- User has a domain on CloudFlare and uses **CloudFlare Tunnel** for remote access.
- Sync trigger: **hybrid** (automatic background + manual override).
