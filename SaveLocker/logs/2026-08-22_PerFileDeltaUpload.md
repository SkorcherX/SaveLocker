# Task — Upload only changed files, not the whole save folder

**Shipped:** 2026-08-22, commit `f7d2a58`. Decision 1 (storage): copy-forward, as recommended below —
content-addressable storage was rejected because it doesn't improve identical-push detection at all
(pure hash/manifest comparison, unaffected by storage layout) and trades the actual problem (upload
bandwidth) for a different one (disk space, via a GC subsystem nobody asked for). Decision 4 (floor):
5 files or 2 MB total, a starting number. Execution order 1–5 all landed as described, plus one thing
this doc didn't anticipate: the agent's Begin request sends its FULL current manifest (not just the
changed paths) either way, so it doubles as decision 6's "deletions are an explicit declared list"
for free — no separate removed-paths field was needed. New `UploadStatus.RetryFull` handles the one
case the doc's decision 3 didn't spell out in wire terms: a delta negotiated against a head that
moved on before Complete, retried transparently inside `ApiClient` as a full archive. Measured per
the verification plan: an 8-slot 3.2 MB fixture with one file changed sent ~400 KB, not ~3.2 MB.
`tests/run-delta-upload-tests.ps1` (17/17) covers every item in *Verification* below except a
byte-level network capture (the audit trail's "N of M files needed fresh bytes" was used instead,
which is what the diagnostic note under Motivation about the maintainer's own "barely opened the
game" report also turned out to need). Full write-up: `CONTEXT.md` (2026-08-22 entry) and
`Decisions.md`.

**Created:** 2026-08-19

**Target:** `src/Shared/SaveArchive.cs`, `src/Shared/Contracts.cs`, `src/Server/Program.cs`,
`src/Server/Services/SyncService.cs`, `src/Server/Services/ArchiveStore.cs`,
`src/Agent.Core/SyncEngine.cs`, `src/Agent.Core/ApiClient.cs`. API surface changes, so
`src/Server/openapi.json`, `web/src/api-types.ts` and `agent-ui/src/api-types.ts` need regenerating
per the standing convention.

**Goal:** on a normal (non-conflicting, non-forced) push, upload only the files that actually
changed since the server's current head, instead of re-zipping and re-sending the entire save
folder every time. Pull, download, conflict handling and the existing archive-safety hardening must
end up unchanged — a delta-built version has to be byte-identical, once reconstructed, to what
today's full-archive path would have produced.

---

## Motivation (maintainer, 2026-08-19)

Prompted by two specific games: **Cyberpunk 2077** and **Fallout: New Vegas**.

Per Ludusavi's known save layout for both — **confirm against the actual folder before writing any
code, this has not been measured on this maintainer's machine yet**:

- **Cyberpunk 2077** keeps one subfolder per save slot (`sav.dat` + `screenshot.png` +
  `metadata.9.json`), and retains a configurable number of autosaves/quicksaves alongside manual
  saves — often a dozen-plus slots.
- **Fallout: New Vegas** keeps one `.fos` per slot (quicksave, autosave, manual 1–N). Heavily-modded
  FNV is notorious for save bloat from script-variable leaks; individual `.fos` files can reach the
  tens of MB.

In both cases a normal play session changes **one or two slots**, but every push today re-zips and
re-uploads *every* slot, changed or not (`SaveArchive.CreateArchive`,
[src/Shared/SaveArchive.cs:63](../../src/Shared/SaveArchive.cs)). For a folder with 20 slots at a
few MB each, that is tens to 100+ MB sent for a session that changed a few MB — and the project
already cares about upload cost on this exact path: the parked `tasks/OfflineBackoff.md` (see
Backlog) exists because a Deck away from home pays in battery and metered traffic for needless
network activity on the sync path. This is the same cost center, on the byte-count side rather than
the request-count side.

---

## Background — what happens today

- **Change detection is a single aggregate hash**, not a per-file one.
  `SaveArchive.HashDirectory` ([SaveArchive.cs:23](../../src/Shared/SaveArchive.cs)) walks the save
  folder in a stable order (exclude globs applied, symlinks skipped) and folds every file's relative
  path and bytes into one SHA-256. Nothing about which *individual* files changed is ever computed
  or stored — only "did the whole tree change since I last hashed it."
- **A push either sends nothing or sends everything.**
  `SyncEngine.PushCoreAsync` ([SyncEngine.cs:208](../../src/Agent.Core/SyncEngine.cs)) skips the
  network entirely if the aggregate hash matches `LastSyncedHash`. Otherwise
  `SaveArchive.CreateArchive` zips **every** matched file — changed or not — and the agent uploads
  that complete zip as the request body (`ApiClient.UploadAsync`,
  [ApiClient.cs:190](../../src/Agent.Core/ApiClient.cs)).
- **The server never opens the zip it receives.** `ArchiveStore.SaveAsync`
  ([ArchiveStore.cs:73](../../src/Server/Services/ArchiveStore.cs)) streams the uploaded bytes
  straight to `.incoming/*.part` and publishes with a rename. It never parses zip structure, so
  today's upload path carries none of the zip-bomb / zip-slip hardening that `RestoreArchive`
  (pull direction) has — it doesn't need to, because it's a dumb byte copy.
- **The content hash is client-asserted, not server-verified.** The agent computes the aggregate
  hash and sends it as a query parameter; `SyncService.UploadAsync`
  ([SyncService.cs:498](../../src/Server/Services/SyncService.cs)) stores it as
  `SaveVersion.ContentHash` without recomputing it from the uploaded bytes. Fine for today's model
  (whole file, one trust boundary); matters more once the server has to reason about *which* files
  a partial upload claims to change.
- **Storage is one complete zip per version**, addressed by version id:
  `{ArchiveRoot}/{gameId}/{versionId}.zip` (`ArchiveStore.RelativePath`,
  [ArchiveStore.cs:53](../../src/Server/Services/ArchiveStore.cs)). Download
  (`SyncService.DownloadVersionAsync`) streams that one file verbatim. Retention
  (`PruneVersionsAsync` → `ReleaseArchivesAsync`) deletes the whole file for a pruned version.
  Nothing today assumes a version's archive might share bytes with another version's archive.
- **Fast-forward vs. conflict is decided by `parentVersionId`.**
  `SyncService.IngestAsync` ([SyncService.cs:540](../../src/Server/Services/SyncService.cs)) only
  takes the simple path — advance the head — when the uploading machine's declared parent matches
  the server's actual current head. Anything else (a stale parent, `force`, a brand-new machine with
  no parent) is a conflict or an explicit overwrite. **This is the fork point for scope**, below.

