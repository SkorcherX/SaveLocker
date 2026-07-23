# 2026-07-22/23 — the Octopath conflict storm

The first multi-day real-hardware play session. One game, one machine, one weekend: **75 open
conflicts and 2,659.3 MB of saves on a game configured to retain 5.** Escaping it required `curl`
against the admin API, which is itself a finding.

Plan and item numbering: `Backlog.md` → "🔴 ACTIVE — conflict handling". Traps: `Gotchas.md`.
Fix in flight: `tasks/0.0-agent-stale-parent.md`.

## Timeline

**2026-07-22, afternoon.** A conflict is recorded while the maintainer is playing Octopath Traveler 0
on the Deck. They are not logged into the console, so they never see it. The agent raises its alert;
the alerts appear in the console's upper-right and are **dismissed without being read**. Play
continues for roughly a day.

**2026-07-23.** Noticed via *disk usage*, not via the alert — 80 versions, 2.66 GB. The maintainer had
already flagged the newest save (7/23 11:02) as Latest, yet the console still demanded resolution of
two saves from July 22.

Recovery took four rounds, each revealing the next defect:

1. Bulk-resolved 75 conflicts over the admin API, then re-pointed the head with `set-latest`.
2. Played again → conflicted again. Cause: console resolution never informs the agent.
3. Repaired the agent's parent with a guarded `savelocker pull` → prune finally ran, 2.66 GB → 171 MB.
4. Played again → **still** conflicted on every save. Cause: the daemon's stale in-memory config.

## The four defects

**#1 — Conflicts do not deduplicate.** `SyncService.cs:437` inserts a fresh `ConflictFlag` on every
divergent push. Because the head never advances while conflicted, all 75 rows share the same
`VersionAId` (the July 22 head) and differ only in `VersionBId`. The console renders exactly one of
them — `GameDetail.tsx:40` does `.find()` over a list ordered oldest-first (`SyncService.cs:563`) — so
the user is permanently shown the **least** useful conflict.

The resolution they actually wanted already existed as a row: the newest conflict paired the July 22
head against the 7/23 11:02 save. The UI simply never offered it.

**#2 — Retention is unreachable while conflicted.** `PruneVersionsAsync` is called only on the
fast-forward path (`SyncService.cs:459`); the conflict branch returns at `:451`. And the protected set
(`:481`) pins both versions of every open conflict — with 75 open, nearly every version was pinned.
Two independent reasons the retention setting had not applied since July 22.

**#3 — Resolving a conflict does not un-stick the agent.** `ResolveConflictAsync` writes
`HeadVersionId` and stamps the flag. Nothing notifies the agent, whose parent advances only on a
successful push or a pull (`SyncEngine.cs:165` / `:280`). Already recorded at `CONTEXT.md:332`; this
session is what made the cost concrete. Tracked as item 0.4.

**#4 — The daemon pushes from state the launch wrapper superseded.** The root cause of the loop, and
invisible until 1–3 were worked around. `ProtonRun` and the daemon each own `config.json`; the daemon
loads it once at boot (`Daemon.cs:118`) and never re-reads. After the wrapper pushes on exit, the
daemon's watcher pushes with the old parent → conflict, permanently, since `SyncEngine.cs:178` does
not advance the pointer on conflict.

`AgentConfig.SaveGameSyncState` describes this scenario in its own doc comment and fixed only the
**write** side; `Decisions.md` §8 closed half the door. Worse, `CommandPoller.cs:179` calls the full
`_config.Save()` — explicitly unsafe for per-game sync state per `AgentConfig.cs:96–100` — rewinding
the pointer the wrapper just wrote, so the wrapper's next push conflicts too.

The tell was the alternation: prune *did* run (the wrapper's pushes fast-forward) while every save
still conflicted (the daemon's did not).

## What this says about the test suite

The `CONTEXT.md` lesson from v0.3.0 applies again, verbatim: **when a suite passes, ask what state it
never puts the system in.**

- Nothing pushes **twice** into an open conflict — which is all it would have taken to catch #1 and #2.
- `run-concurrency-tests.ps1` was written specifically for cross-process state and has 12 checks. All
  cover the **write** race. **None covers a long-lived reader that went stale**, which is #4.

Both gaps are closed as part of task 0.0, and each new check must be proven to fail against pre-fix
code before it counts.

## Two process findings worth keeping

**The alert was dismissible, so it was dismissed.** `HealthService.cs:201` treats a conflict
identically to "server unreachable" — but every other agent event self-heals and a conflict does not.
A day of play happened behind a dismissed toast. Item 1.6.

**There was no way to back up a save from the console.** Version download is agent-only
(`Program.cs:295`, `X-Api-Key` group): no admin route, no button. Every recovery step had to be
prefaced with "copy the save folder on the Deck first," because the console could not offer it. Item
1.5 — the escape hatch that makes every other destructive action safe to offer.

## State at end of session

- Conflicts cleared; head correctly on the newest save; storage 2,659.3 MB → 171 MB.
- ✅ **Defect #4 fixed and device-verified the same day** — `logs/2026-07-23_agent-stale-parent.md`.
  The daemon is running again and a 4-save session produced zero conflicts. The stop-the-daemon
  workaround is retired.
- ⏳ Defects #1, #2 and #3 remain open as backlog items 0.1, 0.2 and 0.4. The *loop* is gone, but a
  genuine two-machine conflict would still write a row per push, still stall retention, and still
  surface the oldest and least useful conflict in the console.
- The full remediation plan is 14 items across three tiers in `Backlog.md`.
