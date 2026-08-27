# API Reference

Server endpoints (`src/Server/Program.cs`).

> **Live contract:** the server emits an OpenAPI document at `/openapi/v1.json` and an interactive Swagger UI at `/swagger`. The web dashboard's TypeScript types are generated from that document (`openapi-typescript` → `web/src/api-types.ts`), so they can't drift from the C# DTOs. This page is the human-readable narrative; the generated doc is the machine-checked source of truth. Regenerate per `Build and Run.md` after changing the API.

**Two auth tiers:**
- **Agent routes** (`X-Api-Key: <machine key>`) — identify the calling machine.
- **Admin routes** (`X-Admin-Password: <password>`) — dashboard / human operations. When no admin password is set the header is ignored and all admin routes are open. Check `GET /api/admin/status` to know if a password is required.

> **This page is the SERVER's API only.** The agent serves a *separate* API on `:5178` that manages its own machine — different host, different port, different credential (`X-SaveLocker-Token` plus a loopback `Host` check, no CORS). It has its own OpenAPI document at `:5178/openapi/v1.json`; see `AgentApiServer.cs` and [[REPO_MAP]] → Auth model.

## Public (no auth)
- `POST /api/machines/register` `{ name }` → `{ machineId, apiKey }`. First-time registration of a new name is open. Re-registering an **existing** name rotates its key — requires `X-Admin-Password` once an admin password is set (**401** otherwise). With no password configured it stays open.
- `POST /api/enroll` `{ token, machineName? }` → `{ machineId, apiKey, machineName }`. Spends a single-use enrollment token for a real machine key. Burning the token and issuing the key are one transaction, so a failure part-way leaves the file usable rather than spent for nothing; concurrent redemptions still yield exactly one winner. **No auth filter, because the token *is* the credential** — a fresh agent has nothing else. Unknown, expired and already-spent tokens all answer **401**. If the token was minted *for* a machine name, that name is binding and `machineName` in the body is ignored — so a leaked file cannot be spent to claim another machine's identity. Redeeming for an existing name **rotates** that machine's key (the re-enrollment path for a wiped device), authorised by the token exactly as the admin password authorises re-registration.
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
- `GET /api/games/{id}/path-candidates` → `MachineScanCandidateDto[]` `{ machineId, machineName, suggestedPath, lastSeen }`. What each machine's own scan proposes for this game — what the dashboard offers when mapping a save folder by hand.
- `POST /api/games/{id}/excludes` body `string[]` → 200 / 404. Per-game exclude globs. Empty array clears them back to `Sync:DefaultExcludeGlobs`.
- `POST /api/games/{id}/conflict-policy` body `{ policy, preferredMachineId? }` → 200 / 404. `policy` = `Manual` (default — record it and wait for an admin) | `NewestWins` (the incoming version always wins, no conflict row) | `PreferMachine` (the designated machine's pushes advance the head; every other machine follows the `Manual` path). This is what selects the automatic head moves described under **Admin** below.
- `POST /api/games/{id}/prune` → `PruneResult { removed }`. Applies retention to this game now, rather than waiting for the next upload to trigger it.

## Server settings
- `GET /api/settings` → `ServerSettingsDto { steamGridDbConfigured, steamGridDbKeyMasked, steamGridDbFromConfig, adminPasswordSet, defaultExcludeGlobs, autoFetchHours }`. Never returns the raw key.
- `POST /api/settings/steamgriddb-key` body `{ apiKey }` → `{ ok, message }`. **Verifies, then stores** — a key that fails verification answers **400** `{ ok: false, message, keptExistingKey }` and leaves the existing key exactly as it was. The probe hits an authenticated endpoint (`grids/game/{id}`); `search/autocomplete` is public and would accept anything. Null/blank clears the DB override (falls back to config) without a verification round-trip.
- `POST /api/settings/agent-update-auto-fetch` body `{ hours }` → `{ autoFetchHours }`. Sets the GitHub installer polling interval; `0` disables it. The server applies a console change within one minute and immediately polls when enabling or changing the interval.

## Leases
- `POST /api/games/{id}/lease` → `LeaseAcquireResponse { granted, lease }`. *(agent)* Atomic: simultaneous callers get exactly one `granted: true`, and every other caller gets `granted: false` carrying the **holder's** lease — never an error. Re-acquiring your own live lease renews it. An expired lease is taken over in place.
- `POST /api/games/{id}/lease/renew` → 200 / 409. Renews the lease held by the calling machine; `SyncEngine` calls this on a 3 h timer during long play sessions. *(agent)*
- `DELETE /api/games/{id}/lease` → 204 (release own lease). *(agent)*
- `DELETE /api/games/{id}/lease/force` → 204 (admin force-release).

## Sync
- `POST /api/games/{id}/upload?hash={h}&parent={versionId?}&force={bool?}` body = zip → `UploadResult { status: Created|NoChange|Conflict|RetryFull, version, conflict }`. **413** if the body exceeds `Storage:MaxUploadMb` (default 200), counted while copying rather than from the declared length. The archive is staged and published atomically, so a refused or interrupted upload leaves no version and no file. Single-shot; kept only for an agent/server too old to have the chunked routes below.
- **Chunked upload** (`Gotchas.md` → *Hosting/network*, "Cloudflare's 100s edge timeout") — the normal path today, one small request per `ApiClient.UploadChunkBytes` (4 MiB) slice instead of one long one:
  - `POST /api/games/{id}/upload/begin` body `BeginUploadRequest { contentHash, parentVersionId?, force, files? }` → `BeginUploadResponse { sessionId?, noChange?, useDeltaPath, needPaths? }`. `files` — the agent's *complete* current per-file manifest (`{path, sha256, size}[]`, forward-slash relative paths) — is optional; omitting it (an old agent, `force`, a first-ever push, or a save below the delta-worthwhile size/count floor) always gets `useDeltaPath: false`, meaning the *next* request must carry the whole archive exactly as before. When `files` is sent and the server has a stored per-file baseline for its current head, `useDeltaPath: true` and `needPaths` names exactly which of the agent's declared paths need fresh bytes — everything else in `files` not in `needPaths` is copy-forwarded server-side from the base version's own archive. `noChange` short-circuits with no session, before a single byte moves, when the content already matches the head.
  - `PUT /api/games/{id}/upload/{sessionId}/chunk?offset={n}` body = raw bytes — either the whole archive (full path) or a small zip containing only `needPaths` (delta path). An offset behind what the session has already received is treated as a harmless retry replay; ahead of it is a conflict (a gap can only mean an earlier chunk never arrived).
  - `POST /api/games/{id}/upload/{sessionId}/complete` → `UploadResult`. On the delta path this is where reconstruction happens: the small payload is validated (entry count/size caps, and every entry must be one of the paths the server itself asked for) before anything is copied. `RetryFull` means the negotiated base moved on since Begin (another machine pushed in between) — the partial payload can no longer be trusted to reconstruct against; `ApiClient` retries transparently with a full archive, never surfaced past it.
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

Every head change the *server* decides — the two above, `conflicts/{id}/resolve`, and an automatic
`NewestWins`/`PreferMachine` upload — queues a deduplicated **unforced** `Pull` for each live machine
that syncs the game (mapped save path or uploaded version), skipping the uploader that already
learned the new head from its own upload response. Rollback and Set as Latest additionally supersede
any open conflict that **offers the chosen version** (audited as `conflict.resolve_superseded`);
conflicts between two other versions stay open. An ordinary push queues nothing. See `Decisions.md`.
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
- `POST /api/admin/enrollments` `{ machineName?, ttlMinutes?, serverUrl?, gameIds?, settleQuietSeconds?, settleMaxWaitSeconds? }` → `CreateEnrollmentResponse { id, policy }`. Mints a single-use token (default TTL **15 min**, max 24 h) and returns the **policy file** the agent consumes. **The raw token is in this response and nowhere else** — the server stores only its hash, so a policy that isn't saved here is unrecoverable. `gameIds` null = every enabled game.
  - `serverUrl` is the address the **agent** will use. Precedence: the explicit override → `Server:PublicBaseUrl` → the origin the console was reached on. Validated before a token is minted, so a bad value costs neither a token nor a retry: **400** if it is not an absolute `http(s)` URL, and **400** if an *inferred* URL is loopback (`localhost`, `127.x`, `::1`, `0.0.0.0`) — such a file would tell the new machine to sync with itself. A loopback URL you state explicitly (override or `Server:PublicBaseUrl`) is accepted: that is a same-box agent.
- `GET /api/admin/enrollments/effective-url` → `EffectiveServerUrl { url, fromConfig, isLoopback }`. What a policy minted right now would carry. The console shows it before the button is pressed, since the token is single-use.
- `GET /api/admin/enrollments` → `EnrollmentDto[]` (100 newest) `{ id, machineName, createdAt, expiresAt, redeemedAt, redeemedByMachineName }`. **Tokens whose window closed >24 h ago (`EnrollmentService.ListRetention`) are omitted** and pruned by the hourly sweep — their history lives in the audit log (`enrollment.create` records the expiry; an unredeemed one logs `enrollment.expire` when pruned).
- `DELETE /api/admin/enrollments/{id}` → 204 / 404. Revokes an unspent token. Deleting a *spent* one only drops the record — it does not revoke the API key it bought (delete the machine for that).

The policy file is **deliberately unsigned** (`Decisions.md` §4). Its `games` list only pre-seeds the agent; the server stays authoritative and the agent's reconcile corrects it.

## Agent installer management (admin)

**Every route in this section takes an optional `?platform=`.** Absent means `win-x64`, so a console or script written before platform slots existed keeps managing the Windows installer unchanged. Valid values are `win-x64`, `linux-x64` and `decky-plugin` (`AgentPlatform.All`); anything else is **400** rather than defaulted — `?platform=` selects a directory, and a caller asking for a platform this server does not host wants to hear so, not to be handed the Windows installer instead. Storage is `{root}` for `win-x64` and a subdirectory named for the platform beside it, so no deployed server needed migrating.

**`decky-plugin` is not an agent**, and rides these routes deliberately. It is the Decky plugin the *Linux agent* installs into `~/homebrew/plugins/SaveLocker` on the user's behalf, and a slot is exactly what it needs — an upload, a digest, an atomic replace and a version to compare — so it gets one rather than a near-identical second set of routes. It is also the only slot whose GitHub asset comes from a **different repository** (`AgentUpdate:Plugin:GitHubRepo`, default `SkorcherX/SaveLocker-Decky`), which is why a slot carries its own repo.

- `GET /api/admin/agent-installer` → `AgentInstallerStatus { version, fileName, uploadedAt, sizeBytes, sha256, platform }`, or 204 if none hosted **for that platform**.
- `POST /api/admin/agent-installer?version={v}` — multipart `file` field. Stores installer + sidecar JSON; replaces any previous **for that platform**. Returns `AgentInstallerStatus`.
  - **413** past `AgentUpdate:MaxInstallerMb` (default 200), enforced by Kestrel *and* counted while copying. The limit is raised from Kestrel's 30 MB default, never removed.
  - **400** for an empty filename, a version this server cannot parse, or a file whose name does not match the platform's expected shape — `SaveLocker-Agent-Setup-*.exe` for `win-x64`, `savelocker-*-linux-x64.tar.gz` for `linux-x64`, `SaveLocker*.zip` for `decky-plugin`. Checked before anything on disk is touched. (Matched with `EndsWith`, not `Path.GetExtension`: the Linux package's extension by that reckoning is `.gz`.)
  - Replacement is atomic and serialised against the GitHub fetch, the background poller and delete. A refused or interrupted upload leaves the previous installer and its metadata serving.
