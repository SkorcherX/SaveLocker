# SaveLocker — Home

> **Official product name: SaveLocker** (locked 2026-06-22 — see [[Decisions]]).
> "LocalGameSync" remains the **codebase/technical name** (namespaces, `LocalGameSync.sln`,
> repo path `C:\Projects\LocalGameSync`, installer `AppName`, single-instance mutex) until a
> deliberate code rename — tracked as a task in [[Future Work]]. Docs use **SaveLocker** for
> the product and `LocalGameSync` only when referring to actual code identifiers/paths.

Sync save-game data for games **without native cloud save** between a gaming PC
and a laptop, using an always-on **unRAID server** as the authoritative hub.

Core problem being solved: never silently overwrite newer progress when playing
on both machines. Prevented with a **lease/checkout** model + content-hash
conflict detection, plus an **admin dashboard** for resolution and rollback.

## Map of content
- [[Architecture]] — components, data model, sync flows
- [[Decisions]] — locked choices and why
- [[Progress]] — phase status and what's verified
- [[Build and Run]] — dev build, server, unRAID deploy, agent CLI
- [[CLI Reference]] — every agent command + options
- [[API Reference]] — server REST endpoints
- [[UX Roadmap]] — the plan to move off the CLI (dashboard + tray UX)
- [[Game Discovery and Art]] — Steam scanning + SteamGridDB design
- [[Future Work]] — what's deliberately not built yet
- [[Gotchas]] — traps that have already bitten us
- [[Hygiene Review 2026-07-06]] — repo audit findings + model-assigned action list

## At a glance
- **Stack:** .NET 9. Agent = C#/WinForms tray + CLI. Server = ASP.NET Core +
  EF Core/SQLite in Docker. Shared class library.
- **Repo root:** `C:\Projects\LocalGameSync`
- **Solution:** `LocalGameSync.sln` → `src/Shared`, `src/Server`, `src/Agent`
- **Status:** PoC complete + the **UX phase functionally done** — tray UX, game
  scanning, dashboard admin, agent command channel, save-folder mapping, and
  SteamGridDB cover art all built & verified. Agent versioning + auto-update fully
  shipped 2026-07-10/11: MinVer git-tag versioning, release CI, server-hosted installer,
  console installer management UI, live version in agent UI (see [[Agent Auto-Update]]).
  Remaining: code-signing, per-game glob filters, console redesign polish.
  See [[Progress]] (milestone queue) and [[UX Roadmap]].
