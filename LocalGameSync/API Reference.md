# API Reference

Back to [[Home]]. Server endpoints (`src/Server/Program.cs`).

> **Live contract:** the server emits an OpenAPI document at `/openapi/v1.json` and an
> interactive Swagger UI at `/swagger`. The web dashboard's TypeScript types are generated
> from that document (`openapi-typescript` → `web/src/api-types.ts`), so they can't drift
> from the C# DTOs. This page is the human-readable narrative; the generated doc is the
> machine-checked source of truth. Regenerate per `web/README.md` after changing the API.

**Two auth tiers:**
- **Agent routes** (`X-Api-Key: <machine key>`) — identify the calling machine.
- **Admin routes** (`X-Admin-Password: <password>`) — dashboard / human operations.
  When no admin password is set the header is ignored and all admin routes are open.
  Check `GET /api/admin/status` to know if a password is required.

## Public (no auth)
- `POST /api/machines/register` `{ name }` → `{ machineId, apiKey }`. First-time
  registration of a new name is open. Re-registering an **existing** name rotates its
  key (so a re-installed agent can recover its identity) — but because that also lets a
  caller hijack the machine, it requires `X-Admin-Password` once an admin password is
  set (**401** otherwise). With no password configured it stays open. Agents supply it
  via the Settings "Admin Password" field or `agent register --admin-password`.
- `GET /health` → `{ service, status:"ok" }`.
- `GET /api/admin/status` → `{ passwordRequired }` — lets the UI decide whether to
  prompt for a password before any admin call.
- `GET /` → React admin dashboard (served from `wwwroot/` by ASP.NET static files).

## Games & state
- `GET /api/games` → `GameDto[]`
- `GET /api/machines` → `MachineDto[]` `{ id, name, createdAt, lastSeen }` (dashboard
  "Machines" panel / last-seen).
- `DELETE /api/machines/{id}` → 204 / 404 / 400. Delete a machine (revoke its API key).
  Removes its leases + pending commands; **keeps** its uploaded `SaveVersion`s as history.
  **400** if you target the machine whose key authenticated the request (no self-delete).
- `POST /api/games` `{ name, manifestKey?, customPathsJson?, suggestedSaveDir? }` → `GameDto`
  (dedupes by name, **case-insensitive + trimmed** so both machines map to one game).
  Dashboard / admin use. Agents use `POST /api/agent/games` (agent-auth) during enrollment.
- `POST /api/games/{id}/enabled?value={bool}` → 200 / 404 (admin: enable/disable a
  game; disabled games are skipped by agents).
- `POST /api/games/{id}/save-dir?value={path}` → 200 / 404. Set/clear the game's
  **suggested save folder**. Propagated to agents, which use it **only if that path
  exists on that machine**, else fall back to manifest detection or a manual map.
  `GameDto.suggestedSaveDir` carries it. The **resolved** per-machine folder is stored
  server-side as a `MachineSavePath` (see the `/paths` endpoints below).
- `POST /api/games/{id}/art/refresh` → `{ message }` 200, or 400 `{ message }` if no
  SteamGridDB key is configured / no match found. (Re)fetches cover/hero/logo/icon from
  SteamGridDB and caches them under `/data/art/{gameId}/` (`Storage:ArtRoot`; served at
  `/art/{gameId}/…`). Art also fetches automatically on first enrollment (best-effort).
  `GameDto`'s `gridUrl`/`heroUrl`/`logoUrl`/`iconUrl` are the served relative URLs
  (cache-busted).
  Requires a SteamGridDB key — set it in the dashboard (Server settings) or via config
  `SteamGridDb:ApiKey` (env `SteamGridDb__ApiKey`); the DB value wins.
- `DELETE /api/games/{id}` → 204 (admin: remove a game + its versions, archives,
  leases, conflicts).
