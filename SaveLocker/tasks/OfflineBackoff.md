# Task — Back off when the server stays unreachable

**Created:** 2026-07-29

**Target:** `src/Agent.Core/OfflineQueueDrainer.cs`, `src/Agent.Core/OfflineQueue.cs`,
`src/Agent.Core/CommandPoller.cs`, `src/Agent.Core/HealthReporter.cs`

**Goal:** stop the agent retrying at a fixed fast interval forever while the server is unreachable.
After a run of consecutive failures it should retry progressively less often, and return to normal
promptly once the server is back.

**Motivation (maintainer, 2026-07-29):** a Steam Deck away from home — travelling, tethered, or on
someone else's network — currently retries on a fixed cadence indefinitely. That costs battery
(radio wake-ups and CPU wake-ups that prevent deep idle) and, on a metered connection, traffic. The
saves themselves are safe; this is about what the device does while it cannot reach the server.

---

## Background — what happens today

Nothing here is broken. It is tuned for a brief blip, and a week of travel is not a brief blip.

The offline queue stores **one entry per game** (deduped by `GameId`) and no save data. On
reconnect it pushes the save folder as it is at that moment, so a long offline stretch produces one
new version, not one per session. That behaviour is deliberate and is **not** in scope here.

**Three separate loops touch the network, and all three keep going while offline:**

| Loop | Interval | What it sends while offline |
|---|---|---|
| `CommandPoller.TickAsync` | **20 s** | game reconcile (`GET /api/games`), command fetch, **and a heartbeat** in its `finally` |
| `OfflineQueueDrainer` | **30 s** | one push attempt per queued game — each one **re-archives the save folder first** |
| `HealthReporter` | piggybacks on the poller | pending events + heartbeat |

So an idle offline Deck with one queued game makes roughly **five failed requests a minute**, and
the drainer's share of that does real work first: `PushAsync` hashes and zips the save directory
before discovering the server is unreachable, then deletes the archive
(`SyncEngine.cs` — the `finally` around the upload). That is the expensive one per attempt, and on
a large save folder it is not close.

**Backing off only the drainer therefore does not meet the stated goal** — it would remove two of
five requests a minute and leave the poller waking the device every 20 s regardless. The work below
covers all three, with the drainer first because it is both the one asked for and the costliest per
attempt.

A useful detail: `OfflineQueue.Entry` already carries `RetryCount` and `LastAttemptAt`, and
`RecordAttempt` already maintains them. Per-entry backoff needs no new persisted state.

---

## Decisions to make first

These are choices, not implementation details. Settle them before writing code and record the
outcome in `Decisions.md`.

1. **The curve.** A starting proposal, deliberately conservative:
   - attempts 1–10: unchanged (30 s drainer / 20 s poller) — a short outage still recovers fast;
   - then double each failure: 1 m, 2 m, 4 m, 8 m, 15 m, 30 m;
   - **cap at 30 m.** The cap is what bounds "how stale can the console's view of this machine be",
     so it is a product decision, not a tuning knob.
2. **Jitter.** Add ±20 % so a fleet that lost the same server does not retry in lockstep. Cheap, and
   it also stops a Deck and a PC on the same LAN waking together.
3. **What resets the backoff.** At minimum a successful request. **This is the part that decides
   whether the feature feels broken**: if the only reset is "a retry eventually succeeded", then
   arriving home means waiting out up to a 30-minute sleep before anything syncs. Options, not
   exclusive:
   - any successful call on any loop resets all three (shared state);
   - a network-availability change (`NetworkChange.NetworkAvailabilityChanged`) resets immediately —
     covers the laptop-lid and Deck-wake case directly;
   - any user-initiated action (tray Sync, `savelocker push`, a Game Mode button) resets and retries
     now, regardless of where the backoff had got to.
4. **Whether the poller backs off as far as the drainer.** The poller is how a dashboard-issued Pull
   or Sync reaches the machine, so a long backoff there delays remote commands too. Consider a lower
   cap for the poller (5 m?) than for the drainer (30 m), or accept the delay and say so in the KB.
