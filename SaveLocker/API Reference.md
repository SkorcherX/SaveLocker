# API Reference

Server endpoints (`src/Server/Program.cs`).

> **Live contract:** the server emits an OpenAPI document at `/openapi/v1.json` and an interactive Swagger UI at `/swagger`. The web dashboard's TypeScript types are generated from that document (`openapi-typescript` → `web/src/api-types.ts`), so they can't drift from the C# DTOs. This page is the human-readable narrative; the generated doc is the machine-checked source of truth. Regenerate per `Build and Run.md` after changing the API.

**Two auth tiers:**
- **Agent routes** (`X-Api-Key: <machine key>`) — identify the calling machine.
- **Admin routes** (`X-Admin-Password: <password>`) — dashboard / human operations. When no admin password is set the header is ignored and all admin routes are open. Check `GET /api/admin/status` to know if a password is required.

## Public (no auth)
- `POST /api/machines/register` `{ name }` → `{ machineId, apiKey }`. First-time registration of a new name is open. Re-registering an **existing** name rotates its key — requires `X-Admin-Password` once an admin password is set (**401** otherwise). With no password configured it stays open.
- `POST /api/enroll` `{ token, machineName? }` → `{ machineId, apiKey, machineName }`. Spends a single-use enrollment token for a real machine key. **No auth filter, because the token *is* the credential** — a fresh agent has nothing else. Unknown, expired and already-spent tokens all answer **401**. If the token was minted *for* a machine name, that name is binding and `machineName` in the body is ignored — so a leaked file cannot be spent to claim another machine's identity. Redeeming for an existing name **rotates** that machine's key (the re-enrollment path for a wiped device), authorised by the token exactly as the admin password authorises re-registration.
- `GET /health` → `{ service, status:"ok" }`.
- `GET /api/admin/status` → `{ passwordRequired, build: { version, commit, builtAt, isRelease } }`. Reachability probe **and** the console's build identity. `version` is the product version shared with the agent (one git tag for the repo), carrying a `+{n}.{sha}` suffix on builds made after the nearest tag; `isRelease` is simply the absence of that suffix. `builtAt` is UTC, null when unstamped, and an unstamped local build reports `"dev"` rather than a plausible-looking number. Unauthenticated on purpose — the version has to be readable before you can authenticate, since a wrong admin password is one of the things you would be diagnosing. It does disclose the exact version and commit to anyone who can reach the port; accepted for a self-hosted LAN service. Values come from `SAVELOCKER_VERSION` / `_COMMIT` / `_BUILT_AT`, baked in by `docker-publish.yml` (see `BuildInfo.cs`).
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
- `GET /api/games/{id}/versions` → `SaveVersionDto[]`. Each version carries `protected`; protected
  versions are exempt from automatic retention until an admin clears it.

## Server settings
- `GET /api/settings` → `ServerSettingsDto { steamGridDbConfigured, steamGridDbKeyMasked, steamGridDbFromConfig, autoFetchHours }`. Never returns the raw key.
- `POST /api/settings/steamgriddb-key` body `{ apiKey }` → `{ ok, message }`. Stores in DB then verifies. Null/blank clears the DB override (falls back to config).
- `POST /api/settings/agent-update-auto-fetch` body `{ hours }` → `{ autoFetchHours }`. Sets the GitHub installer polling interval; `0` disables it. The server applies a console change within one minute and immediately polls when enabling or changing the interval.

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
- `GET /api/conflicts` → open `ConflictDto[]`. `escalated` becomes true after the conflict has been
  open for six hours.
- `POST /api/conflicts/{id}/resolve?version={winningVersionId}&keepBoth={bool}` → 200 / 400.
  Refuses to replace a newer current head with an older conflict option. With `keepBoth=true`, the
  chosen version becomes Latest and both conflict snapshots are protected from retention.
- `POST /api/games/{id}/rollback?version={versionId}` → 200 / 400.
- `POST /api/games/{id}/set-latest?version={versionId}` → 200 / 400. Same head-pointer move as rollback; backs the **"Set as Latest"** dashboard action + initial-sync wizard.
- `POST /api/games/{id}/retain?value={n?}` → 200 / 404. Set per-game version retention limit (null = global default).
- `DELETE /api/games/{id}/versions/{versionId}` → 200 / 404 / 400. Refuses if it is the head or referenced by an open conflict.
- `POST /api/games/{id}/versions/{versionId}/protected?value={bool}` → 200 / 404. Protect or
  unprotect a version from automatic retention.