---

## Decisions to make first

These are choices, not implementation details. Settle them before writing code and record the
outcome in `Decisions.md`.

1. **How the server reconstructs a version from a partial upload.** Two real options:
   - **(a) Copy-forward into a new complete zip** (recommended starting point): on a clean
     fast-forward push, the server opens the previous head's zip, copies across every entry the
     agent did *not* flag as changed/removed, adds the newly uploaded entries, and writes that out
     as the new version's `.zip` — same on-disk layout as today, one file per version, nothing about
     `RestoreArchive`, download, or pruning changes at all. Costs server CPU/disk I/O proportional to
     the *whole* save on every push (it still touches every unchanged entry to copy it forward), but
     that cost is cheap and elastic (a Docker container on unRAID) compared to what's actually scarce
     in the motivating case — upload bandwidth and battery on a Deck.
   - **(b) Content-addressable blob store**, git/restic-style: store each unique file once by its own
     hash, and a version becomes a manifest of `path → blob hash`. Genuinely better — dedups
     identical files across slots *and* across versions, so 19 unchanged Cyberpunk slots cost zero
     extra storage forever, not just zero extra network this one time — but it replaces `ArchiveStore`'s
     core abstraction, requires reference-counted garbage collection so pruning a version doesn't
     delete a blob another surviving version still points at, and means `RestoreArchive`'s download
     path has to materialize a zip on demand instead of streaming one that already exists on disk.
   <br>**Recommendation: start with (a).** It solves the stated problem (network bytes on a
   constrained uplink) without touching the already-hardened restore/download contract or adding a
   GC subsystem. (b) is a legitimate future step if storage cost (not network cost) ever becomes the
   complaint — note it in `Decisions.md` as deferred, not rejected.