5. **Whether an idle drainer should stop its timer entirely.** Today it fires every 30 s and returns
   immediately when the queue is empty. That is cheap in CPU but still a wake-up. Stopping the timer
   on empty and restarting it on `Enqueue` removes it completely, at the cost of one more moving
   part. Probably worth it *for this motivation specifically*.

---

## Execution order

1. **A single backoff policy object in `Agent.Core`**, used by all three loops. One curve, one set of
   tunables, one place to test. Not three hand-rolled variants that drift.
   - Pure and deterministic given (consecutive failure count, now) so it can be unit-tested without
     waiting real time.
   - A **test-only environment variable** to compress the curve, following the rule already settled
     in `Decisions.md`: read silently, default to production values, clamp the override, document in
     `Gotchas.md` only — never in `cli-reference.md` or the release notes.
2. **Drainer**: skip an entry whose `LastAttemptAt + Delay(RetryCount)` has not arrived, rather than
   attempting every tick. Per-entry, because one game may be failing for a reason another is not
   (a missing save folder, an oversized archive) — a per-process counter would let one bad game
   throttle a healthy one.
   - **Do not archive before the delay check.** The whole point is to skip the expensive work, and
     the current code hashes and zips first.
3. **Poller and heartbeat**: apply the same policy to consecutive tick failures. Keep the reconcile
   and the heartbeat on the same schedule — splitting them doubles the wake-ups.
4. **Reset**, per the decision above. Whatever is chosen, make it observable: log the transition into
   and out of backoff, with the interval. "It seems slow to sync now" is otherwise undiagnosable.
5. **Report it.** The agent already tells the console it could not reach the server; it should also
   be able to say it has backed off and when it will next try. Check whether `AgentEventCodes` needs
   a new code or whether the existing `ServerUnreachable` event should carry the interval — the
   console shows these, and "retrying every 30 minutes" is more use than "unreachable".

## Verification

Existing suites must stay green — this changes shared `Agent.Core` code that Windows and Linux both
run. Add to `tests/run-concurrency-tests.ps1` or a new suite, with the curve compressed by the
test-only variable:

1. A push against a stopped server queues, and the **first ten** retries happen at the fast interval.
2. The eleventh onwards are progressively further apart, and the gap **caps** rather than growing
   without bound.
3. Bringing the server back causes a sync **promptly** — this is the check that protects the arriving
   -home case. It must fail if the only reset is a successful retry.
4. A user-initiated push while backed off is **not** delayed.
5. Two games queued, one failing for a per-game reason: the healthy one is not throttled by the
   broken one.
6. Restarting the agent mid-backoff does not reset the counter to zero and start hammering again
   (`RetryCount` is persisted, so this should hold — assert it rather than assume).
7. **Measure the thing that motivated this.** Count requests reaching a stub server over a fixed
   offline window, before and after. State the numbers in the commit message. Without this the
   feature is asserted rather than demonstrated — the goal was fewer wake-ups, not a nicer curve.

Manual, on the Deck: leave it offline with a queued game and confirm from `agent.log` that the gap
grows and caps, then reconnect and confirm it syncs without waiting out the full interval.

## Done when

- All three loops share one backoff policy, and no loop retries at a fixed fast interval indefinitely.
- Recovery after reconnect is prompt and tested, not "within one backoff interval".
- A user-initiated sync always bypasses the backoff.
- Request counts over a fixed offline window are measured and quoted, not estimated.
- The curve, the cap, the reset rule and the poller-vs-drainer split are recorded in `Decisions.md`;
  any new test-only variable is in `Gotchas.md` and nowhere user-facing.

## Explicitly out of scope

- **Per-session offline history.** The queue stores intent, not save data, so a long offline stretch
  still produces one version on reconnect. Changing that means staging an archive per attempt and
  trades away the current "the queue costs a few hundred bytes" property. Separate decision.
- Radio/power management beyond not making needless requests. The agent does not manage the Deck's
  networking.
