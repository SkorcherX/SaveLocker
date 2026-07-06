# UX Roadmap

Back to [[Home]]. The CLI proof-of-concept works ([[Progress]]). This phase makes
LocalGameSync usable by **non-technical everyday users**: the **dashboard** is the
primary admin surface, and the **tray agent** handles setup + sync with no CLI.
The CLI ([[CLI Reference]]) stays for power users/automation.

Guiding principle: the agent owns anything needing local disk/registry access
(scanning, restore); the dashboard owns administration (enroll, art, conflicts,
rollback, status). They meet at the server API.

## Workstream 1 — Tray agent UX (no CLI)
**Status (2026-06-21): first slice DONE & user-verified** — Settings/Connect window
(server URL, machine name, Register → API key + Copy, tracked-games list) + View
API key dialog + embedded app icon; clipboard STA bug and DPI clipping fixed (see
[[Progress]]). **Polish DONE (2026-06-22):** first-run prompt (`MaybeShowFirstRun`) +
auto-start on login (`AutoStart.cs` HKCU Run-key + "Start with Windows" checkbox). WS1 complete.

**Goal:** install, connect, and manage from the tray icon + small windows.
- Right-click tray menu: **Connect/Register**, **View API key** (with Copy),
  **Add games…** (opens scanner), per-game Force Pull/Push, Sync All,
  **Open Dashboard**, **Settings**, Exit.
- **Connect dialog:** enter server URL + machine name → calls `register` →
  shows the API key with a **Copy** button (to paste into the dashboard once).
- **Settings window:** server URL, machine name, re-register, view key, enrolled
  games list (add/remove). Replaces hand-editing `config.json`.
- **First-run:** if unregistered, prompt to set server + register.
- Reuses existing `SyncEngine`, `ApiClient`, `register`/`set-server`/`whoami`
  logic — this is a GUI over what the CLI already does.
- **Done when:** a user can install, connect, enroll a game, and sync without ever
  opening a terminal.

## Workstream 2 — Game discovery / scanning
**Status (2026-06-22): DONE & verified.** Binary + text VDF readers, `GameScanner`
(Steam shortcuts + installed games + save-root heuristic), `scan` CLI, and the tray
**"Add games…"** picker (`AddGamesForm.cs`) all shipped; picker confirmed on the
real machine. See [[Progress]].

**Goal:** pick games from a scanned list instead of typing paths.
See [[Game Discovery and Art]] for the technical design.
- Agent-side scanner: Steam `shortcuts.vdf` (non-Steam games) + installed Steam
  games + common save roots + Ludusavi manifest → candidate list with suggested
  save dir + "has Steam Cloud" flag.
- **Start with delivery option A** (agent settings window shows candidates to
  tick). Dashboard-driven scanning (option B) needs an agent command channel —
  defer to Workstream 5.
- **Done when:** "Add games…" shows a checklist of detected games; ticking one
  enrolls it (server `Game` + local `TrackedGame`) with the save dir pre-filled.

## Workstream 3 — Artwork (SteamGridDB)
**Status (2026-06-22): DONE & verified with real art.**
`ArtService` (search → fetch grid/hero/logo/icon → cache under `/data/art/{gameId}/`, `Storage:ArtRoot`),
`Game` art-URL fields (+additive migration), `POST /api/games/{id}/art/refresh`,
fetch-on-enroll, dashboard cover thumbnail + "Refresh art". Verified end-to-end with a
real `SteamGridDb:ApiKey`: Octopath got grid/hero/logo/icon cached + the cover serves.
(CDN images downloaded with a no-auth client.) See [[Progress]] / [[Game Discovery and Art]].

**Goal:** dashboard shows cover/hero/logo per game.
See [[Game Discovery and Art]] for endpoints/auth.
- Server-side SteamGridDB key in config; add art URL fields to `Game`; endpoint to
  (re)fetch art by game name (autocomplete → asset URLs); fetch on enroll + manual
  refresh; cache.
- Dashboard renders art on each game card.
- **Done when:** enrolled games display cover art automatically (when found),
  with a manual "refresh art" fallback.

## Workstream 4 — Dashboard as primary admin
**Status (2026-06-22): server + UI built, endpoints verified; browser check pending.**
Added `/api/machines`, `POST …/enabled`, `POST …/set-latest`; rebuilt the dashboard
with enable/disable + delete + Machines (last-sync) table + initial-sync wizard +
"Set as Latest"/Latest badge. Art on cards lands in WS3. See [[Progress]].

**Goal:** do day-to-day administration entirely in the browser.
- **Game-management panel** (agreed): list games with art; **enable/disable**;
  **delete** (endpoint already exists); see versions; rollback; resolve conflicts
  (exist); lease status; last-sync per machine.
- **"Set as Latest"** action (agreed helper): designate a chosen machine's version
  as **Latest** (the authoritative copy agents pull on initial sync). Surfaced
  with a **Latest** badge on the head version; today this is the `rollback`
  endpoint reframed + relabeled. Include an **initial-sync wizard**: "which machine
  has your real progress?" → that machine's save becomes Latest. See "Latest"
  nomenclature in [[Decisions]].
- **Done when:** enroll-status-resolve-rollback-enable/disable-delete are all
  doable from the dashboard.

## Workstream 5 — Agent command channel
**Status (2026-06-22): built & verified end-to-end (polling model).** Brought forward
(ahead of WS3) because it's what makes dashboard-created games reach agents. Agent
polls `GET /api/agent/commands` (~20s), runs pull/push/sync/scan, reports results;
each poll also reconciles the game list (adopt new server games, drop deleted ones).
Dashboard has per-machine action buttons + a command log. See [[Progress]] /
[[API Reference]].
**Open follow-up:** adopted games the manifest can't map are left unmapped — need an
agent save-folder picker to finish the loop.

Original plan:
- Agent polls `GET /api/agent/commands` (or websocket/SignalR) and reports results.
- Unlocks dashboard-driven scanning (option B) and remote control.

## Suggested order
1 → 2 → 4 (game panel + art display) → 3 (art fetch) → 5 (stretch).
Workstreams 1 and 2 give the biggest UX win (no CLI, pick-to-add).

## Decisions (locked — see [[Decisions]])
- **Auth:** Admin-password auth shipped (`AdminPasswordFilter`, PBKDF2-SHA256, set from
  ConfigView — 2026-06-25). CloudFlare Access + Google deferred; Cloudflare Tunnel also
  deferred (removed from roadmap 2026-06-25, commit `c0c41d1`). See [[Decisions]].
- **Enrollment:** game defined once on the server (dashboard); agent maps its local
  save dir. Scanner only *suggests* candidates.
- **Latest:** the authoritative version agents pull is labeled **Latest** (the head
  pointer); action to set it is **"Set as Latest"**.
- **Art:** download/cache SteamGridDB images on the server.
- **Start with:** Workstream 1 (tray UX) + 2 (scanning).
