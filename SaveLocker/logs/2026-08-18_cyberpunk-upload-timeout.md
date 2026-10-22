# 2026-08-18 — Cyberpunk 2077 uploads always failing: a Cloudflare edge timeout, not an agent bug

Branch `cyberpunk-save-upload-fix`. Not merged, not deployed.

## The question

> "cyberpunk save on pc always fails to upload, either in my local docker setup or remote"

## Reproducing it

Built the throwaway rig (`.\tests\testenv.ps1 build`), enrolled the maintainer's REAL Cyberpunk 2077
save (a genuine 102 MB / 101-file archive — 33 autosaves, `.dat` dominates at 98 MB, `.png`
screenshots only 4 MB, so excluding them was never going to be the fix) against the test console, and
pushed it via CLI. It succeeded first try — 90 MB archive, `Created`. So the sync engine, the archive
format, and hashing were never the bug.

The REAL installed agent's log told a different story: every request type — heartbeat, list-games,
get-commands, not just upload — was failing with `HttpRequestException` / SSL connection reset,
`SocketException 10054`. The upload-specific line read *"server unreachable — queued for retry. (Error
while copying content to a stream.)"*

## The actual cause

`config.json`'s `ServerUrl` is `https://maro.savelocker.dpdns.org`, proxied through Cloudflare
(`Server: cloudflare` on every response). A direct `curl` upload of the real 87 MB archive — bypassing
the .NET agent entirely — reproduced the exact failure: HTTP 524 ("A Timeout Occurred") after 25.5 MB,
at the ~100s mark. **Cloudflare's free/pro edge enforces a fixed ~100s cap on any single
request/response cycle** and it is not configurable. Measured real upload speed to this server: ~200
KB/s — nowhere near enough to get 100+ MB through in 100s.

This is what explains "always," not "sometimes": Minit and Slay the Spire (both KB-sized) finish
before the clock starts noticing; Cyberpunk (100+ MB and growing every autosave) never can, on this
link, through this domain. See `Gotchas.md` → *Hosting / network*.

## The fix

Chunked upload, additive alongside the existing single-shot route:

```
POST /api/games/{id}/upload/begin      -> { sessionId } or { noChange } before a byte moves
PUT  /api/games/{id}/upload/{s}/chunk?offset=N   (repeated, ~4 MiB each, retried individually)
POST /api/games/{id}/upload/{s}/complete          -> UploadResult (same as the old route)
```

Reuses the exact same conflict-aware ingest (`SyncService.IngestAsync`) the single-shot path already
had — `PrepareUploadAsync` is the shared preamble, now called once at Begin (to pre-empt an exact
content match) and once more at Complete (against whatever the head has become by the time the last
byte lands). No single request can now take anywhere near 100s. `ArchiveStore` gained an in-memory
chunked-session table (Begin creates a staged `.part` file + session; a retried chunk is idempotent
against its own offset; Complete does the same atomic rename the old path used).

**The old single-shot route is untouched**, and `ApiClient.UploadAsync` falls back to it on a 404 from
Begin — a server the agent has updated ahead of (the two ship and redeploy separately). Without that,
updating the agent first would turn "sometimes works" into "always 404s" until the server caught up.

## Verification

- Server bug-bounty, agent-integration, and hardening suites: no regressions. (The 4
  `run-server-bugbounty-tests.ps1` CS-01 failures — the pre-fix-DB migration section, which builds an
  old server from a **nested** `git worktree` — reproduce identically on a clean pre-fix checkout in
  this sandboxed session; confirmed unrelated by stashing this change and re-running. Not investigated
  further — looks like an artifact of running inside a worktree already.)
- Chunked upload against a rebuilt test console, real Cyberpunk folder: byte-identical
  (90,464,336 bytes, 101 entries, a valid zip on download) to the original single-shot upload of the
  same folder.
- Fallback: confirmed the real (not-yet-redeployed) server 404s `/upload/begin` today, which is
  exactly the condition the fallback branch checks for.
- `web/src/api-types.ts` and `src/Server/openapi.json` regenerated and committed per the API-change
  rule; `web` typechecks and builds against them.

## Not done

**The real server still needs redeploying** for this to actually fix the maintainer's own Cyberpunk
sync — an agent carrying this fix, talking to today's live server, still falls back to the single-shot
route and hits the identical 524. No cross-process/cross-restart upload resume: a connection dropped
mid-chunk retries that chunk in-process (this is what actually matters — no request is ever large
enough to legitimately time out), but a killed agent process starts the next push from scratch, same
as before this change.