- `GET /api/overview` → `GameStateDto[]` (dashboard's single fetch).

## Server settings (dashboard-managed)
- `GET /api/settings` → `ServerSettingsDto { steamGridDbConfigured, steamGridDbKeyMasked,
  steamGridDbFromConfig }`. Never returns the raw key — masked as `••••••••47ec`.
  `steamGridDbFromConfig` is true when the key is only in `appsettings`/env (no DB row).
- `POST /api/settings/steamgriddb-key` body `SetSteamGridDbKeyRequest { apiKey }` →
  `{ ok, message }`. Stores the key in the DB (`AppSetting`), then **verifies** it with a
  cheap authenticated SteamGridDB call (`ok:false` + reason if rejected). A null/blank
  `apiKey` clears the DB override (falls back to config). DB value overrides config.
- `GET /api/games/{id}/state` → `GameStateDto` (game, head, lease, hasOpenConflict).
- `GET /api/games/{id}/versions` → `SaveVersionDto[]`.

## Leases
- `POST /api/games/{id}/lease` → `LeaseAcquireResponse { granted, lease }`. *(agent)*
- `POST /api/games/{id}/lease/renew` → 200 / 409 (renews the lease held by the
  calling machine; `SyncEngine` calls this on a 3 h timer during long play sessions). *(agent)*
- `DELETE /api/games/{id}/lease` → 204 (release own lease). *(agent)*
- `DELETE /api/games/{id}/lease/force` → 204 (admin force-release). *(admin)*

## Sync
- `POST /api/games/{id}/upload?hash={h}&parent={versionId?}&force={bool?}`
  body = zip → `UploadResult { status: Created|NoChange|Conflict, version, conflict }`.
- `GET /api/games/{id}/download` → head zip; response headers `X-Version-Id`,
  `X-Content-Hash` (agent stores these as the next push's parent).
- `GET /api/versions/{versionId}/download` → that version's zip.

## Admin
- `GET /api/conflicts` → open `ConflictDto[]`.
- `POST /api/conflicts/{id}/resolve?version={winningVersionId}` → 200 / 400.
- `POST /api/games/{id}/rollback?version={versionId}` → 200 / 400.
- `POST /api/games/{id}/set-latest?version={versionId}` → 200 / 400. Same head-pointer
  move as rollback (audited `set_latest`); backs the dashboard **"Set as Latest"**
  action + initial-sync wizard. See "Latest" nomenclature in [[Decisions]].
- `POST /api/games/{id}/retain?value={n?}` → 200 / 404. Set the per-game version
  retention limit (null = use global default from `Storage:RetainVersionsPerGame`).
- `DELETE /api/games/{id}/versions/{versionId}` → 200 / 404 / 400. Delete a specific
  version (refuses if it is the head or referenced by an open conflict).
- `GET /api/games/{id}/paths` → `[{ machineId, machineName, path }]` — per-machine
  save path overrides for this game.
- `POST /api/games/{id}/paths/{machineId}?value={path}` → 200. Set (or clear if blank)
  the save path override for a specific machine.
- `DELETE /api/games/{id}/paths/{machineId}` → 204. Clear the save path override.
- `GET /api/audit?limit={n}` → `AuditLogDto[]` (default 200, max 1000). Full audit log.
- `POST /api/admin/password` `{ password }` → `{ ok, message }`. Set or clear the admin
  password (blank = disable password requirement).
- `POST /api/admin/backup` → `BackupResult { ok, message, backup, totalBackups }`. Take an
  immediate SQLite snapshot (`VACUUM INTO`) and prune to the retention count. A scheduled
  snapshot also runs nightly (`Backup:HourOfDay`, default 03:00) + a startup catch-up.
- `GET /api/admin/backups` → `BackupInfo[]` `{ fileName, sizeBytes, createdAt }`, newest
  first. Snapshots live under `Storage:BackupRoot` (default `/data/backups`).

## Agent command channel (Workstream 5)
Polling model: the agent makes outbound requests (server stays passive; works through
tunnels/firewalls). The agent identifies itself by its `X-Api-Key`.
- `POST /api/agent/games` `{ name, manifestKey?, … }` → `GameDto` — agent enrollment
  (same body as `POST /api/games`; uses agent-auth so the dashboard admin password
  is not required). Commit `47f6a3b`.
- `GET /api/agent/commands` → `AgentCommandDto[]` — the calling machine's **pending**
  commands; claiming them flips each to `Dispatched` so a later poll won't re-run them.
- `POST /api/agent/commands/{id}/result` `{ status, result }` → 200 / 404 (agent reports
  outcome; 404 if the command isn't this machine's).
- `POST /api/agent/path/{gameId}?value={path}` → 200. Agent reports the local save path
  it resolved for a game (stored in `MachineSavePaths`; used for per-machine path display
  in the dashboard).
- `POST /api/commands` `{ machineId, gameId?, type, force }` → `AgentCommandDto` (dashboard
  queues a command; `type` = `Pull|Push|Sync|Scan`, `gameId` null = all the machine's games). *(admin)*
- `GET /api/commands` → recent `AgentCommandDto[]` (dashboard activity log). *(admin)*

Besides commands, the agent also **reconciles the game list** each poll: it adopts server
games it isn't tracking yet (auto-mapping the save dir from the manifest when possible) and
drops local games deleted on the server. This is how a dashboard-created game reaches agents.

DTO shapes live in `src/Shared/Contracts.cs` (the source the OpenAPI schemas are built
from). Enums serialize as strings.
