# API Reference

Back to [[Home]]. Server endpoints (`src/Server/Program.cs`). All `/api/*` except
register require header `X-Api-Key: <machine key>`.

## Public
- `POST /api/machines/register` `{ name }` → `{ machineId, apiKey }`
  (re-registering an existing name rotates its key).
- `GET /health` → `{ service, status:"ok" }`.
- `GET /` → admin dashboard (static `wwwroot/index.html`).

## Games & state
- `GET /api/games` → `GameDto[]`
- `GET /api/machines` → `MachineDto[]` `{ id, name, createdAt, lastSeen }` (dashboard
  "Machines" panel / last-seen).
- `DELETE /api/machines/{id}` → 204 / 404 / 400. Delete a machine (revoke its API key).
  Removes its leases + pending commands; **keeps** its uploaded `SaveVersion`s as history.
  **400** if you target the machine whose key authenticated the request (no self-delete).
- `POST /api/games` `{ name, manifestKey?, customPathsJson?, suggestedSaveDir? }` → `GameDto`
  (dedupes by name, **case-insensitive + trimmed** so both machines map to one game).
- `POST /api/games/{id}/enabled?value={bool}` → 200 / 404 (admin: enable/disable a
  game; disabled games are skipped by agents).
- `POST /api/games/{id}/save-dir?value={path}` → 200 / 404. Set/clear the game's
  **suggested save folder**. Propagated to agents, which use it **only if that path
  exists on that machine**, else fall back to manifest detection or a manual map.
  `GameDto.suggestedSaveDir` carries it. (Per-machine paths are not stored server-side.)
- `POST /api/games/{id}/art/refresh` → `{ message }` 200, or 400 `{ message }` if no
  SteamGridDB key is configured / no match found. (Re)fetches cover/hero/logo/icon from
  SteamGridDB and caches them under `wwwroot/art/{gameId}/`. Art also fetches
  automatically on first enrollment (`POST /api/games`, best-effort). `GameDto`'s
  `gridUrl`/`heroUrl`/`logoUrl`/`iconUrl` are the served relative URLs (cache-busted).
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
- `POST /api/games/{id}/lease` → `LeaseAcquireResponse { granted, lease }`.
- `DELETE /api/games/{id}/lease` → 204 (release own lease).
- `DELETE /api/games/{id}/lease/force` → 204 (admin force-release).

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

## Agent command channel (Workstream 5)
Polling model: the agent makes outbound requests (server stays passive; works through
tunnels/firewalls). The agent identifies itself by its `X-Api-Key`.
- `GET /api/agent/commands` → `AgentCommandDto[]` — the calling machine's **pending**
  commands; claiming them flips each to `Dispatched` so a later poll won't re-run them.
- `POST /api/agent/commands/{id}/result` `{ status, result }` → 200 / 404 (agent reports
  outcome; 404 if the command isn't this machine's).
- `POST /api/commands` `{ machineId, gameId?, type, force }` → `AgentCommandDto` (dashboard
  queues a command; `type` = `Pull|Push|Sync|Scan`, `gameId` null = all the machine's games).
- `GET /api/commands` → recent `AgentCommandDto[]` (dashboard activity log).

Besides commands, the agent also **reconciles the game list** each poll: it adopts server
games it isn't tracking yet (auto-mapping the save dir from the manifest when possible) and
drops local games deleted on the server. This is how a dashboard-created game reaches agents.

DTO shapes live in `src/Shared/Contracts.cs`. Enums serialize as strings.
