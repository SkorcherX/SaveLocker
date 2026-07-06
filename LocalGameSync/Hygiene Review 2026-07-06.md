# Hygiene Review — 2026-07-06

Back to [[Home]]. Full-repo hygiene review (read-only pass): dependency audit,
commit hygiene, doc↔code consistency, and a prioritized action plan. Iterate
through **Actions** below as time/tokens allow; each task dictates the model
to run it with (**Opus 4.8** for risk/architecture work, **Sonnet 4.6** for
mechanical/doc work).

Decisions locked with the user during review:
- **Remove** `design_handoff_savelocker/` + `design_handoff_savelocker_agent/` (spent prototypes, already ported).
- **Ignore** Obsidian `workspace.json` / `graph.json` (keep `app.json`/`appearance.json`/`core-plugins.json` tracked).
- **Commit** the `.verify/` test scripts into the repo.

## Findings

### A. Dependencies — clean ✅
Every declared requirement is used. No unused NuGet or npm packages:
- Server: `Microsoft.EntityFrameworkCore.Sqlite/Design` (migrations), `SixLabors.ImageSharp` (`ArtService` hero downscale).
- Shared: `YamlDotNet` (`ManifestLoader`). Agent: `Microsoft.Web.WebView2` (`AgentWindow`).
- web: react/react-dom + tailwind toolchain all used. agent-ui: `lucide-react` used in all 6 components.
- **No secrets in any tracked file** — the `apiKey` in the agent design handoff was a mock; CI uses `GITHUB_TOKEN` correctly.

### B. Commit hygiene
1. **`.verify/` tests untracked** — the only test suite (`run-agent-tests.ps1`, the
   10-check integration run referenced by [[Progress]] / [[Build and Run]]) lives only
   on the dev machine. A disk failure or fresh clone loses it.
2. **`.obsidian/workspace.json` + `graph.json` tracked** — per-session UI state that
   churns every Obsidian session → noisy diffs.
3. **Design handoff folders** — spent one-time prototypes (both ported to `web/` and
   `agent-ui/`), carrying 2×1.1 MB duplicate logos.
4. **`appsettings.Development.json` gitignored under "Secrets"** but it only holds dev
   storage paths (`localstate/` — see [[Gotchas]]). A fresh clone silently loses the
   documented dev behavior and would default to the Docker `/data` path (→ `C:\data` on Windows).
5. **Legacy console `src/Server/wwwroot/index.html` untracked** (all of `wwwroot/` is
   ignored as build output), yet [[Console Redesign]] / [[Progress]] describe it as a
   live fallback/debug tool. It exists only on the dev machine — archive it or declare it retired.
6. Minor: logo PNG duplicated 5× (~5.6 MB; 3× after handoff deletion); `web/README.md`
   is unmodified Vite template boilerplate; unused template assets
   (`web/src/assets/react.svg`, `vite.svg`, `hero.png`, `web/public/icons.svg`);
   `.dockerignore` doesn't exclude docs/design/agent-ui (build-context bloat only).

### C. Doc ↔ code drift (docs lag the last ~6 commits)
1. **[[Architecture]]**: says schema via `EnsureCreated()` "migrations deferred" —
   migrations shipped 2026-06-24 (`Migrate()` + bootstrap shim). Says the dashboard is
   vanilla-JS `wwwroot/index.html` — it's the React `web/` app baked in by Docker.
   Data-model list omits `AgentCommand`, `AppSetting`, `MachineSavePaths`.
2. **[[API Reference]]**:
   - Auth model outdated: claims all `/api/*` need `X-Api-Key`; code has two filter
     groups — agent (`X-Api-Key`) vs admin (`X-Admin-Password` via `AdminPasswordFilter`).
   - Agent enrollment moved to `POST /api/agent/games` (commit `47f6a3b`); doc still
     says agents use `POST /api/games`.
   - Missing endpoints: `GET /api/admin/status`, `POST /api/admin/password`,
     `POST /games/{id}/lease/renew`, `POST /games/{id}/retain`,
     `DELETE /games/{id}/versions/{versionId}`,
     `GET|POST|DELETE /games/{id}/paths[/{machineId}]`,
     `POST /api/agent/path/{gameId}`, `GET /api/audit`.
   - Art location stale: "cached under `wwwroot/art/`" — moved to `/data/art`
     (`Storage:ArtRoot`, commit `8eae726`). Same staleness in [[UX Roadmap]] WS3 and
     [[Game Discovery and Art]].
3. **[[Future Work]] + [[Progress]] milestone #10**: lease auto-renew/heartbeat listed
   as pending — **shipped** in commit `ee27a57` (`SyncEngine` 3 h renew timer,
   `/lease/renew` endpoint; the README already documents it). Milestone #11 (installer
   artwork) likely also done (`21b0bb9`) — confirm.
4. **[[Progress]]** "Last updated 2026-06-25" — commits through 06-27 undocumented
   (lease heartbeat, enrollment-401 fix, stats/timezone fix, agent-window fix,
   art-volume persistence).
5. **[[UX Roadmap]]** "Decisions (locked)" still says auth = CloudFlare Access +
   Google, "no in-app login" — contradicts [[Decisions]] (deferred 06-25) and the
   built admin-password auth.
