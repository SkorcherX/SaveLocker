Running log of session outcomes, newest entries appended at the bottom.

---

## 2026-08-24/25 — File-count / newest-mtime delta in conflict UI (conflict-version-stats, PR #15)

**Branch:** `conflict-version-stats`. PR: https://github.com/Marwanello/SaveLocker/pull/15.

### Request sequence

1. Asked whether the backlog item "File-count / newest-mtime delta in conflict UI" (the last piece of
   an otherwise-complete conflict Tier 1) was hard to implement — exploratory question, no
   implementation yet.
2. Asked to implement it, with an explicit forward-looking constraint: design it so it doesn't paint
   the codebase into a corner for a future migration where conflict resolution moves from manual
   dashboard clicks to agent-side automatic decisions, the way Steam Cloud auto-resolves save
   conflicts.
3. Asked to create a new branch using kebab-case naming and commit the changes, then open a PR.
4. Requested an xhigh-effort code review of PR #15, then asked for the 7 surviving findings to be
   applied with minimal edits.

### What was found

Reading `SaveArchive.CreateArchive` (`src/Shared/SaveArchive.cs`) showed it has always stamped
`entry.LastWriteTime = File.GetLastWriteTime(full)` on every zip entry, long before this task existed.
That meant the "upload time vs. on-demand" tradeoff the backlog note posed wasn't really a choice:
deriving file count and newest-mtime from the archive on demand costs nothing extra, needs no DB
migration or wire-field addition, and works retroactively on every archive ever uploaded.

### What was built

- `SaveArchive.GetArchiveStats(zipPath)` (Shared) — opens the zip, counts real file entries (skipping
  directory entries), tracks the max `LastWriteTime`. Returns
  `readonly record struct ArchiveStats(int FileCount, DateTime? NewestFileWriteUtc)`.
- `VersionStatsDto(int FileCount, DateTime? NewestFileWriteUtc)` (`src/Shared/Contracts.cs`) — the wire
  shape, kept separate from `SaveVersionDto` so listing versions never has to open a zip for versions
  nobody is looking at.
- `SyncService.GetVersionStatsAsync(Guid? gameId, Guid versionId)` — a nullable-`gameId` overload used
  by both routes: `null` for the agent group's flat route (matching `DownloadVersionAsync`'s own
  precedent — a valid machine key already gates the group), and a real game id for the admin group's
  nested route, which checks the version actually belongs to that game before answering.
- Two new endpoints backed by the same service call: `GET /api/versions/{versionId}/stats` (agent
  group) and `GET /api/games/{id}/versions/{versionId}/stats` (admin group).
- Console (`GameDetail.tsx`): conflict card fetches stats lazily for only the two versions an open
  conflict is showing, cached in a `useRef` `Set` keyed by version id (archive contents never change
  once uploaded, so no cache invalidation is needed). Renders `"N files · newest change <time>"` under
  the existing machine/time/size line; spacing kept stable by moving `marginTop` onto the always-
  rendered button row rather than the conditionally-rendered stats line, so the card doesn't jump once
  the fetch resolves.

**Agent-migration angle:** the agent-scoped route (`/api/versions/{versionId}/stats`) has no UI caller
today — it exists ahead of need. When conflict resolution moves to the agent (Steam-Cloud-style), an
agent deciding between its own local archive and a conflicting server version will want exactly this
comparison, through the same authenticated call the agent already makes for everything else. One
shared `SyncService` method with two thin, appropriately-scoped entry points means future work reuses
this endpoint rather than inventing a second one.

### Code review, xhigh effort — 7 findings, all fixed

An xhigh-effort review of PR #15 (10 finder angles, 2 of which hit a subagent weekly rate limit
mid-run and were covered manually instead, including writing a standalone .NET test to confirm the
most severe finding) surfaced 7 findings, all applied with minimal edits:

1. **Timezone skew in the newest-mtime value (confirmed empirically).** `entry.LastWriteTime.UtcDateTime`
   reconstructs the DOS zip timestamp using the *reading* process's local timezone, not the *writing*
   agent's — a standalone test proved an 8-hour skew when the writer and reader ran in different
   zones. Since the two sides of a real conflict usually come from different physical machines, this
   could flip which save looked "newer." Fixed by treating the stored wall-clock value as UTC directly
   (`DateTime.SpecifyKind(..., DateTimeKind.Utc)`) instead of reinterpreting it through whichever
   timezone the server happens to be deployed in — deterministic now, though still bounded by
   whatever offset the agent's own clock had at upload time (the DOS format can't carry more than
   that).
2. **A failed stats fetch permanently hid a version's stats.** `GameDetail.tsx` marked a version id as
   "requested" before the fetch resolved and never un-marked it on failure. Fixed: the `.catch` now
   clears the id so the next 15s poll retries.
