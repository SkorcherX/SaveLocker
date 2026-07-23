# Task 0.0 — The daemon must not push from stale state

**Backlog:** `Backlog.md` → "🔴 ACTIVE — conflict handling", item 0.0.
**Why first:** every other item in that plan is a fix for a *consequence*. This is the cause. While
it stands, a Deck conflicts on every save no matter how the server or console behave.

## The defect, precisely

Two processes own one `config.json`:

- **`ProtonRun`** (Steam launch wrapper) loads it per game launch. Always fresh.
- **The daemon** loads it once at boot. `Daemon.cs:118` captures `TrackedGame` references out of
  `_config.Games` when starting folder watchers; nothing calls `AgentConfig.Load` again for the
  process lifetime.

So after the wrapper pushes on exit and writes a new `LastKnownVersionId`, the daemon's folder
watcher pushes with the superseded parent. `SyncService` correctly sees `serverHead.Id !=
parentVersionId` and records a conflict. Because `SyncEngine.cs:178` deliberately does **not**
advance the pointer on conflict, the daemon never recovers — it conflicts on every save until the
process restarts.

`AgentConfig.SaveGameSyncState` (`AgentConfig.cs:123`) documents this exact scenario in its own
comment, but fixed only the **write** side. `Decisions.md` §8 closed half the door.

**Second half, worse:** `CommandPoller.cs:179` calls the full `_config.Save()`, which
`AgentConfig.cs:96–100` states plainly is unsafe for per-game sync state. On any game-list change it
serializes the daemon's stale `LastKnownVersionId` for *every* game over the file, rewinding the
pointer the wrapper just wrote — so the **wrapper's** next push conflicts too. This is the §8
lost-update reintroduced through a call path the fix never covered.

## Steps

### 1. Measure before changing anything

Establish how often `ReconcileGamesAsync` actually writes. If `changed` flips true on most polls,
Fix B is the dominant cause and the severity ordering below changes.

✅ **DONE 2026-07-23 — answered statically, and the static answer is stronger than the sample.**

**Result: `changed` is edge-triggered, not level-triggered. In steady state it is `false`.** Every
one of the five paths that sets it writes the value that makes its own next comparison equal, so each
converges after a single poll:

| Path | `CommandPoller.cs` | Converges because |
|---|---|---|
| game deleted server-side | 103–108 | `RemoveAll` — gone next poll |
| exclude globs differ | 117–121 | assigns `local.ExcludeGlobs = serverGlobs` |
| server has a path for this machine | 124–130 | assigns `local.SaveDirectory = sg.MachineSavePath` |
| tracked but unmapped, resolved locally | 132–139 | assigns `local.SaveDirectory = fill` |
| adopt a new server game | 163–174 | game now exists locally |

⚠️ **A 5-minute idle sample would have measured 0 and been actively misleading** — it would have read
as "Fix B is unnecessary." The dangerous case is not steady state. `changed` flips precisely when a
**human is editing in the console** (changing a save path, adding or removing a game), which is
exactly what the maintainer was doing while recovering from the incident. So Fix B corrupts the
parent pointer at the worst available moment: mid-repair.

**Consequence for severity:** Fix A is the always-on cause of the loop; Fix B is a latent corruption
that fires during console edits. Both ship, but A is the one that ends the conflict-on-every-save.

### 2. Fix A — refresh the parent before using it

Add to `AgentConfig`:

```
/// <summary>Re-read this game's sync bookkeeping from disk into the caller's object.</summary>
public void RefreshGameSyncState(TrackedGame game)
```

Mirror the read half of `SaveGameSyncState`: acquire `AgentStateLock.Acquire("config", StateDir)`,
deserialize `ConfigPath`, find the entry by `GameId`, and copy **`LastKnownVersionId` and
`LastSyncedHash`** into the passed object. Missing file, unreadable JSON, or a game not present on
disk must all leave the in-memory object untouched — the existing `catch` in `SaveGameSyncState` sets
the precedent.

Call it in `SyncEngine` at the top of both `PushAsync` and `PullAsync`, **after** taking
`AgentStateLock.ForGame`, before delegating to the core method.

⚠️ **Lock ordering.** The existing order is `ForGame` → `config` (`PushAsync` holds `ForGame`,
`SaveGameSyncState` then takes `config`). The new call must keep that order. Before writing it,
confirm nothing takes `config` → `ForGame`, and confirm `AgentStateLock` tolerates the same name
being acquired twice in sequence within one process — on Linux `flock` is per file-description, so a
nested acquire of the *same* name can block a process against itself. Sequential is fine; nested is
not.

### 3. Fix B — make `Save()` safe by construction

✅ **DONE 2026-07-23 — but not in the shape this task file originally proposed.**

The plan was to give `ReconcileGamesAsync` its own merge-write. Auditing the call sites first changed
the diagnosis: **`_config.Save()` has 17 callers, and every one writes settings or the game list** —
server URL, machine name, API key (`AgentApiServer.cs:194/209/228/238`), a save folder
(`ProtonRun.cs:133`, `AgentCli.cs:450`), a removed game, enrollment seeding (`Enroller.cs:47`,
`AgentCli.cs:380`), tray settings (`TrayApp.cs:130/284/364`). **None intends to write per-game sync
bookkeeping.**

