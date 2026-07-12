# API Reference

Server endpoints (`src/Server/Program.cs`).

> **Live contract:** the server emits an OpenAPI document at `/openapi/v1.json` and an interactive Swagger UI at `/swagger`. The web dashboard's TypeScript types are generated from that document (`openapi-typescript` → `web/src/api-types.ts`), so they can't drift from the C# DTOs. This page is the human-readable narrative; the generated doc is the machine-checked source of truth. Regenerate per `Build and Run.md` after changing the API.

**Two auth tiers:**
- **Agent routes** (`X-Api-Key: <machine key>`) — identify the calling machine.
- **Admin routes** (`X-Admin-Password: <password>`) — dashboard / human operations. When no admin password is set the header is ignored and all admin routes are open. Check `GET /api/admin/status` to know if a password is required.

## Public (no auth)
- `POST /api/machines/register` `{ name }` → `{ machineId, apiKey }`. First-time registration of a new name is open. Re-registering an **existing** name rotates its key — requires `X-Admin-Password` once an admin password is set (**401** otherwise). With no password configured it stays open.
- `GET /health` → `{ service, status:"ok" }`.
- `GET /api/admin/status` → `{ passwordRequired }`.
- `GET /` → React admin dashboard (served from `wwwroot/` by ASP.NET static files).

## Games & state
- `GET /api/games` → `GameDto[]`
- `GET /api/machines` → `MachineDto[]` `{ id, name, createdAt, lastSeen }`
- `DELETE /api/machines/{id}` → 204 / 404 / 400. Removes leases + pending commands; keeps `SaveVersion`s. **400** if targeting the machine whose key authenticated the call.
- `POST /api/games` `{ name, manifestKey?, suggestedSaveDir? }` → `GameDto` (dedupes by name, case-insensitive + trimmed). Dashboard/admin use. Agents use `POST /api/agent/games`.
- `POST /api/games/{id}/enabled?value={bool}` → 200 / 404 (enable/disable a game; disabled games are skipped by agents).
- `POST /api/games/{id}/save-dir?value={path}` → 200 / 404. Set/clear the game's **suggested save folder**. Propagated to agents, which use it only if that path exists on that machine.
- `POST /api/games/{id}/art/refresh` → `{ message }` 200, or 400 if no SteamGridDB key configured. (Re)fetches cover/hero/logo/icon and caches under `/data/art/{gameId}/`. Also fetches automatically on first enrollment (best-effort).
- `DELETE /api/games/{id}` → 204 (admin: remove game + versions, archives, leases, conflicts).
- `GET /api/overview` → `GameStateDto[]` (dashboard's single fetch).
- `GET /api/games/{id}/state` → `GameStateDto`.
- `GET /api/games/{id}/versions` → `SaveVersionDto[]`.

## Server settings
- `GET /api/settings` → `ServerSettingsDto { steamGridDbConfigured, steamGridDbKeyMasked, steamGridDbFromConfig }`. Never returns the raw key.
- `POST /api/settings/steamgriddb-key` body `{ apiKey }` → `{ ok, message }`. Stores in DB then verifies. Null/blank clears the DB override (falls back to config).

## Leases
- `POST /api/games/{id}/lease` → `LeaseAcquireResponse { granted, lease }`. *(agent)*
- `POST /api/games/{id}/lease/renew` → 200 / 409. Renews the lease held by the calling machine; `SyncEngine` calls this on a 3 h timer during long play sessions. *(agent)*
- `DELETE /api/games/{id}/lease` → 204 (release own lease). *(agent)*
- `DELETE /api/games/{id}/lease/force` → 204 (admin force-release).

## Sync
- `POST /api/games/{id}/upload?hash={h}&parent={versionId?}&force={bool?}` body = zip → `UploadResult { status: Created|NoChange|Conflict, version, conflict }`.
- `GET /api/games/{id}/download` → head zip; response headers `X-Version-Id`, `X-Content-Hash`.
- `GET /api/versions/{versionId}/download` → that version's zip.

## Admin
- `GET /api/conflicts` → open `ConflictDto[]`.
- `POST /api/conflicts/{id}/resolve?version={winningVersionId}` → 200 / 400.
- `POST /api/games/{id}/rollback?version={versionId}` → 200 / 400.
- `POST /api/games/{id}/set-latest?version={versionId}` → 200 / 400. Same head-pointer move as rollback; backs the **"Set as Latest"** dashboard action + initial-sync wizard.
- `POST /api/games/{id}/retain?value={n?}` → 200 / 404. Set per-game version retention limit (null = global default).
- `DELETE /api/games/{id}/versions/{versionId}` → 200 / 404 / 400. Refuses if it is the head or referenced by an open conflict.
- `GET /api/games/{id}/paths` → `[{ machineId, machineName, path }]` — per-machine save path overrides.
- `POST /api/games/{id}/paths/{machineId}?value={path}` → 200. Set/clear the save path override.
- `DELETE /api/games/{id}/paths/{machineId}` → 204.
- `GET /api/audit?limit={n}` → `AuditLogDto[]` (default 200, max 1000).
- `POST /api/admin/password` `{ password }` → `{ ok, message }`. Set or clear the admin password.
- `POST /api/admin/backup` → `BackupResult { ok, message, backup, totalBackups }`. Take an immediate SQLite snapshot.
- `GET /api/admin/backups` → `BackupInfo[]` `{ fileName, sizeBytes, createdAt }`, newest first.

## Agent installer management (admin)
- `GET /api/admin/agent-installer` → `AgentInstallerStatus { version, fileName, uploadedAt, sizeBytes }`, or 204 if none hosted.
- `POST /api/admin/agent-installer?version={v}` — multipart `file` field (`.exe`). Stores installer + sidecar JSON; replaces any previous. Returns `AgentInstallerStatus`. Body limit: 200 MB.
- `DELETE /api/admin/agent-installer` → 204. Removes the hosted installer; agents stop being offered updates.

## Agent installer download (public)
- `GET /api/agent/installer/download` — streams the hosted installer binary. No auth. 404 if none hosted.

## Agent update check (agent-auth)
- `GET /api/agent/latest` → `AgentVersionInfo { latestVersion, downloadUrl }`, or 204 if no installer is configured. Checks the filesystem first (`AgentInstallerService`); falls back to static `AgentUpdate:LatestVersion` + `DownloadUrl` config.

## Agent command channel (agent-auth)
Polling model: agent makes outbound requests (~20 s). Each poll also reconciles the game list (adopt new server games, drop deleted ones).
- `POST /api/agent/games` `{ name, manifestKey?, suggestedSaveDir? }` → `GameDto` — agent enrollment (agent-auth; no admin password required).
- `GET /api/agent/commands` → `AgentCommandDto[]` — pending commands for the calling machine; claiming them flips each to `Dispatched`.
- `POST /api/agent/commands/{id}/result` `{ status, result }` → 200 / 404.
- `POST /api/agent/path/{gameId}?value={path}` → 200. Agent reports the locally resolved save path.
- `POST /api/commands` `{ machineId, gameId?, type, force }` → `AgentCommandDto` (dashboard queues a command; `type` = `Pull|Push|Sync|Scan`). *(admin)*
- `GET /api/commands` → recent `AgentCommandDto[]` (dashboard activity log). *(admin)*

DTO shapes live in `src/Shared/Contracts.cs`. Enums serialize as strings.