3. **The agent-scoped stats route had no test coverage** — only the admin-scoped route was exercised.
   Added a check that authenticates with a machine's own `ApiKey` (read from its config file) and
   hits `/api/versions/{id}/stats` directly.
4. **No server-side caching**, despite the PR's own write-up asserting archive stats never change once
   uploaded. Added a `ConcurrentDictionary<Guid, VersionStatsDto>` cache on `SyncService`, keyed by
   version id, cleared only by a restart (fine — a pruned version's id is never reused).
5. **Two competing idioms for ownership-scoping** existed side by side (a route-level check for
   download, a service-level check for stats), and the stats overload's doc comment inaccurately
   claimed to match the download route's precedent. Resolved by dropping the misleading claim; the
   nullable-`gameId` overload itself was not restructured.
6. **An unnecessary single-arg forwarding overload** (`GetVersionStatsAsync(Guid) => GetVersionStatsAsync(null, ...)`)
   existed only so the agent route didn't have to pass `null` explicitly. Removed; the agent route now
   calls the two-arg overload directly.
7. **A comment restated what the code does rather than why**, per this repo's CLAUDE.md rule "No
   comments unless the WHY is non-obvious." Rewritten to explain the actual non-obvious reason the
   admin route is separate from the agent one (different auth scheme).

### Verification

- `dotnet build --no-incremental` clean for `SaveLocker.Server` and `SaveLocker.Agent` (the
  Windows/WinForms host — the one pre-existing MSB3277 WindowsBase warning, no new ones).
- `web`: `npm run build` (tsc + vite) and `npm run lint` (oxlint) both clean, both before and after
  the review fixes.
- `src/Server/openapi.json` regenerated from a real dev server's `/openapi/v1.json` and committed;
  `web/src/api-types.ts` regenerated from that snapshot (`npm run gen:api`) — diff is exactly the two
  new routes and the new schema, nothing else moved.
- Manual, live: registered two throwaway machines against a dev server, pushed two divergent
  single-file/two-file archives to force a real conflict, confirmed both `/stats` routes (admin- and
  agent-scoped) return the right `fileCount`, then loaded the console in the browser pane and
  confirmed the conflict card rendered `DemoMachineA — current Latest · 1 file · newest change …` and
  `DemoMachineB · 2 files · newest change …`. Demo game deleted afterward; no dev-state residue.
- `tests/run-health-tests.ps1` — three checks total for this feature (file count, newest-mtime, and
  the added agent-scoped-route check), all passing. 22/22 passing overall, no regressions.

### Not done

PR #15 open against `main`, mergeable after resolving a docs-only merge conflict on this file,
`CONTEXT.md`, and `session_summary.md` against an unrelated PR (#14) that landed on `main` in the
meantime. No task file existed for this — it was an ad hoc backlog pick. Full technical write-up:
`logs/2026-08-24_conflict-version-stats.md`.

---

## 2026-08-26 — PR #12 review, fixes, and PR #14

[PR #14](https://github.com/Marwanello/SaveLocker/pull/14) is open and mergeable.

**Rename:** `decky-api-contract-and-steam-cloud-fallback` -> **`steam-cloud-fallback`** (43 -> 20
chars), still kebab-case and in line with repo names like `wine-proton-case-insensitive-paths` and
`per-file-delta-upload` — a noun phrase naming the headline change.

**PR:** [Marwanello/SaveLocker#14](https://github.com/Marwanello/SaveLocker/pull/14) —
`steam-cloud-fallback` -> `main`, 8 files, +135/-76, MERGEABLE.

Three things worth knowing about how this was done:

- **Targeted the fork, not upstream.** `gh`'s default repo here resolves to `SkorcherX/SaveLocker`,
  so a bare `gh pr create` would have opened a PR on the upstream maintainer's repo. `--repo
  Marwanello/SaveLocker` was passed explicitly. That is also the only correct base: the parent commit
  `01f1c56` is a fork-only merge, and it was verified to be an ancestor of `origin/main` before
  creating the PR. Upstream has a *different* PR #12, so a PR there would have diverged.
- **Amended rather than adding a commit.** The `CONTEXT.md` entry named the old branch, so that one
  line was fixed. Standard guidance prefers a new commit over amending, but nothing was pushed yet
  and a separate commit to correct a stale string would have been noise. The commit is now
  `1ae1538`; nothing else was rewritten.
- **Local `main` is 1 commit behind `origin/main`** — pre-existing, unrelated to this work.
  Fast-forward it when convenient: `git -C "D:\Projects\SaveLocker\SaveLocker" pull --ff-only`

Still outstanding: **no test suite has been run** against these changes, and the Decky plugin repo
needs updating to read `hasSteamCloud: null` as "unknown, use your own heuristic" — the agent can now
legitimately send it. Both are called out in the PR body.

The empty worktree directory at `.claude\worktrees\review-pr-12-b073b2` could not be deleted from
that session (the shell's cwd sat in it); `rmdir /s /q` clears it once the session ends.
