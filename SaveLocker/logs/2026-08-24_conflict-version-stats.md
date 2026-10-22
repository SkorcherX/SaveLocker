# 2026-08-24 — File-count / newest-mtime delta in the conflict UI (conflict Tier 1, last piece)

Branch `claude/conflict-ui-file-delta-3df975`. Not merged, not deployed.

## The question

Backlog carried this as the one item left in an otherwise-complete conflict Tier 1: the conflict
card shows a size and an upload time for each side, but neither tells a user which machine actually
has "more" progress — a smaller, newer save can still be the one to keep. Asked directly whether
adding it would be hard, then asked to implement it, with one constraint: keep in mind that conflict
management is expected to move to the agent later, the way Steam Cloud resolves conflicts itself.

## What made this easier than the backlog note implied

The backlog framed it as a choice between "compute at upload time" (needs a DB migration, a wire
field) and "derive from the archive on demand" (no migration, but re-opens the zip). Reading
`SaveArchive.CreateArchive` (`src/Shared/SaveArchive.cs`) found this wasn't really a choice:
`entry.LastWriteTime = File.GetLastWriteTime(full)` has been set on every zip entry since the method
was written, long before this task existed. So the newest-mtime signal already lives inside every
archive ever uploaded — deriving on demand costs nothing extra and works retroactively, with no
backfill. File count is just the entry count, minus directory entries.

## The fix

- `SaveArchive.GetArchiveStats(zipPath)` (Shared) — opens the zip, counts real entries, tracks the
  max `LastWriteTime`. Returns `readonly record struct ArchiveStats(int FileCount, DateTime?
  NewestFileWriteUtc)`.
- `VersionStatsDto(int FileCount, DateTime? NewestFileWriteUtc)` (`src/Shared/Contracts.cs`) — the
  wire shape, deliberately separate from `SaveVersionDto` so listing versions never has to open a
  zip for versions nobody is looking at.
- `SyncService.GetVersionStatsAsync` — two overloads sharing one body: `(Guid versionId)` for the
  agent group's flat route (unscoped, matching `DownloadVersionAsync`'s own precedent — a valid
  machine key already gates the group), and `(Guid gameId, Guid versionId)` for the admin group's
  nested route, which checks the version actually belongs to that game before answering.
- Two new endpoints, both backed by the same service call:
  `GET /api/versions/{versionId}/stats` (agent group) and
  `GET /api/games/{id}/versions/{versionId}/stats` (admin group).
- Console: `GameDetail.tsx`'s conflict card fetches stats lazily for only the two versions an open
  conflict is currently showing, cached in a `useRef` `Set` keyed by version id so the 15s poll in
  `App.tsx` (which hands `conflicts` down as a fresh array reference every time) never re-fetches a
  version's stats once they're known — an uploaded archive's contents never change, so the cache
  never needs invalidating. Renders as `"N files · newest change <time>"` under the existing
  machine/time/size line, spacing kept stable (`marginTop` on the button row, not the stats line)
  so the card doesn't jump once the fetch resolves.

## Built with the agent-migration constraint in mind

The agent-group route (`/api/versions/{versionId}/stats`) has no UI caller today — the console only
calls the admin-scoped one. It's there ahead of need because the maintainer wants conflict
resolution to move to the agent eventually, Steam-Cloud-style: when that happens, an agent deciding
between its own local archive and a conflicting server version will want exactly this comparison,
through the same authenticated call the agent already makes for everything else. Building one
shared `SyncService` method with two thin, appropriately-scoped entry points now means that future
work reuses this endpoint rather than inventing a second one.

## Verification

- `dotnet build --no-incremental` clean for `SaveLocker.Server` and `SaveLocker.Agent` (the
  Windows/WinForms host — the one pre-existing MSB3277 WindowsBase warning, no new ones).
- `web`: `npm run build` (tsc + vite) and `npm run lint` (oxlint) both clean.
- `src/Server/openapi.json` regenerated from a real dev server's `/openapi/v1.json` and committed;
  `web/src/api-types.ts` regenerated from that snapshot (`npm run gen:api`) — diff is exactly the two
  new routes and the new schema, nothing else moved.
- Manual, live: registered two throwaway machines against a dev server, pushed two divergent
  single-file/two-file archives to force a real conflict, confirmed both `/stats` routes (admin- and
  agent-scoped) return the right `fileCount`, then loaded the console in the browser pane and
  confirmed the conflict card rendered `DemoMachineA — current Latest · 1 file · newest change …` and
  `DemoMachineB · 2 files · newest change …`. Demo game deleted afterward; no dev-state residue.
- `tests/run-health-tests.ps1` — added two checks right after the existing "stale conflict is
  escalated in the console contract" assertion (same real-conflict fixture, no new setup needed):
  file count matches the known single-file save, newest-mtime is non-null. 21/21 passing, no
  regressions to the other 19.

## Not done

Not merged. No task file existed for this — it was an ad hoc backlog pick, so there's nothing to
move out of `tasks/`. `Backlog.md`'s conflict Tier 1 item removed; this file is the write-up it
pointed at.
