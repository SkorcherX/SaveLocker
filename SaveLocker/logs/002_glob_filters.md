# Task: Per-game glob filters + upload size cap (hygiene 5e)

**Created:** 2026-07-12
**Status:** ✅ Complete (all layers built + verified; agent runtime application pending device test)
**Decisions (confirmed with user):** exclude-only • 200 MB configurable cap • global defaults + per-game overrides

## Goal

1. Exclude files matching per-game + global glob patterns from the save archive **and** the content hash (same filter for both, or conflict detection breaks).
2. Raise the game-upload body limit from Kestrel's implicit 30 MB to a configurable cap (default 200 MB).

## Design

- **Filter model:** exclude-only. Global defaults (`Sync:DefaultExcludeGlobs`, fallback `*.tmp *.log *.bak Thumbs.db desktop.ini`) apply to every game; each game adds its own via `Game.ExcludeGlobs` (newline-separated). Effective = global ∪ per-game.
- **Glob engine:** `Microsoft.Extensions.FileSystemGlobbing` (supports `*`, `**`, `?`, dir patterns).
- **Who applies it:** the agent, during hash + archive. The agent `/api/games` endpoint returns the **effective** merged set in `GameDto.ExcludeGlobs`; dashboard endpoints return per-game only.

## Steps

1. **Shared** — `SaveArchive`: add `Microsoft.Extensions.FileSystemGlobbing` ref; `EnumerateRelativeFiles(root, excludeGlobs)`, `HashDirectory(dir, excludeGlobs=null)`, `CreateArchive(src, zip, excludeGlobs=null)` (build zip entry-by-entry so excluded files are skipped). Null = no filter (back-compat).
2. **Server** — `Game.ExcludeGlobs` (string?) + EF migration `AddGameExcludeGlobs`. `GameDto.ExcludeGlobs` (string[]?). `Mapping` splits per-game. Agent `/games` merges global defaults → effective. `POST /api/games/{id}/excludes` (admin, JSON string[]). `SyncService.SetExcludeGlobsAsync`. Upload endpoint: set `MaxRequestBodySize` from `Storage:MaxUploadMb` (default 200). `ServerSettingsDto.DefaultExcludeGlobs` for dashboard display. appsettings keys. Regenerate `openapi.json`.
3. **Agent** — `TrackedGame.ExcludeGlobs` (List<string>); reconcile propagates `GameDto.ExcludeGlobs`; `SyncEngine` Push/Pull pass `game.ExcludeGlobs` to hash + archive.
4. **Web** — regen `api-types.ts`; `api.setExcludes`; GameDetail excludes editor + read-only global-defaults hint.

## Verify

- Unit-level: hash of a dir with a `foo.log` present == hash with it excluded via `*.log`.
- Build server + agent; typecheck web.
- Device (user): add `*.log` exclude to a game, sync, confirm the log isn't in the archive and no spurious version when only the log changes.
