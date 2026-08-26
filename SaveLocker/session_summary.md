Running log of session summaries, newest entries appended at the bottom.

---

## Session Summary — 2026-08-24/25 (conflict-version-stats, PR #15)

### What was asked

1. **Difficulty check** on a backlog item: "File-count / newest-mtime delta in conflict UI" — the last
   remaining piece of an otherwise-complete conflict Tier 1. Answered without implementing.
2. **Implement it**, with a stated future constraint: the maintainer wants conflict management to
   eventually migrate to the agent, auto-resolving conflicts the way Steam Cloud does, so the
   implementation shouldn't box that future in.
3. **Create a kebab-case branch, commit, and open a PR.**
4. **xhigh-effort code review of the PR**, then apply the surviving findings with minimal edits.

### What shipped

A conflict card in the console currently shows size and upload time per side, but nothing indicating
which machine actually has more progress — a smaller, newer save can still be the one worth keeping.
This session added a derived file-count and newest-file-mtime stat to each side of a conflict.

Key finding that simplified the work: `SaveArchive.CreateArchive` has always written each zip entry's
`LastWriteTime`, so the stat didn't need a new upload-time computation or DB migration — it could be
derived from any already-uploaded archive on demand, retroactively, for free.

**New capability:** `SaveArchive.GetArchiveStats()` (Shared) → `VersionStatsDto` (Contracts) →
`SyncService.GetVersionStatsAsync` (a nullable-`gameId` overload used by both an agent-unscoped and an
admin-scoped route) → two new endpoints (`/api/versions/{id}/stats` agent-group,
`/api/games/{id}/versions/{id}/stats` admin-group) → console fetches lazily per open conflict, cached
by version id (immutable once uploaded), rendered as `"N files · newest change <time>"` on the
conflict card.

**Forward-looking design:** the agent-scoped stats route has no UI caller yet — it's there so a future
agent-side auto-resolver can reuse the identical authenticated call path instead of requiring new
server work when that migration happens.

### Code review and fixes

An xhigh-effort review (10 finder angles; 2 hit a subagent rate limit mid-run and were covered
manually, including a standalone .NET test that empirically confirmed the top finding) surfaced 7
findings, all fixed:

- The zip's `entry.LastWriteTime.UtcDateTime` silently used the *reading* process's timezone instead
  of the *writing* agent's — a real bug that could flip which conflict side looked newer. Fixed to be
  deterministic regardless of server timezone.
- A failed stats fetch permanently suppressed itself with no retry — fixed.
- The agent-scoped route had no test coverage — added.
- No server-side caching despite the archive-stats-never-change invariant the PR itself asserted —
  added an in-memory cache.
- Two competing ownership-scoping idioms and a misleading doc comment — the inaccurate claim removed.
- An unnecessary forwarding overload — removed.
- A comment that stated what instead of why, per this repo's CLAUDE.md — rewritten.

### Verification performed

- Clean `dotnet build --no-incremental` (Server + Agent), clean `npm run build` / `npm run lint` (web)
  — both before and after the review fixes.
- `openapi.json` regenerated from a live server and committed; `api-types.ts` regenerated from it —
  diff limited to the two new routes and one new schema.
- Live manual test: two throwaway machines, a forced real conflict, both stats routes confirmed
  correct, conflict card visually confirmed in-browser. Demo data cleaned up afterward.
- `tests/run-health-tests.ps1`: 22/22 passing (3 checks for this feature, including the new
  agent-scoped-route check), zero regressions.

### Outcome

- Branch `conflict-version-stats`, PR https://github.com/Marwanello/SaveLocker/pull/15.
- Backlog's conflict Tier 1 entry for this item removed; full technical write-up at
  `SaveLocker/logs/2026-08-24_conflict-version-stats.md`.
- `SaveLocker/CONTEXT.md` updated with a session narrative paragraph per the vault's session-handoff
  convention.

### Open follow-ups (not started)

- Actual agent-side auto-resolution logic (the Steam-Cloud-style migration itself) — this session only
  laid an endpoint the future work can reuse; no agent decision logic was written.

---

## Session Summary — 2026-08-26

Code review of fork PR #12, five findings applied, shipped as fork PR #14.

- **Branch:** `steam-cloud-fallback`
- **Commit:** `1ae1538` — *Fix: null-safe HasSteamCloud and keep /api/games' field names*
- **PR:** https://github.com/Marwanello/SaveLocker/pull/14 (`steam-cloud-fallback` -> `main`, 8 files,
  +135/-76, MERGEABLE)