6. **[[CLI Reference]]** missing the `log` command (exists in agent `Program.cs`,
   only mentioned in [[Gotchas]]).

### D. Code-level observations (no bugs found; two design warts)
- **`MachineSavePaths` lives outside EF** — created by raw `CREATE TABLE IF NOT EXISTS`
  at startup (`Program.cs`), queried via raw SQL in `SyncService`; no entity, no
  migration. Works, but future migrations/model snapshots won't know it exists.
- **`POST /api/machines/register` is unauthenticated and rotates keys by name** —
  anyone who can reach the server can re-register an existing machine name and hijack
  its identity (the real agent gets locked out). Acceptable on trusted LAN; harden
  before any tunnel exposure.
- Raw SQL in `SyncService` uses `FormattableString` interpolation → parameterized by
  EF; safe.

## Actions — iterate as time/tokens allow

| # | Task | Model | Status |
|---|---|---|---|
| 1 | **Repo cleanup commit** — `git rm -r` both design-handoff folders; gitignore + `git rm --cached` `.obsidian/workspace*.json` / `graph.json`; delete unused template assets (`web/src/assets/react.svg`, `vite.svg`, `hero.png`, `web/public/icons.svg`); rewrite `web/README.md` (5 lines about the dashboard); extend `.dockerignore` (`LocalGameSync/`, `docs/`, `agent-ui/`, `installer/`, `web/node_modules/`). | **Sonnet 4.6** — mechanical file ops, zero design judgment. | ☐ |
| 2 | **Bring tests + dev config into the repo** *(needs the Windows machine — files aren't in the clone)* — move `.verify/run-agent-tests.ps1` (+ siblings) to a tracked `tests/` folder, keep `.verify/` ignored for scratch output; commit a sanitized `appsettings.Development.json` (localstate paths, no keys); decide legacy `wwwroot/index.html` fate (archive under `docs/legacy-console/` or retire in docs); update [[Build and Run]] / [[Progress]] references. | **Sonnet 4.6** — file moves + doc touch-ups. | ☐ |
| 3 | **Docs refresh pass** — fix everything in finding C: [[Architecture]] (Migrate(), React dashboard, full table list), [[API Reference]] (two-tier auth, `/api/agent/games`, 9 missing endpoints, `/data/art`), [[Future Work]] + [[Progress]] (heartbeat done, 06-26/27 session entry, confirm installer-artwork status), [[UX Roadmap]] (stale auth decision), [[CLI Reference]] (`log`). | **Sonnet 4.6** — the findings enumerate exactly what to write; cross-check against `Program.cs` while editing. | ☐ |
| 4a | **Fold `MachineSavePaths` into EF** — entity + `DbSet` + migration, mirroring the RetainVersions bootstrap-stamp pattern so existing DBs (ThunderHorse/Wideboy) migrate cleanly; replace raw SQL in `SyncService`. | **Opus 4.8** — touches live production DBs; a wrong migration bricks the server on deploy. | ☐ |
| 4b | **Guard machine-key rotation** — re-registering an *existing* machine name requires the admin password (or explicit dashboard toggle); first-time registration stays open. | **Opus 4.8** — auth-flow change; must not break agent enrollment (see the `47f6a3b` 401 regression). | ☐ |
| 5a | **Server-side SQLite backup** — nightly `VACUUM INTO /data/backups/` + retention; the DB *is* the version graph, archives are useless without it. | **Opus 4.8** — data-safety feature; needs care around WAL + live writes. | ☐ |
| 5b | **Swagger/OpenAPI + generated TS clients** (`openapi-typescript`) for both UIs — kills API-doc drift (finding C) at the source. Already recommended in [[Console Redesign]]. | **Opus 4.8** — refactors both UIs' `api.ts`/`types.ts` against a generated contract. | ☐ |
| 5c | **Background lease sweep** (expire stale leases proactively) + Docker `HEALTHCHECK` in Dockerfile/compose. | **Sonnet 4.6** — small, well-scoped additions. | ☐ |
| 5d | **Toolchain alignment + CI** — align agent-ui to web's versions (vite 8 / TS 6), add oxlint + `lint` script to agent-ui, add a PR workflow that builds both UIs + `dotnet build` (today CI only builds the server image on `main`). | **Sonnet 4.6** — config/versioning work; build output verifies it. | ☐ |
| 5e | **Per-game include/exclude globs** before archiving + upload size limit on `/upload`. | **Opus 4.8** — globs change content hashing → conflict-detection semantics. | ☐ |
| 5f | **Bigger swings (later)** — agent auto-update; Linux/Steam Deck agent (server already cross-platform; `PathResolver` is the Windows-only piece); code-signing (existing backlog). | **Opus 4.8** — architectural. | ☐ |

## Verification
- Tasks 1–3: `git status` clean (no stray leftovers), `docker build -f src/Server/Dockerfile .` succeeds, both `npm run build`s pass.
- Task 4a: run the migration against a **copy** of a real DB (bootstrap-shim path) before deploying.
- Task 4b: exercise register / re-register / agent enrollment with and without an admin password set.