- `GET /api/games/{id}/paths` → `[{ machineId, machineName, path }]` — per-machine save path overrides.
- `POST /api/games/{id}/paths/{machineId}?value={path}` → 200. Set/clear the save path override.
- `DELETE /api/games/{id}/paths/{machineId}` → 204.
- `GET /api/audit?limit={n}` → `AuditLogDto[]` (default 200, max 1000).
- `POST /api/admin/password` `{ password }` → `{ ok, message }`. Set or clear the admin password.
- `POST /api/admin/backup` → `BackupResult { ok, message, backup, totalBackups }`. Take an immediate SQLite snapshot.
- `GET /api/admin/backups` → `BackupInfo[]` `{ fileName, sizeBytes, createdAt }`, newest first.

## Agent health (agent-auth)
- `POST /api/agent/health` body `AgentHeartbeat { agentVersion, platform, lastSyncTime?, trackedGames, unmappedGames, offlineQueueDepth, events?, resolvedGameIds? }` → `AgentHeartbeatResponse { escalatedConflicts }`. Piggybacks the existing ~20 s poll, so it adds no schedule. **This is the only way a headless agent can tell anyone anything** (`Decisions.md` §2 — the console is the Deck's UI). A conflict open for more than six hours is returned so a Windows tray agent can notify the user even when the stuck machine is a Deck.
  - `events[]` = `{ code, severity, message, gameId?, occurredAt? }`. Codes are the fixed vocabulary in `src/Shared/AgentEventCodes.cs`. They carry only what the server **cannot infer** — a blocked pull, a missing save folder, a rejected upload, a settle-gate timeout, an unreachable server. (A *conflict* the server already knows about; the event ties it to the machine that is **stuck**.)
  - **Deduplicated** on (machine, game, code) while open: a persistent fault bumps `lastSeen`/`count` instead of adding a row every poll.
  - `resolvedGameIds[]` = games that just synced cleanly. Their open events **auto-close**, so a machine that recovers does not leave a stale alarm on the console.

## Agent health (admin)
- `GET /api/admin/health` → `AgentHealthDto[]` — every machine, **including ones that have never sent a heartbeat** (an agent enrolled but never run is exactly the case worth seeing). `online` is computed against a **5-minute** staleness window (`HealthService.StaleAfter`), which tolerates a few missed 20 s beats.
- `GET /api/admin/health/events` → open `AgentEventDto[]`, worst first.
- `POST /api/admin/health/events/{id}/dismiss` → 204 / 404. **Dismiss is not resolve**: it does not fix the condition, and if the condition still holds the agent's next report reopens it.

## Enrollment (admin)
- `POST /api/admin/enrollments` `{ machineName?, ttlMinutes?, serverUrl?, gameIds?, settleQuietSeconds?, settleMaxWaitSeconds? }` → `CreateEnrollmentResponse { id, policy }`. Mints a single-use token (default TTL **15 min**, max 24 h) and returns the **policy file** the agent consumes. **The raw token is in this response and nowhere else** — the server stores only its hash, so a policy that isn't saved here is unrecoverable. `serverUrl` defaults to the URL the console was reached on; override it when the admin is on the LAN but the agent must use the public tunnel. `gameIds` null = every enabled game.
- `GET /api/admin/enrollments` → `EnrollmentDto[]` (100 newest) `{ id, machineName, createdAt, expiresAt, redeemedAt, redeemedByMachineName }`. **Tokens whose window closed >24 h ago (`EnrollmentService.ListRetention`) are omitted** and pruned by the hourly sweep — their history lives in the audit log (`enrollment.create` records the expiry; an unredeemed one logs `enrollment.expire` when pruned).
- `DELETE /api/admin/enrollments/{id}` → 204 / 404. Revokes an unspent token. Deleting a *spent* one only drops the record — it does not revoke the API key it bought (delete the machine for that).

The policy file is **deliberately unsigned** (`Decisions.md` §4). Its `games` list only pre-seeds the agent; the server stays authoritative and the agent's reconcile corrects it.

## Agent installer management (admin)
- `GET /api/admin/agent-installer` → `AgentInstallerStatus { version, fileName, uploadedAt, sizeBytes }`, or 204 if none hosted.
- `POST /api/admin/agent-installer?version={v}` — multipart `file` field (`.exe`). Stores installer + sidecar JSON; replaces any previous. Returns `AgentInstallerStatus`. Body limit: 200 MB.
- `DELETE /api/admin/agent-installer` → 204. Removes the hosted installer; agents stop being offered updates.

The server can optionally keep the hosted installer current from GitHub through
**Configuration → Agent updates → Automatic GitHub fetch**. Set the interval in hours
(`0` disables it); dashboard values are stored in the server database and override
`AgentUpdate:AutoFetchHours` config/environment defaults. The scheduler only downloads
when the GitHub release is newer than the hosted installer.

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
