# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

> **All three Linux/Deck security-hardening items shipped in v0.2.0** (PR #8, 2026-07-18) and the
> operational follow-up is done (container updated, fleet keys rotated). Indexed in
> `logs/shipped-2026-07.md`; rationale in `Decisions.md` §7–§9; narrative in `logs/sessions.md`.

## 🔴 ACTIVE — conflict handling (the 2026-07-22 Octopath incident)

One real play session on a Deck produced **75 open conflicts and 2.66 GB of saves on a game
configured to retain 5**, and escaping it required `curl` against the admin API. Nothing here is
hypothetical: every item below is a defect the maintainer hit in sequence over 2026-07-22/23.

**What the incident actually proved.** The server behaved correctly on every single request. The
*agent* misreported its parent version, and the *console* had no way to see or fix the result. So
the fix is mostly agent-side and console-side, and the tempting server-side reading ("resolution
logic is wrong") is the wrong one.

Full narrative in `logs/2026-07-23_conflict-storm.md`. Sequence of discovery, which matters because
each fix revealed the next one:

| # | Symptom the maintainer saw | Actual cause |
|---|---|---|
| 1 | 75 conflicts, console only ever offered one | `ConflictFlag` inserted per divergent push, console `.find()`s the oldest |
| 2 | 2.66 GB on a retain-5 game | prune unreachable while conflicted; every version pinned by an open conflict |
| 3 | Resolved in console → conflicted again immediately | console resolution never tells the agent; its parent pointer stays stale |
| 4 | Pulled to fix that → **still** conflicted on every save | the daemon never re-reads `config.json`, so it pushes from state the wrapper superseded |

⚠️ **Item 4 was the root cause of the loop and was invisible until items 1–3 were worked around.**
✅ **Fixed and device-verified 2026-07-23** (0.0 below). **The remaining items are still live:** the
loop is gone, but a genuine two-machine conflict would still produce a row per push (0.1), still
stall retention (0.2), and still leave the console showing the oldest and least useful one (1.1).
**Next up is 0.4.**

### Tier 0 — data-layer defects (small diffs, highest blast-radius reduction)

- ~~**0.0 — The daemon pushes from stale in-memory state.**~~ ✅ **DONE 2026-07-23**, device-verified
  on the Deck: 4 saves through the real Steam launch path, **zero conflicts**, where every prior
  session conflicted on every save. Full record in `logs/2026-07-23_agent-stale-parent.md`; the
  locked decision is `Decisions.md` §10, amending §8.
  - **Fix A:** `SyncEngine` calls `AgentConfig.RefreshGameSyncState` inside the per-game lock before
    push and pull. §8 had stopped a long-lived process *erasing* another's parent; nothing stopped it
    *using* one already superseded.
  - **Fix B — not the shape originally planned.** Auditing first showed all **17** `_config.Save()`
    callers write settings or the game list and none intends to write sync state. So `Save()` is now
    safe by construction and `CommandPoller` needed no edit at all. A rule 17 callers must remember
    is not a rule.
  - `run-concurrency-tests.ps1` **12 → 17**; checks 6 and 7 verified to FAIL against pre-fix code.
  - ⏳ **Released nowhere yet.** The Deck runs a hand-built `9.9.9-ci` tarball. **Windows agents are
    equally affected** — the tray is a long-lived host too. Ship this in the next release.

- **0.4 — Resolving a conflict must un-stick the agent.** `ResolveConflictAsync` is a database edit
  that looks like an action. It should enqueue a **guarded** `Pull` for the machine whose version
  lost; the command channel already exists (`SyncService.cs:620`, picked up on the 20 s poll).
  - ⚠️ **Guarded, never forced.** An auto-triggered *force* pull is precisely how the losing machine's
    unpushed progress gets destroyed — the failure class `Gotchas.md` and v0.3.2 already fought twice.
    The console's own Pull button sends `force: true` (`GameDetail.tsx:370`); do not reuse that path.

- **0.1 — Conflicts must deduplicate.** `SyncService.cs:437` inserts a fresh `ConflictFlag` per
  divergent push, unconditionally. Dedupe on **(gameId, versionAId, diverging machineId)**: update
  `VersionBId` to the newer version, bump a `Count`, touch `LastSeen`.
  - Mirror `HealthService.ApplyEventAsync` (`HealthService.cs:100`), which already dedupes on
    (machine, game, code) while open and documents why appending produces 4,300 rows a day. Conflicts
    need the same reasoning and should be recognisably the same shape.
  - Including `machineId` keeps two genuinely diverging machines as two conflicts. 75 → 1.

- **0.2 — Retention must run while conflicted.** The conflict branch returns at `SyncService.cs:451`
  without calling `PruneVersionsAsync`. Add it, and call it from `ResolveConflictAsync` too —
  resolution is the moment a pile of versions becomes unpinned. Already safe to run: the protected
  set (`SyncService.cs:481`) covers the head and every open-conflict version.
  - ⚠️ **`NoChange` returns at `SyncService.cs:410`, above the prune call and above the force check.**
    So a force-push with no gameplay since prunes nothing and looks like the fix failed. This wasted
    real time during the incident; whatever we do here, keep that path in mind.

- **0.3 — Resolving must not silently rewind the head.** `SyncService.cs:577` writes `HeadVersionId`
  with no regard for what is already there — which is how a resolve silently undoes a "Set as Latest".
  At minimum audit it distinctly when the winning version is *older* than the current head; right now
  the destructive case is indistinguishable from a normal resolve in the audit log.

### Tier 1 — console UX (the maintainer's requirement: no `curl`, and not tedious)

- **1.1 — Show every conflict, newest-first.** `GameDetail.tsx:40` does `.find()` over a list ordered
  oldest-first (`SyncService.cs:563`), so the console reliably surfaces the *least* useful conflict.
  Flip to `OrderByDescending` and render `.filter()`. The newest conflict is the one holding the
  user's actual progress.
- **1.2 — Show enough to decide.** Today: `Keep 4a3f9c21 from Deck (7/22/2026, 4:31 PM)`. Nobody can
  choose between two saves on a hash fragment. Show machine, local time, size, **and the delta**
  (file count, newest file mtime). Add **Keep both** — promote the winner, retag the loser as a
  protected version exempt from pruning. That is the option users actually want and cannot express.
- **1.3 — Confirm before resolving.** `GameDetail.tsx:277` fires with no confirmation, while delete,
  Set as Latest and delete-game all confirm. It is the most consequential button on the page and the
  only unguarded one. Name consequences concretely, including how many newer saves are affected.
- **1.4 — Bulk actions on the Versions card.** A **Prune now** button (apply retention server-side
  without needing a push) and multi-select delete. `PruneVersionsAsync` exists; it has no endpoint.
- **1.5 — Download a version from the console.** Version download is agent-only (`Program.cs:295`,
  `X-Api-Key` group). There is no admin route and no button, which is why "back up the good save
  before touching anything" was not offerable as a UI step during the incident. Add
  `admin.MapGet("/games/{id}/versions/{versionId}/download")` + a per-row button. **This is the escape
  hatch that makes every other destructive action safe to offer.**
- **1.6 — Conflict alerts must not be dismissible like transient warnings.** `HealthService.cs:201`
  treats a conflict identically to "server unreachable", but every other agent event self-heals and a
  conflict does not. The maintainer dismissed the toast and played for a day. Either suppress Dismiss
  for `AgentEventCodes.Conflict`, or require acknowledging that the conflict remains open. Badge
  should deep-link to the resolution UI.

### Tier 2 — prevention

- **2.1 — Per-game conflict policy.** Add `Game.ConflictPolicy` ∈ `{ Manual, NewestWins,
  PreferMachine }`. For a single-player game across one person's own devices `NewestWins` is right
  essentially always, and would have made this entire incident a non-event. Keep `Manual` the default;
  offer the policy at the moment of first conflict. **Highest prevention value in the plan**, but needs
  a schema change + migration, which is why it is not Tier 0.
- **2.2 — The agent should back off when conflicted.** `SyncEngine.cs:178` alerts and retries forever;
  it uploaded ~75 near-identical full archives nobody asked for. After N consecutive conflicts on a
  game, report without uploading the payload — the server already holds a divergent copy.
  - ⚠️ Verify first whether `2659.3 MB / 80` ≈ one save, which would confirm the archives are
    near-duplicates and the backoff saves real Deck wifi, not just server disk.
- **2.3 — Escalate a conflict that goes unread.** A conflict open >6 h is categorically different from
  one open 5 minutes — it means the human does not know. Escalation must reach the Windows tray as
  well as the console, since the Deck cannot toast (`Decisions.md` §2). In this incident that would
  have reached the maintainer on the PC while the Deck stayed silent.

### Suggested sequencing

| Order | Items | Why |
|---|---|---|
| ~~1~~ | ~~**0.0**~~ | ✅ done 2026-07-23 — the agent no longer misreports its parent |
| **2** | **0.4**, 0.1, 0.2 | Kills the conflict-loop, the 75-row explosion, and the unbounded storage |
| 3 | 1.1, 1.3, 1.6 | Makes the existing UI honest — small diffs, high safety return |
| 4 | 1.4, 1.5 | Removes the need for shell access entirely |
| 5 | 2.1 | Highest prevention value; schema change + migration |
| 6 | 1.2, 2.2, 2.3, 0.3 | Polish and hardening |

⚠️ **Tiers 1 and 2 touch the API** — regenerate `web/src/api-types.ts` and commit the updated
`src/Server/openapi.json` snapshot, per `CLAUDE.md`.

⚠️ **The test-suite lesson applies again, exactly as `CONTEXT.md` warns.** Every defect here was
invisible because no suite puts the system in the state where it fires: nothing pushes *twice* into
an open conflict (0.1, 0.2), and `run-concurrency-tests.ps1`'s 12 checks cover the write race but not
**a long-lived reader that went stale** (0.0). Each fix ships with the test that would have caught it.

## High priority
- ~~**Linux Help-KB articles**~~ ✅ **DONE 2026-07-18 — and most of it was already done.** The task file (`tasks/linux-kb-articles.md`) had been **deleted** in `ff2c375`, while `CONTEXT.md` and this file both still pointed at it and listed work that had shipped days earlier. Actual state when checked: `deck-supported-games` written and registered (`83aa15e`); `deck-troubleshooting` **folded into `troubleshooting.md`** as the task file explicitly permitted, covering every bullet it listed; and all four §4 edits (`cli-reference`, `save-in-use-safety`, `conflicts`, `adding-games`) already applied. Recovered the task file from git history to confirm rather than trusting either doc.
  - **The real gap was newer than the task file:** v0.2.0 added user-visible refusal messages (`REFUSED the server's save`, symlink/junction, size and entry limits, path escape) with **nothing in the KB explaining them** — on a Deck, where the KB is the only support surface. Wrote `restore-safety.md` (Troubleshooting) covering all three causes with the fix for each, the deliberate exception that a symlinked save *root* still works, and the `SAVELOCKER_MAX_RESTORE_*` overrides. Cross-linked from `troubleshooting.md`; verified rendering, search, and both deep-links in the running dashboard.
- **Code-signing** — installer + exe currently unsigned. SmartScreen warns on first run for new users. Options: EV certificate or Azure Trusted Signing. *Explicitly deferred by the maintainer, 2026-07-18.*
- ~~**Device-verify the agent window post-0.2.0.**~~ ✅ **DONE 2026-07-18** — both machines were re-registered *from the agent window*, so WebView2 loading the injected token and sending it on every `/api/*` call is confirmed on real hardware.
- ⏳ **Deferred by design: one state owner** for the Linux agent — wrapper→daemon IPC over a Unix socket, standalone fallback when no daemon is up. The locking in `Decisions.md` §8 makes the current two-owner model *correct*; IPC would make it *simple*. Worth doing before the state files grow further.

## Planned — large
- **Linux agent (Proton / Steam Deck)** — ✅ **SHIPPED, all six phases** (2026-07-12 → 2026-07-14; PRs #1, #4, #5, #6). Design locked in `Decisions.md`; the plan and its outcomes are archived at `logs/2026-07-14_linux-agent.md`. Proton-only for v1, headless daemon serving the existing React UI, Steam launch-option wrapper as the sync trigger.
  - **Shipping (2026-07-14):** `release.yml` now builds the **Linux tarball on `ubuntu-latest`** and attaches `savelocker-<ver>-linux-x64.tar.gz` to the GitHub Release. Before this there was **no way for a Deck user to install the agent at all** — the packaging scripts existed but nothing ever ran them. CI's `package-linux` job now installs the tarball into a throwaway HOME on every PR so it cannot rot.
- **Device-verify fresh Windows installer enrollment.** The wizard and silent-upgrade guard shipped in v0.1.7. The **upgrade** path is now well verified (silent 0.1.6→0.1.7 in 2026-07-14; 0.1.8→0.2.0 on two machines in 2026-07-18). It is the **fresh install** that has never run: clean-machine happy path, config-directory ACL, expired-token, skip, and `/SILENT /ENROLL=…`. Needs a box where `%PROGRAMDATA%\SaveLocker` does not exist.
  - **Deferred: Linux auto-update.** The update channel (`/api/agent/latest` → hosted `.exe`) is installer-shaped and Windows-only. A Deck user currently re-runs `install.sh` from a newer tarball. Retrofitting it for a tarball is its own piece of work — worth doing before there are many Deck users, since a headless device that never updates is one nobody will notice is stale.
  - **ALL PHASES (0–6) DONE.** Proton saves are byte-identical to Windows saves (proven in CI, round-tripping Windows→Linux→Windows). Enrollment is one file and one command, with no API key ever copied by hand. A Deck's failures reach the console. And Phase 6 fixed a **real data-loss bug** that also affected Windows: the restore's delete pass followed symlinks and **deleted files outside the save folder** (junctions on Windows, and a Wine prefix is full of links).
  - ✅ **HARDWARE NOW AVAILABLE (2026-07-19).** The maintainer owns a Steam Deck — the long-standing blocker is resolved. gamescope, Game Mode, SD-card paths, and suspend/resume can now be validated on-device.

## Steam Deck onboarding UX (discovered via real hardware, 2026-07-19)

All items below were surfaced by the first real Deck user session. The session exposed that the
current flow requires typing 130-character paths on an on-screen keyboard, misidentifies AppIDs,
silently crashes on empty save dirs, and gives the user no console-driven alternative.

> **The two path-setup sections below are DONE (2026-07-19)** — all five items. The four "Quick
> guardrails" were already fixed in `f1edc4e`. What remains from the Deck session is device
> verification, not code.

### Quick guardrails

✅ **All four shipped in `f1edc4e`** (2026-07-19). Re-verified against the filesystem on 2026-07-19
rather than trusting the commit message — see the vault-drift entry in `Gotchas.md`:

- ~~`pull` with empty save dir must fail cleanly~~ — `AgentCli.cs:272` prints `'<game>' has no save
  directory set.` and exits non-zero. No restore attempted.
- ~~`add-game` should warn when `--dir` doesn't exist~~ — warns `save directory does not exist`.
- ~~`doctor` should point at `scan` when appid is missing~~ — `Doctor.cs` names `savelocker scan`.
- ~~`install.sh` should print the full-path launch option~~ — prints
  `${HOME}/.local/bin/savelocker run -- %command%` and explains that Game Mode lacks
  `~/.local/bin` on PATH, so the short form silently stops the game launching.

### Console-driven path setup (eliminates CLI typing on Deck)

- ~~**Make the per-machine save path field obviously per-machine in the console.**~~ ✅ **DONE
  2026-07-19.** The `prompt()` is gone. Editing now happens **inline in that machine's row**, under
  a label reading `Save path on <Machine Name>`, with Save/Cancel (Enter/Escape also work). The old
  modal was detached from the table, which is exactly what made it easy to type a Deck path into a
  Windows machine's row.

- ~~**Surface scan candidates in the console when a Deck reports an unmapped game.**~~ ✅ **DONE
  2026-07-19.** Agents now report unconfirmed guesses on the existing heartbeat and the row shows
  `<Machine>'s scan found: <path>` with a one-click **Apply**.
  - **New wire field:** `AgentHeartbeat.PathCandidates` — appended and optional, so the fleet and
    the container still upgrade in either order. The regenerated `openapi.json` diff is **+83 / −0**.
  - **New table `MachineScanCandidates`** (migration `20260719190348_AddMachineScanCandidates`),
    keyed `(MachineId, GameId)`, cascading from both. Deliberately **not** written into
    `MachineSavePaths`: that table is pushed back to the agent as authority on the next poll, so a
    guess landing there would auto-apply itself. A human promotes it; applying retires the guess.
  - **Only unmapped games are reported** — uploading the whole scan would put the user's entire game
    library on the server for no purpose the console has.
  - Scanning is throttled to 15 min in `CommandPoller`; the 20 s poll would otherwise make an
    unmapped game a permanent background I/O load on a slow SD card.

- ~~**`scan` should offer to auto-apply when a tracked game is unmapped.**~~ ✅ **DONE 2026-07-19.**
  Prompts `Found save path for '<game>' — apply? [<path>] (y/n)`, then saves config and reports the
  path to the server. `--yes` applies all; `--no-prompt` only lists. **Prompting is skipped when
  stdin is redirected** — `scan` runs from the test harness, from dashboard-issued commands and
  under systemd, where a prompt would read EOF or block the unit forever.

### Agent UI path browser (controller-navigable, no terminal required)

- ~~**Add a path mapping flow to the agent UI (`localhost:5178`).**~~ ✅ **DONE 2026-07-19.** An
  unmapped game's row now reads `No save folder set` with a **Set save path** button instead of
  showing nothing.
  - **New `GET /api/browse?path=`** backed by `Agent.Core/PathBrowser.cs`. **Rooted** at `$HOME` plus
    host-supplied Steam roots (`GameScanner.BrowseRoots()` on Windows, `SteamRoots.BrowseRoots()` on
    Linux — the latter includes `/run/media` so an **SD card** is reachable). Containment is checked
    *after* canonicalization, symlinks are never followed, and a refused path is not described.
    Covered by 8 new checks in `run-local-api-tests.ps1` (now **22**), including a symlink inside
    `$HOME` pointing outside it.
  - **The native dialog still wins where there is one.** The UI calls `folderPick` first; the Windows
    tray returns its Explorer dialog and the browser never appears. The Linux daemon passes
    `pickFolder: null`, and *that* is what drops a Deck into the browser. `agent-ui/` is one shared
    bundle — this is how the Deck flow stays off Windows.
  - ✅ **Verified on a real Deck 2026-07-19**, with one correction. **The D-pad does not work** —
    SteamOS Desktop Mode maps the right stick to the mouse cursor and the left stick to scrolling,
    and the D-pad to neither. Point-and-click is the real interaction, which the 44 px rows were
    already sized for, so the feature works as intended; only the *claim* was wrong. KB corrected.
    Arrow keys still work with a keyboard attached, so the handler stays.
  - ✅ **`/run/media` confirmed present in the root list on-device**, so SD cards are reachable.
  - The maintainer's reaction is worth recording: *"I didn't know we had a UI because we kept calling
    it headless."* The word was doing real damage in our own docs — `adding-games.md` flatly said
    there was no UI to click. Both articles now lead with what headless does and doesn't mean.

- ~~**Pre-fill scan candidates in the agent UI path browser.**~~ ✅ **DONE 2026-07-19.** New
  `GET /api/games/{id}/suggested-path` name-matches the (cached) scan and the browser opens there,
  falling back to the root list when the guess no longer exists on disk.

## Found on real hardware, fixed in v0.3.0 (2026-07-19)

Four bugs from one afternoon with a Deck. Recorded because of what they have in common: **three
were invisible to the test suite by construction** — it ran in the one state where each could not
fire. When a suite passes, ask what state it never puts the system in.

- ✅ **`install.sh` destroyed a running agent and reported success.** `cp` cannot replace a running
  binary (`Text file busy`); overwriting the mapped DLLs killed the daemon with **SIGBUS**; and
  `find -exec` hides its child's exit status, so `set -euo pipefail` never fired and the script
  printed "Installed." Fixed by stopping the unit first and copying with `--remove-destination`
  (new inode, so anything still running survives). CI now upgrades over a live daemon every PR.
- ✅ **`savelocker status` 401'd wherever an admin password was set.** It called the admin-filtered
  `/api/games/{id}/state` with a machine key. `AdminPasswordFilter` passes everything through when
  no password exists, so it worked on a fresh server — and the suite runs without one, so it passed
  for as long as the bug existed. Now `/api/agent/games/{id}/state`; the test sets a password.
- ✅ **A renamed Steam shortcut silently stranded the save path.** A non-Steam AppID is
  `crc32(exe + name)`, recomputed on rename/re-point, and Steam then creates a *fresh* prefix. The
  old path keeps existing, reading and hashing fine. `doctor` now compares the tracked
  `compatdata/<id>` against the shortcut's current AppID. **Do not "fix" this with a wildcard** — a
  glob could match the empty new prefix and push it over a good server head.
- ✅ **A save folder one level too deep nests itself on restore.** Archives store paths relative to
  the save root, so pulling an archive rooted at X into X/sub recreates `sub` beneath itself; the
  pull *succeeds* and the game never sees the files. `SaveDirSanity` now flags a repeated path tail
  (longest run first, warning only — a game whose save folder legitimately repeats a name must
  still sync).

Also corrected: the KB claimed **D-pad** navigation in the agent UI's folder browser. It does not
work — SteamOS Desktop Mode maps the right stick to the cursor and the left stick to scroll, the
D-pad to neither. Point-and-click is the real interaction. And "headless" was misleading readers
into thinking there was no UI at all; both articles now say what it does and does not mean.

## ✅ DONE (2026-07-20) — version the console/server release

Shipped. The console reports its own version and carries hand-written release notes.

- **Build identity** — `docker-publish.yml` computes `<nearest tag>[+<n>.<sha>]` from
  `git describe`, passes it as build args, and the Dockerfile bakes it into the runtime image as
  `SAVELOCKER_VERSION` / `_COMMIT` / `_BUILT_AT`. `BuildInfo.cs` reads env → assembly
  `InformationalVersion` → `"dev"`. An unstamped build says `dev`; it never invents a number.
- **Served** on `GET /api/admin/status` as `{ passwordRequired, build }` — `passwordRequired` kept
  its original JSON path, so every existing reachability probe (six test suites, CI, `ApiClient`)
  was unaffected.
- **Shown** in three places: a version chip in the NavBar (always on screen, click → release notes,
  amber on a dev build), a **Console** card on Configuration sitting directly above **Machines** so
  console and fleet versions read together, and the **What's New** view itself.
- **Release notes** are hand-written markdown, one file per release in `web/src/releases/`, bundled
  via `?raw` exactly like the Help KB. The same file is the GitHub Release body (`release.yml`
  passes `body_path`), so notes are written once and cannot drift. Backfilled 0.3.0 and 0.3.2;
  **deliberately no 0.3.1**, and the 0.3.2 notes explain why.
- **Images are tagged with the version** alongside `:latest`, so a rollback has something to name.

⚠️ **`docker-publish.yml` needed `fetch-depth: 0` + `fetch-tags: true`** — `git describe` fails on
the default shallow clone. Same class as the documented MinVer-stamps-0.0.0.0 trap.

- **Version skew is surfaced** (`web/src/versionSkew.ts`). Two distinct faults, named separately:
  an agent **newer than the console** (badge in the Machines row + a warning on the Console card),
  and **a fleet running mixed agent versions** (the thing that produces repeated conflicts). Only
  the newer-than-console direction warns — an older agent is normal and supported, per the deploy
  note. A CI tarball (`9.9.9-ci`) is labelled **TEST BUILD** rather than reported as newer, which
  it would otherwise be forever. Both warnings are absent when the fleet agrees.

## Medium priority
- **Windows: `%PROGRAMDATA%\SaveLocker` ACLs on a multi-user box.** The local API token (`api-token`, 0600 on Linux) has **no POSIX mode on Windows** — it inherits the ACL of `%PROGRAMDATA%\SaveLocker`, which the installer widens to give the interactive user Modify. On a machine with several local users, another user may be able to read it and drive this machine's agent. Note this is **not a new exposure**: `config.json` in the same directory already holds the long-lived machine key under the same ACL. Fix both together — tighten the directory ACL to the enrolling user + SYSTEM, or move mutable per-user state out of the machine-wide directory. `run-local-api-tests.ps1` only asserts the file *exists* on Windows; give it a real ACL assertion once the model is decided.
- **Linux agent secret permissions and state layout.** `config.json` contains a long-lived machine key, but file privacy currently depends on the launching shell's umask. Enforce `0700` on private state directories and `0600` on config, queue, health, and log files in code, including CLI enrollment paths. Consider separating immutable app files from mutable XDG config/state so upgrades and permission repair cannot overlap the executable tree.
- **Harden the `systemd --user` unit.** Keep the non-root per-user design, but add and Deck-test conservative restrictions such as `UMask=0077`, `NoNewPrivileges=yes`, `PrivateTmp=yes`, `ProtectSystem=full`, `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6`, `RestrictSUIDSGID=yes`, and `LockPersonality=yes`. Mirror every directive in both `packaging/linux/savelocker.service` and `SystemdAutoStart.UnitFile()` so the UI toggle cannot regenerate a weaker unit. Do not add `ProtectHome` (save access), `ProtectProc` (Linux writer probe), or `MemoryDenyWriteExecute` (.NET JIT). Record `systemd-analyze --user security savelocker.service` before/after on a Deck.
- **Linux release provenance.** Pin GitHub Actions in `release.yml` to full commit SHAs, especially the release-upload action with `contents: write`; publish SHA-256 checksums and a GitHub artifact attestation for the tarball; enable immutable releases using a draft → attach all assets → publish flow. Document the verification command beside the Deck install instructions.
- **Constrain external manifest paths.** The Ludusavi manifest is downloaded from mutable `master`, and expanded templates are not currently proven to remain inside the intended Proton prefix. Pin or integrity-verify an approved manifest revision, canonicalize resolved paths, reject `..`/symlink escapes outside allowed roots, and test a hostile manifest entry. Preserve explicit manually mapped portable-save paths as a separate trusted-user path.
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

_Dropped items (won't-do) are recorded in `logs/shipped-2026-07.md`._