2. **The manifest must be diffed against the server's actual current head, not the agent's memory of
   its own last push.** If agent A's last sync doesn't match what's currently on the server (machine
   B pushed since), diffing against A's stale local state can miss files the server's real head needs
   preserved. So the delta must be computed against a manifest the server hands back for its *current*
   head — meaning a manifest round trip (agent's local manifest → server replies "these paths are new
   to me, upload them; these you can drop, I don't have them anymore") has to happen before the
   file upload, not be skipped as an optimization.
3. **Only take the delta path on a clean fast-forward.** `force` pushes, a diverged/conflicting push,
   and a machine's first-ever push (no known parent) all keep today's full-archive behavior
   unconditionally — those are exactly the cases where the server cannot safely assume what "the old
   content" was to copy forward from. This bounds the change to one branch of `IngestAsync`
   ([SyncService.cs:638](../../src/Server/Services/SyncService.cs)); the conflict/policy logic is
   untouched.
4. **A size/file-count floor below which delta is skipped entirely.** Most tracked games have a
   handful of small files; for those the manifest round trip is pure overhead with no payoff. Pick a
   threshold (file count, or total bytes) under which `PushCoreAsync` just does what it does today.
   Keeps the common case exactly as fast as it is now.
5. **Apply the existing zip-safety hardening to the reconstruction, not just today's whole-file cap.**
   `ExtractChecked` ([SaveArchive.cs:238](../../src/Shared/SaveArchive.cs)) bounds entry count and
   uncompressed size because an archive is treated as hostile input once the server actually opens
   it (Decisions.md §4 threat model — a compromised machine API key is exactly this kind of partial
   trust). Today's upload path avoids that whole class of problem by never parsing the zip it
   receives. Once the server opens zips to copy entries forward, the same entry-count / size caps
   need to apply to the *delta payload* and to the *reconstructed result*, not only to the top-level
   upload size like `ArchiveStore.SaveAsync`'s `maxBytes` does now.
6. **Deletions are an explicit list, not an inference.** A slot the agent no longer has locally (a
   deleted save) must be named in the manifest exchange as removed, not just absent from "changed" —
   the delta protocol should mirror `RestoreArchive`'s own "archive is complete truth" model
   (files present locally but absent from what's declared are the ones that get dropped), so a race
   or a bug in the diff can't silently resurrect a file nobody asked to keep.

---

## Execution order

1. **Per-file manifest primitive** in `SaveArchive.cs`: a `ComputeManifest(sourceDir, excludeGlobs)`
   built from the *same* `EnumerateRelativeFiles` call `HashDirectory` and `CreateArchive` already
   use, so the manifest, the aggregate hash, and the archive can never disagree about which files
   exist. Returns `(relativePath, sha256, size)` per file.
2. **Wire contract**: manifest DTOs in `Contracts.cs`, and a new agent-authenticated endpoint (or an
   addition to the existing upload flow) that lets the agent hand the server its manifest for a game
   and get back which paths the server actually needs bytes for — new/changed relative to its real
   current head — plus which of the agent's declared-removed paths it should drop.
3. **Server-side reconstruction**: extend the fast-forward branch of `IngestAsync` to build the new
   version's zip from (previous head's entries not in the changed/removed set) + (newly uploaded
   entries), through the same size/entry-count guard `ExtractChecked` uses. Every other branch
   (`diverged`, `force`, no-parent) is untouched and keeps receiving a full archive exactly as today.
4. **Agent side**: `PushCoreAsync` gains the size/count-floor check (decision 4); above it, compute
   the local manifest, exchange it, zip up only the files the server asked for, upload that (much
   smaller) zip plus the removed-paths list. Below the floor, or on `force`/conflict/first-sync, fall
   straight through to today's full-archive path — that code path does not change.
5. **Regenerate `openapi.json` and both `api-types.ts` files** per the standing convention once the
   endpoint shape is settled.

## Verification

1. **Multi-slot fixture, one slot changed**: assert the request body for the delta upload contains
   only the changed file's bytes (not the whole folder), and that the resulting server-side archive,
   once downloaded and restored on a second machine, is byte-for-byte identical to what a full-archive
   push of the same end state would have produced. This is the core regression guard — nothing about
   pull/restore should be able to tell a version apart based on how it was built.
2. **Deletion**: a slot removed locally is absent from the reconstructed version, not resurrected
   from the previous head's zip.
3. **Force / first-sync / conflict-divergent pushes still send a full archive** — assert the delta
   path is provably *not* taken, not just that the end result happens to be correct.
4. **Below the size/count floor, no manifest round trip happens at all** — assert request count, so a
   small tracked game's push stays exactly as cheap as it is today.
5. **A hostile delta payload** (a changed-file entry that zip-slips, or a reconstruction that would
   exceed the entry/size caps) is refused the same way `ExtractChecked` refuses one today — test
   against deliberately broken input, per the project's own rule for security-relevant code
   (`run-hardening-tests.ps1`'s convention: it must FAIL against pre-fix code).
6. **Measure the thing that motivated this.** Build a fixture shaped like the Cyberpunk/FNV case
   (N slots, 1 changed) and count bytes actually sent over the wire before and after, for both the
   changed-one-slot case and the below-floor small-save case. State the numbers in the commit
   message — the goal was fewer bytes on a constrained uplink, not a nicer diagram of one.

## Done when

- A clean fast-forward push of a multi-file save with one changed file sends only that file's bytes,
  measured and quoted, not estimated.
- Pull, download, restore, conflict resolution, and `force` pushes are all provably unchanged —
  covered by tests that would fail if any of them started depending on how a version was built.
- The reconstruction path carries the same zip-bomb/zip-slip/entry-count hardening the download path
  already has, tested against deliberately hostile input.
- Small tracked games (below the size/count floor) still push in one request, exactly as today.
- The storage-model choice (copy-forward vs. content-addressable) and the size/count floor are
  recorded in `Decisions.md`.
- `openapi.json`, `web/src/api-types.ts` and `agent-ui/src/api-types.ts` are regenerated and
  committed.

## Explicitly out of scope

- **Byte-level diffing inside a single file** (rsync rolling-checksum, bsdiff). This task detects
  whole-file adds/changes only. That's the right granularity here because both motivating games
  write each save slot as its own file that gets fully rewritten per save, not one file that grows
  in place — confirm that assumption against the real save layout before building (decision 0, in
  Motivation), and revisit scope if it turns out wrong.
- **Content-addressable storage / cross-version dedup** (decision 1b). Deferred, not rejected — a
  legitimate follow-up if storage cost rather than network cost becomes the complaint.
- **Any change to the pull/download wire format.** An agent pulling a save still receives one
  complete zip, exactly as it does today.