- `POST /api/admin/agent-installer/fetch-github` → fetches the newest release asset for that platform from GitHub now, instead of waiting for the poller. Same atomicity and validation as the upload above.
- `DELETE /api/admin/agent-installer` → 204. Removes that platform's hosted installer; its agents stop being offered updates. The other platform is untouched.

The server can optionally keep the hosted installer current from GitHub through
**Configuration → Agent updates → Automatic GitHub fetch**. Set the interval in hours
(`0` disables it); dashboard values are stored in the server database and override
`AgentUpdate:AutoFetchHours` config/environment defaults. The scheduler only downloads
when the GitHub release is newer than the hosted installer.

## Agent installer download (public)
- `GET /api/agent/installer/download?platform={p}` — streams the hosted installer binary for that platform. No auth. 404 if none hosted; **400** for an unknown platform. Absent `platform` = `win-x64`.

## Agent update check (agent-auth)
- `GET /api/agent/latest?platform={p}` → `AgentVersionInfo { latestVersion, downloadUrl, sha256 }`, or 204 if no installer is configured for that platform. Checks the filesystem first (`AgentInstallerService`); falls back to static `AgentUpdate:LatestVersion` + `DownloadUrl` config, with the Linux fallback under `AgentUpdate:Linux:*` and the plugin's under `AgentUpdate:Plugin:*` (env `AgentUpdate__Linux__DownloadUrl` — deliberately not a dashed section name, because `AgentUpdate__linux-x64__…` is not a legal bash identifier and the Linux harness has to export it).
  - The returned `downloadUrl` **names the platform** (`…/download?platform={slot}`). That is what stops an agent following it to the other OS's package and failing a digest check it could never pass.
  - `sha256` is the digest of the bytes this server stored, so the update channel does not depend on release-side provenance.
  - The config fallback resolves its platform from the **normalised** slot, not the raw parameter. That distinction is not cosmetic: while it compared the raw (null) value, every agent that sent no `?platform=` — all of them, before agents learned to — was routed to the empty Linux section and got 204, i.e. the entire Windows fleet would have gone silently "up to date" forever. Both paths now have their own coverage.
  - The hosted `downloadUrl` uses `Server:PublicBaseUrl` when set, else the request origin — same resolver as the enrollment policy.