- **Reviewed:** `01f1c56` — *Agent: per-game alias, HasSteamCloud and pull-before-launch for the Decky
  plugin, plus testenv plugin builds (#12)*

---

### Which PR #12

There are two, and they are different changes. `origin` is the fork
(`Marwanello/SaveLocker`); `upstream` is `SkorcherX/SaveLocker`. **The `gh` CLI's default repo here
resolves to `SkorcherX/SaveLocker`** — the upstream maintainer's repo — so every `gh` call in this
session passed `--repo Marwanello/SaveLocker` explicitly. The reviewed target was the *fork's* PR #12,
confirmed by its merge commit `01f1c56` matching local `HEAD`. A bare `gh pr create` would have opened
a pull request against a third party's repository.

### What changed

**`HasSteamCloud` became `bool?`** (`AgentApiServer.cs`, `AgentConfig.cs`). This is the substantive
change and the reason for the commit title. `bool` collapsed *"this game has no Steam Cloud"* and
*"nobody has determined whether this game has Steam Cloud"* into the same `false`. Two call sites
never assign the field at all, so `false` was being published as a determination that had never been
made. `null` now means unknown.

The two non-assigning sites were left non-assigning, with a comment each explaining why:

- `CommandPoller.cs:185` — `GameDto` (`src/Shared/Contracts.cs:20`) carries no Steam Cloud signal at
  all, so there is nothing to assign. `null` is the honest value.
- `AgentCli.cs:237` — **assigning here would be a regression.** `var tracked = existing ?? new
  TrackedGame();` reuses an existing entry, so writing the field on re-add would erase whatever
  enrollment had determined.

**The `/api/games` wire contract was reverted to its published field names.** The PR had renamed
`TrackedGameDto.Id` -> `GameId` and `Path` -> `SaveDirectory` to match the config-side property names.
That record *is* the wire contract, and the Decky plugin that reads it ships from its own repo
(`SkorcherX/SaveLocker-Decky`) on its own update channel — an agent update would have broken every
plugin in the field. The names are back; the new fields stay, purely additive. A `<remarks>` block on
the record now records why the two sides are allowed to disagree.

Objective check that the contract is additive-only:
`git diff 01f1c56^ -- agent-ui/src/api-types.ts` shows **0 deleted lines**.

**`AgentConfig.SaveGameAlias` / `SaveGamePullBeforeLaunch`** were two near-identical
read-modify-write-under-lock bodies; both now call one `MutateGameUnderLock(Guid, Action<TrackedGame>)`
helper. One subtlety is load-bearing and commented in place: the `catch` fallback can leave `onDisk`
aliased to `this`, so the in-memory copy is re-applied only under `!ReferenceEquals(mine, target)` —
an unconditional second `apply` would double-apply in that path.

**`tests/testenv.ps1`** matched Decky plugin source with CRLF-only needles. PowerShell's `.Contains`
and `.Replace` are literal, so the patches silently no-op'd against LF checkouts — the harness would
report success having modified nothing. Inputs and needles are now normalized to LF. Verified both
ways: the old needles return `False` against LF input; the new code applies all four substitutions
against **both** LF and CRLF.

**`agent-ui/src/api-types.ts` was regenerated, never hand-edited** (per `REPO_MAP.md`; CI runs
`gen:api -- --check`, so drift fails the build).

### Gotchas hit

- **`sed -i` silently strips CRLF** from files that need it (`SettingsView.tsx`). Worse, `awk '/\r$/'`
  gives a false negative because Git Bash's awk strips CR on read — **`file -b` is the reliable
  check.**
- **Fresh worktrees need `npm install` in `agent-ui/`** before the Windows Agent build, or MSBuild
  fails with `MSB3073: The command "npm run build" exited with code 1`. Already documented in
  `CONTEXT.md`; hit anyway.
- **The Linux agent's DLL is `savelocker.dll`**, not `SaveLocker.Agent.Linux.dll` — the csproj sets
  `<AssemblyName>savelocker</AssemblyName>`. Daemon smoke test:
  `dotnet src/Agent.Linux/bin/Debug/net10.0/savelocker.dll daemon --port 5190 --config <dir>/config.json`
- **`git worktree remove` can fail with "Permission denied" (exit 255) yet partially succeed** — the
  worktree de-registers and its contents delete, leaving an empty directory that the still-running
  session's shell cwd holds open. `dotnet build-server shutdown` releases the MSBuild/Roslyn locks;
  the empty shell needs a `rmdir` after the session ends.

### Not done

- **No test suite was run.** `run-agent-tests` / `run-linux-tests` should both pass before merge —
  `HasSteamCloud` going nullable is a real behavior change, not a cosmetic one.
- **`SkorcherX/SaveLocker-Decky` needs a matching change** to read `hasSteamCloud: null` as *unknown,
  use your own heuristic*. The agent can now legitimately send it. Separate repo, separate release.

Both are called out in the PR body.