So the defect is not that `CommandPoller` forgot a rule — it is that the rule lived in a doc comment
that 17 call sites had to remember, and one didn't. A rule like that is not a rule. `Save()` now
preserves `LastKnownVersionId` / `LastSyncedHash` (plus `TotalSavesPushed` / `LastSyncTime`) from
disk, and `SaveGameSyncState` remains their only writer. `CommandPoller.cs:179` needed no edit.

Two properties worth keeping in mind:

- **The preserve-from-disk step also refreshes the in-memory objects**, so a long-lived host that
  saves a setting now *stops* being stale instead of propagating staleness. Documented as intentional.
- **A game absent from disk keeps its in-memory values** (it is new here), so enrollment seeding and
  game adoption still persist correctly.

**`SaveDirectory` decision — the caller wins, deliberately.** It is reconciled from the server (its
highest authority per `ResolveSaveDirAsync`) and set directly by the agent UI and CLI, all of which
route through `Save()`. Preserving it from disk would silently discard exactly the write each caller
is making. Recorded in the doc comment so the ambiguity cannot quietly return.

### 4. Regression tests — the ones that would have caught it

✅ **DONE 2026-07-23. `run-concurrency-tests.ps1` is now 17 checks (was 12).**

- **Check 6 — stale reader.** CLI pushes game A (advancing server head and `config.json`), then the
  daemon's watcher is triggered on `saveA`. Asserts the daemon's watch-push does not conflict.
- **Check 7 — reconcile must not rewind.** Game C is the discriminator: the daemon watches `saveC` but
  nothing ever modifies it, so the daemon has never pushed C and its in-memory copy still holds the
  boot-time null. CLI pushes C, then an exclude-glob change is made server-side to force a reconcile
  write. Asserts C's parent version survives.

✅ **Both proven to FAIL against pre-fix code** — stashed the two source files, rebuilt, re-ran:
**15 passed, 2 failed**, and the two failures were exactly checks 6 and 7 while every surrounding
setup assertion still passed. That last part matters: it shows they discriminate on the defect rather
than erroring out.

⚠️ **Check 6 needs its 20-second quiesce.** Check 1 modifies `saveA` and the daemon watches it, so
without a settled start the assertion races the settle gate. **That timing accident is why the
existing suite came within one check of this bug and never caught it** — the daemon's conflicting
push happened after the script had moved on.

### 5. Verify

✅ **All green 2026-07-23.**

- `.\tests\run-concurrency-tests.ps1` — **17/17** on Windows.
- `.\tests\run-agent-tests.ps1` — **20/20** on Windows. Note "Laptop stale push reports CONFLICT"
  still passes: the refresh reads each machine's *own* disk, so genuine two-machine divergence is
  still detected. That was the main regression risk and it is covered.
  ⚠️ Clear `.verify/` and the server DB **together**; see `Gotchas.md`.
- **WSL (real `flock`) — 17/17.** The lock-ordering warning in step 2 is resolved: no nested
  same-name acquisition exists anywhere. `RefreshGameSyncState` acquires and releases `config` before
  `PushCoreAsync` takes it via `SaveGameSyncState`, and `Save()`'s new disk read happens *inside* the
  lock it already held, via a plain unlocked helper.
  ⚠️ `dotnet build` takes **one project per invocation** — passing two csproj paths fails with a
  bare "For switch syntax, type MSBuild -help". Not obvious from the error.

### 6. Device-verify on the Deck — the only proof that counts

✅ **PASSED 2026-07-23.** Daemon restarted, Octopath launched through Steam, **4 saves, all backed up,
zero conflict notifications.** Every prior session conflicted on *every* save, so this is the
discriminating result — no harness reproduces it, because the defect needs two real processes and a
real Steam launch.

Tested with a hand-built tarball stamped **`9.9.9-ci`** (`packaging/linux/build-linux.sh 9.9.9-ci`),
built in WSL on Ubuntu 24.04. Two reasons that stamp, both deliberate: `versionSkew.ts` labels it
**TEST BUILD** so it cannot be mistaken for a release or warn "newer than console" forever, and
glibc 2.39 is older than SteamOS's rolling Arch, so the binary is forward-compatible. Building it on
Arch would produce `GLIBC_2.4x not found` on hardware.

⚠️ **The fix is on the Deck as an unreleased test build.** It is not in any tagged release, and the
Windows agents do not have it — they are equally affected (the tray is a long-lived host too). Ship
it in the next release before considering this closed for the fleet.

## Done when

- ✅ Both fixes in, both regression tests green and **proven to fail against pre-fix code** (15/2).
- ✅ All suites pass — concurrency 17/17 (Windows + WSL), agent 20/20 (Windows).
- ✅ A full Deck play session produces zero conflicts.
- ✅ `Gotchas.md` updated with the step-1 finding; `Decisions.md` §10 records the locked choice.
- ✅ Moved to `logs/` and `CONTEXT.md` updated.
- ⏳ **Not yet released.** Windows agents still carry the bug; see the warning above.

## Do not

- Do not "fix" this by having the conflict path advance `LastKnownVersionId` (`SyncEngine.cs:178`).
  That makes the agent adopt a parent it never pulled, so the next push silently overwrites the other
  machine's save. The stale pointer is a symptom; adopting a wrong one is data loss.
- Do not make the daemon reload the whole config on a timer. That reintroduces the last-writer-wins
  race `SaveGameSyncState` exists to prevent, just with a different window.
- Do not touch items 0.1–0.4 here. They are separate task files; this one stops when 0.0 is verified.
