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

Code review of merged fork PRs #1-10, fifteen findings applied, shipped as fork PR #16.

- **Branch:** `chunked-upload-integrity-and-review-fixes`
- **Commits:** `9d08e65` — *Make a chunked upload all-or-nothing, and the rest of the PR #1-10 review*
  · `6d7c7f3` — *Docs: record the PR #1-10 review and what its fixes cover*
- **PR:** https://github.com/Marwanello/SaveLocker/pull/16 (-> `main`, 16 files, +570/-55, MERGEABLE)
- **Base:** `e83da90` — *Fix: null-safe HasSteamCloud and keep /api/games' field names (#14)*

---

## The headline bug: a chunked upload could publish a truncated save

Introduced by PR #3, which added the chunked upload protocol so large saves survive Cloudflare's
fixed ~100s proxied-edge timeout: `begin` -> N x `chunk?offset=` -> `complete`.

The failure chain:

1. A chunk faults part-way through its body. The bytes already written stay on disk, **and are
   counted** — `BytesReceived` had advanced as the write progressed.
2. The client retries that chunk at its original offset. The server compares that offset against the
   inflated `BytesReceived`, sees it as *behind*, and treats it as a harmless duplicate replay.
3. The remainder of that chunk is therefore never written. The gap is silent.
4. If it was the final chunk, `complete` publishes the short file **under the content hash of the
   complete archive**. Every other machine then pulls it believing it is intact.

Two properties of this codebase shaped the fix, and both are worth remembering:

- **`ContentHash` hashes the save *folder*, not the zip** — it comes from
  `SaveArchive.HashDirectory`. The obvious fix ("re-hash the assembled file and compare against
  `ContentHash`") is therefore impossible without the server extracting the archive first.
- **A zip's central directory lives at the *end* of the file.** That is what makes "did every byte
  arrive" answerable server-side without re-hashing anything: a short assembly cannot produce a
  readable archive.

So `CompleteSession` now opens the staged file as a zip and reads its entry count before publishing.
Failure deletes the staging file and throws `CorruptUploadException`, which `Program.cs` maps to
**422** — deliberately not the 500 an unhandled throw would give, because the agent has to be able to
distinguish *"those bytes did not arrive intact, resend"* from *"the server is broken"*.

And `AppendChunkAsync` is now all-or-nothing: any fault mid-chunk rolls the file back to the offset
that chunk started at, and `BytesReceived` advances **only after the write is durable** — the count
is what a retry is judged against, so it must never describe bytes still in flight. Oversize is the
one case that aborts the whole session instead of rolling back, since retrying cannot help.

## Proving the tests catch it

30 new `CS-12` checks in `tests/run-server-bugbounty-tests.ps1`: byte-identical happy path,
`begin` no-change short circuit, whole-chunk replay, gap-ahead 409, cross-machine and unknown-session
404, truncation 422, a **mid-chunk fault driven through a raw `TcpClient`** that declares a
`Content-Length` it never delivers and then disconnects, and cumulative over-cap.

The suite was then run against a **reverted** `ArchiveStore.cs` + `Program.cs`: **8 checks fail**,
including *"the truncated session published no version"*. That is the part that matters — it shows
the pre-fix server genuinely did publish corrupt archives, so the suite is a regression test rather
than a tautology. Restored: **30/30**.

## The other fourteen findings

Chunk-retry logic in `ApiClient.cs` only caught `HttpRequestException`, but **`HttpClient.Timeout`
surfaces as `TaskCanceledException`/`OperationCanceledException`** — and `ServerHttp.Create` sets no
explicit timeout, so the 100s default applied. Retry now covers transport faults and 5xx/408, with a
real cancellation still passing straight through.

`SyncActivity.Persist` read shared state outside the lock and could write snapshots out of order;
it now snapshots under the lock and serialises writes behind a sequence number, with a timer to flush
throttled updates rather than dropping them. `POST /api/sync` gained a single-flight gate. The Linux
UI's sync call had the default 100s timeout on an operation that can legitimately exceed it.
`PathResolver.SafeChildDirectories` was re-enumerating directories per lookup and is now memoised.
Plus small UI fixes, two `testenv` safety guards (refuse to operate on a state dir that isn't
obviously a test dir), and repointing three `SkorcherX/SaveLocker` URLs to the fork.

## Gotchas hit

- **PowerShell 5.1 unrolls a `byte[]` returned from a function** into the pipeline, so it arrives as
  `Object[]` and `Invoke-WebRequest -Body` serialises it as a *string* — a 125-byte slice went out as
  a 377-byte body. Fix is the load-bearing leading comma: `return ,$s`.
- **Test fixtures must be incompressible.** The first fixture zip was ~376 bytes because repetitive
  text compresses away, which is far too small to exercise chunk boundaries. Now 300KB of seeded
  random bytes.
- **Kestrel's per-request `MaxRequestBodySize` and `ArchiveStore`'s cumulative `maxBytes` are
  different limits with different errors.** A single 3MB body against `MaxUploadMb=2` gets a 413 from
  Kestrel and never reaches `ArchiveTooLargeException`. Exercising the archive cap needs *two* chunks
  that are each under the request cap but together over the archive cap.
- **`PathResolver`'s case-insensitive fallback cannot be tested on Windows** — NTFS resolves the
  mis-cased path directly, so `Directory.Exists(exact)` succeeds and the fallback never runs. Those
  two "failures" are by design; verification moved to WSL.
- **`wsl -d Ubuntu -- bash -lc '...$VAR...'`** has `$VAR` eaten by the *outer* Git Bash shell before
  WSL ever sees it. Use literal paths.
- Line-ending drift again: a Python write left `ActivityCard.tsx` LF in a CRLF tree. Confirmed fixed
  by the diff shrinking to +1/-3 (whole-file churn would have shown as a full rewrite).

## Not done, deliberately

- **Retry on `/upload/{sessionId}/complete`.** The endpoint is not idempotent server-side — a second
  call hits `TryRemove` and 404s — so adding retry today would turn a lost-response *success* into a
  spurious hard failure. Needs the server to remember completed sessions first.
- **Repointing the Decky plugin URL** (`web/src/help/decky-plugin.md:28`,
  `src/Agent.Linux/DeckyPlugin.cs:66`). `Marwanello/SaveLocker-Decky` exists but has **no releases**,
  so the documented *Install Plugin from URL* flow would 404. The stale-looking URL is the working
  one; change it when the fork's plugin repo cuts a release.

Both are called out in the PR body.

## Note on the rebase

`origin/main` advanced mid-session (PR #14), touching `AgentApiServer.cs` and `testenv.ps1` — two
files this work also changed. Rebased rather than opening against a stale base. It applied without
conflict, but a clean auto-merge can still be semantically wrong, so it was checked rather than
trusted: both changesets confirmed present by inspection, PR #14's `HasSteamCloud` still appearing
10x in `AgentApiServer.cs`, the diff vs `origin/main` unchanged at 15 code files / +526/-55, all five
C# projects and `agent-ui` rebuilt, and `CS-12` re-run at 30/30.