## Agent command channel (agent-auth)
Polling model: agent makes outbound requests (~20 s). Each poll also reconciles the game list (adopt new server games, drop deleted ones).
- `POST /api/agent/games` `{ name, manifestKey?, suggestedSaveDir? }` → `GameDto` — agent enrollment (agent-auth; no admin password required).
- `GET /api/agent/games/{id}/state` → `GameStateDto` / 404. The agent-auth twin of `/api/games/{id}/state`, so an agent can read one game's head without an admin password.
- `POST /api/agent/games/{id}/template?value={template}` → 200 / 204 / 400. An agent describing the save location **generically** (a manifest-style template), so every other machine expands it for itself rather than inheriting a literal path that means nothing on its filesystem. **Only fills an empty value** — it never overwrites one machine's answer with another's — and rejects anything that is not a template. **204** means there was already a value.
- `GET /api/agent/commands` → `AgentCommandDto[]` — claims the calling machine's due commands under a **visibility lease** (`Commands:LeaseMinutes`, default 10). Due = `Pending`, or `Dispatched` with an expired lease. One atomic claim, so two pollers on one machine identity cannot both get a command; `claimCount > 1` means an earlier delivery went unanswered. Delivery is at-least-once — see `Decisions.md`.
- `POST /api/agent/commands/{id}/result` `{ status, result }` → 200 / 404. Idempotent: a late or duplicate report for an already-completed command returns 200 without reopening it.
- `POST /api/agent/path/{gameId}?value={path}` → 200. Agent reports the locally resolved save path.
- `POST /api/commands` `{ machineId, gameId?, type, force }` → `AgentCommandDto` (dashboard queues a command; `type` = `Pull|Push|Sync|Scan`). *(admin)*
- `GET /api/commands` → recent `AgentCommandDto[]` (dashboard activity log). *(admin)*

DTO shapes live in `src/Shared/Contracts.cs`. Enums serialize as strings.
