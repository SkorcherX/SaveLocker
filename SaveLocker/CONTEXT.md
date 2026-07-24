# SaveLocker — Session Context

**What:** Self-hosted Windows game save sync. Hub-and-spoke: C#/WinForms tray agent on each PC syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard + embedded React agent UI.

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** main

**Current version:** **v0.3.2** (tagged 2026-07-20 — **four save-path bugs found on real hardware,
plus templated save paths**). It supersedes v0.3.1, which was tagged but never published (below).

### v0.3.2 — the save-root class, closed at three layers

Two machines disagreeing about a game's save root by even one segment made a restore nest a folder
under itself **and delete the correctly-placed copy**, silently, reporting success. That is now
addressed at every level rather than patched at one:

| Layer | What |
|---|---|
| **Describe** | `SuggestedSaveDir` accepts a **template** (`<winPublic>/Documents/…`). An agent with a working folder produces one automatically by reversing its own resolver — under Proton, against the game's own prefix. First correct machine teaches the fleet. |
| **Expand** | Every machine resolves that template against its own folders, so the root is the same *logical* place everywhere by construction. |
| **Refuse** | A restore whose target is deeper than the archive's root is rejected **before** the copy or delete pass, naming the repeated segment. |

Also fixed: **`<winPublic>` resolved one level too deep on Windows** (`CommonDocuments` instead of
`%PUBLIC%`), so all 44 manifest games using it produced `…\Public\Documents\Documents\…`. The Proton
side was already correct, which means Windows and a Deck disagreed about the same game's save root
automatically, with no user error at all. Confirmed against the cached 16.6 MB manifest, not memory.

And the Windows tray fixes originally cut for v0.3.1: **Exit deadlocked the agent** (sync-over-async
on the WinForms UI thread — diagnosed from a stack captured off the live hung process, since nothing
reached the log), and an **update check returning `Failed`/`Skipped` said nothing at all**.
Earlier: v0.3.0 (2026-07-19, PRs #9–#13 — Deck path setup, and four bugs the first real hardware
session exposed); v0.2.0 (2026-07-18, PR #8 — Linux/Deck security hardening, `Decisions.md` §7–§9).

⚠️ **v0.3.1 was tagged but NEVER PUBLISHED** — its assets died in the GitHub incident of
2026-07-19 23:34 UTC, so the release stayed a draft with 0 assets and nobody ever ran it. It was
**superseded by v0.3.2** rather than published: by the time the incident cleared, four more fixes and
the templated-path work had landed, two of them data-loss class. Shipping a build with known
save-destroying behaviour when the fix already existed would have been indefensible.
**The v0.3.1 tag remains as a history marker; there is deliberately no v0.3.1 release.**

### v0.3.1 — the tray Exit deadlock

**Right-click → Exit did not stop the agent**, and a second Exit left the menu frozen on screen;
only Task Manager could end it. `AgentApiServer.Dispose()` blocked the WinForms UI thread with
`StopAsync().GetAwaiter().GetResult()`, and the continuation needed that same thread. Kestrel stops
listening first, so :5178 went dead while the process lived on — which is what made it look like a
half-crash rather than a deadlock.

- **Present since v0.1.8**, when the local API landed. Every Windows agent from 0.1.8 to 0.3.0 has it.
- Diagnosed from a **stack captured off the live hung process** (`dotnet-stack report -p <pid>`) —
  worth remembering as the tool for this, since nothing was in the log.
- Fixed with `Task.Run` so continuations land on the thread pool, plus a bounded wait. `Start()` had
  the same shape and got the same treatment.
- Reproduced in a harness that drives `Dispose` from a real message loop: **old = deadlock at 30 s,
  fixed = 8 ms**.
- Also: an update check that returned `Failed` or `Skipped` produced **no toast and no log line**, so
  "I clicked check for updates and nothing happened" was undiagnosable. Every outcome is now logged
  and a user-initiated check always answers.

### v0.3.0 — what and why

Setting a save path on a Deck no longer needs a terminal: a rooted folder browser in the agent UI,
scan candidates offered for one-click **Apply** in the console, and `scan` offering to map an
unmapped game. Detail in `Backlog.md` under "Steam Deck onboarding UX".

**The four bugs matter more than the feature.** Every one was found by putting the agent on real
hardware for an afternoon, and none was hypothetical:

| Bug | Why it was invisible |
|---|---|
| **`install.sh` destroyed a running agent, then printed "Installed."** | `cp` cannot replace a running binary (`Text file busy`), overwriting mapped DLLs killed the daemon with **SIGBUS**, and `find -exec` hides its child's exit status so `set -e` never fired. This is THE documented Linux update path. |
| **`savelocker status` 401'd on any server with an admin password** | It called an **admin-filtered** route with a machine key. `AdminPasswordFilter` is wide open when no password is set — so it worked on a fresh server, and the test suite (which runs without one) passed for as long as the bug existed. |
| **A stale Proton prefix synced silently** | A non-Steam AppID is recomputed when a shortcut is renamed or re-pointed; Steam then makes a fresh prefix. The old path still exists, reads fine and hashes fine — it is just not where the game writes. `doctor` now compares the two. |
| **A save folder mapped one level too deep nests on restore** | Archives store paths relative to the save root, so pulling an archive rooted at X into X/sub recreates `sub` under itself. The pull *succeeds*. `SaveDirSanity` now flags a repeated path tail. |

⚠️ **The lesson to carry:** three of these four were invisible to the test suite *by construction* —
it ran in the state where the bug could not fire (no admin password; no running daemon; a clean
`.verify/`). When a suite passes, ask what state it never puts the system in.
⚠️ **BREAKING: `savelocker daemon --lan` is REMOVED** and now exits non-zero. Remote access is an SSH
tunnel: `ssh -L 5178:localhost:5178 <user>@<host>`.
**The key-rotation follow-up is DONE** (2026-07-18): container updated, both Windows agents upgraded
to 0.2.0 and re-registered, so no machine still holds a key issued by a version that served it.

Earlier: v0.1.8 (agent UI types from the local OpenAPI); v0.1.7 fixed a v0.1.6 regression that broke
silent auto-update (`NextButtonClick` fires under `/SILENT`; see `Gotchas.md`) — **v0.1.6 must never
be hosted for auto-update**. v0.1.5 released glob depth-matching; v0.1.4 shipped 5e glob filters.
Recently-shipped work indexed in `logs/shipped-2026-07.md`.

⚠️ **Check `git tag -l` before tagging.** This file claimed v0.1.7 while v0.1.8 was already pushed,
which nearly caused a duplicate tag on 2026-07-18.

---

## Status (2026-07-18)

| Area | State |
|------|-------|
| Tray agent (WinForms + React/WebView2) | ✅ done |
| Game scanning (Steam VDF + Ludusavi) | ✅ done |
| Server (REST API, EF/SQLite, leases, conflicts) | ✅ done |
| Admin dashboard (React + Tailwind, baked into Docker) | ✅ done |
| Agent auto-update (version, silent relaunch, installer persistence) | ✅ verified on device (v0.1.2) |
| Fetch installer from GitHub — manual dashboard button | ✅ done (2026-07-11) |
| Scheduled GitHub installer auto-poll | ✅ dashboard-configurable in Agent Updates; persisted server-side and applied within a minute |
| Sync notifications (one toast + save date, not 4) | ✅ v0.1.3, verified on device |
| Per-game exclude globs + 200 MB upload cap (5e) | ✅ v0.1.4; depth-matching fix in v0.1.5 |
| CI/CD (push → Docker → GHCR; tag → GitHub Release) | ✅ done (Watchtower removed) |
| Console Help KB (8 articles, search, deep-links) | ✅ done (2026-07-11, `be54374`) |
| Save-in-use safety (settle gate before auto-push) | ✅ built and device-verified |
| Cross-OS round-trip in CI (Windows agent ↔ Linux agent) | ✅ done 2026-07-13 — byte-identical both ways |
| **Runtime: .NET 10 (LTS)** | ✅ merged 2026-07-13 (PR #2). net9 was STS, EOL 10 Nov 2026 |
| **Known vulnerabilities** | ✅ **none** — `dotnet build` reports no NU1903 (PR #3) |
| Linux agent **Phase 4** — enrollment token + policy import | ✅ done 2026-07-13 — 16/16 + 6/6 TLS (PR #4, merged) |
| Linux agent **Phase 5** — agent health reporting | ✅ done 2026-07-14 — 17/17 (PR #5, merged) |
| Linux agent **Phase 6** — hardening | ✅ done 2026-07-14 — 14/14. **Fixed a real data-loss bug** (below) |
| Agent local API + generated UI types | ✅ ASP.NET Core minimal API + OpenAPI; agent UI schemas generated from the live contract |
| **Local agent API hardening** | ✅ 2026-07-18 — 15/15 (`run-local-api-tests.ps1`, in CI). Token-auth + Host/Origin validation, no CORS, `--lan` withdrawn, machine key no longer served. `Decisions.md` §7. ✅ **fleet keys rotated 2026-07-18** |
| **unRAID server on the current image** | ✅ 2026-07-18 — container updated by the maintainer. Was flagged as possibly still running a pre-net10 image |
| **Cross-process state safety** | ✅ 2026-07-18 — 12/12 (`run-concurrency-tests.ps1`, in CI). Fixed a **self-conflict bug**: a daemon's stale `config.json` write erased another process's parent version, so the next push was rejected as a conflict. `Decisions.md` §8 |
| **Restore treats archives as hostile** | ✅ 2026-07-18 — hardening now 27 (Win) / 28 (Linux), **7 flip against pre-fix code**. Closed a **proven arbitrary-file-overwrite**: the copy pass wrote through a symlink in the save folder. Plus zip-bomb entry/byte caps. `Decisions.md` §9 |
| **Installer GUI enrollment** (Windows) | ✅ v0.1.6 built the wizard page. 🐛 **v0.1.6 broke silent auto-update** (NextButtonClick fires under /SILENT → abort). ✅ **fixed in v0.1.7** (`WizardSilent` guard + `ShouldSkipPage` for enrolled machines). ✅ **silent upgrade of an enrolled agent device-verified on v0.1.7 (2026-07-14)**, and again 0.1.8 → **0.2.0 on two machines (2026-07-18)**. ⏳ fresh-install happy-path enroll (page shows server/name, machine goes online) still unverified on device |
| **0.2.0 upgrade + re-register on device** | ✅ 2026-07-18 — two Windows agents upgraded and re-registered **from the agent window**, confirmed working by the maintainer. That exercises the whole new auth path on real hardware: WebView2 loads the injected token from `index.html` and sends it on every `/api/*` call. The local-API change is device-verified |
| **Console versioning + release notes** | ✅ 2026-07-20 — the console reports its own build and carries hand-written notes. Section below |
| **Help KB** | ✅ complete 2026-07-18 — 14 articles. `restore-safety.md` added for v0.2.0's refusal messages; Deck troubleshooting lives in `troubleshooting.md` |

Shipped-feature detail: `logs/shipped-2026-07.md` + `logs/sessions.md`. Open work: `Backlog.md`.
Full record of the .NET 10 upgrade: `logs/2026-07-13_dotnet-10-upgrade.md`.

---

## Deck path setup — shipped 2026-07-19 (all 5 backlog items)

Setting a save path on a Deck no longer requires a terminal. Three surfaces changed; detail and
rationale in `Backlog.md` under "Steam Deck onboarding UX".

- **Agent UI path browser** — `GET /api/browse?path=` (`Agent.Core/PathBrowser.cs`), **rooted** at
  `$HOME` + host Steam roots, containment checked *after* canonicalization, symlinks never followed,
  `/run/media` included so an **SD card** is reachable. `run-local-api-tests.ps1` is now **22 checks**.
- **The native dialog still wins where there is one.** The UI calls `folderPick` first: the Windows
  tray shows Explorer and the browser never appears; the Linux daemon returns null and *that* opens
  the browser. `agent-ui/` is one shared bundle — this is the seam that keeps the Deck flow off Windows.
- **Console** — per-machine path editing is now inline in the machine's row, labelled
  `Save path on <Machine>`; the `prompt()` is gone. Where an agent reported a guess the row offers
  one-click **Apply**.
- **Heartbeat carries guesses** — `AgentHeartbeat.PathCandidates`, appended + optional, into the new
  `MachineScanCandidates` table (migration `20260719190348`). The `openapi.json` diff is **+83 / −0**,
  so the either-order deploy note below still holds. A guess is deliberately **not** written to
  `MachineSavePaths`, which is pushed back to agents as authority — it would auto-apply itself.
- **`scan` offers to map** an unmapped tracked game (`--yes` / `--no-prompt`; prompting is skipped
  when stdin is redirected, so systemd and the harness never block).

⚠️ **Two things to know before continuing:**
1. **`npm run gen:api` in `agent-ui/` hardcodes :5178 — an installed agent's port.** It silently
   generated types from the *installed* v0.2.0 build. New schemas were just missing, no error.
   Generate against a dev daemon on a free port. See `Gotchas.md`.
2. **Giving `run-agent-tests.ps1` an isolated server DB *causes* 3 failures** unless you clear
   `.verify/` too. This is the documented "clear the server DB and `.verify/` **TOGETHER**" trap in
   `Gotchas.md` — its table predicts this exactly: *server DB only → "Laptop pull restores save" is
   BLOCKED, ~3 fail*, because `.verify/laptop_save` still holds the previous run's file and the pull
   correctly refuses to overwrite apparent un-pushed progress.
   - The habit of isolating `Storage__DbPath` (right for the enrollment suite) is **wrong on its own
     here** — it resets one half of a pair. Delete `.verify/` in the same breath.
   - Verified 2026-07-19: `.verify/` removed + fresh DB → **10/10**. Nothing was wrong with the code.
   - The suite does not self-clean on purpose: wiping `.verify/` alone against a persistent dev DB
     produces the *other* failure mode in that table (initial push reports CONFLICT, ~4 fail).

**Verified on a real Deck 2026-07-19.** The browser works; `/run/media` is in the root list, so SD
cards are reachable. **The D-pad does not work** — Desktop Mode maps the right stick to the cursor
and the left stick to scroll, the D-pad to neither. Point-and-click is the real interaction (the
44 px rows already assumed it), so only the KB claim was wrong, and it is corrected. Arrow keys still
work with a keyboard attached.

⚠️ **"Headless" is actively misleading in our own docs.** The maintainer's first reaction on hardware
was *"I didn't know we had a UI because we kept calling it headless"* — and `adding-games.md` did
literally say there was no UI to click. It means **no tray icon and no pop-ups**; the daemon serves
the full agent UI on :5178. Both KB articles now say so up front. Prefer "no tray, no pop-ups" over
the bare word.

---

## Console versioning + release notes — built 2026-07-20

"Is the fix deployed?" is now answerable **from the console**, and "what changed?" has somewhere to
live. Previously the only identifier was a Docker digest visible solely inside unRAID.

- **The console shares the repo's product version with the agent** — one git tag for the whole
  product, one number to compare, one changelog. `docker-publish.yml` derives it from
  `git describe`: exactly on a tag → `0.3.2`; after one → `0.3.2+5.a1b2c3d`. **The absence of the
  `+` suffix is what `isRelease` means** — the check never touches git.
- **An unstamped build reports `"dev"`, never a plausible number.** `BuildInfo.cs` reads env →
  assembly `InformationalVersion` → `"dev"`. A build that claims a version it does not have is
  worse than one that admits it has none.
- **Release notes are hand-written markdown in `web/src/releases/`**, bundled via `?raw` exactly
  like the Help KB, and **the same file is the GitHub Release body** (`release.yml` `body_path`).
  Written once, cannot drift, and because they ship inside the image the notes you read always
  describe the code serving them. Backfilled 0.3.0 and 0.3.2.
- **Deliberately no 0.3.1 notes** — the What's New list itself now carries the "tagged but never
  published" decision, instead of it living only in this file.
- **Three surfaces, because it must be easy to reference:** an always-visible version chip in the
  NavBar (click → notes; amber on a dev build; a dot until the running release's notes are opened),
  a **Console** card on Configuration placed directly above **Machines** so console and agent
  versions are compared without leaving the page, and the What's New view.
- A dev build shows a banner on What's New saying the notes are for the *previous* release — the
  notes are not a description of what is running, and should not be read as one.
- **Version skew is now surfaced** (`web/src/versionSkew.ts`), which is what makes the version
  useful rather than merely visible. Two separate faults: an agent **newer than the console** (it
  may expect endpoints this server lacks — the failure is an opaque HTTP error, which is exactly
  what made the v0.3.0 `status` 401 hard to place), and **a fleet on mixed agent versions** (which
  shows up as repeated conflicts, not as a version problem). Only the newer-than-console direction
  warns; an older agent is normal and supported per the deploy note. **A `9.9.9-ci` tarball is
  labelled TEST BUILD, not "newer"** — it sorts above every real version and would otherwise warn
  forever. Verified against a seeded 3-machine fleet; 24 logic cases checked including the
  dev-console-vs-tagged-agent ordering.

⚠️ **Two things worth carrying forward:**
1. **`docker-publish.yml` had no `fetch-depth: 0`/`fetch-tags: true`.** `git describe` fails on the
   default shallow clone. Same class as the documented MinVer-silently-stamps-0.0.0.0 trap — CI
   version derivation needs history, and it fails quietly.
2. **`passwordRequired` deliberately kept its original JSON path** when `/api/admin/status` became
   `{ passwordRequired, build }`. Six test suites, CI's wait loop and `ApiClient.PingAsync` all probe
   that route; nesting it would have broken every one of them for no gain.

✅ **The Docker build is verified** (2026-07-20, run `29777099641`, merge `cbee5f7`). The local
daemon was not running during development, so this stayed open until the first `main` push — which
has now happened and passed. The log shows the whole chain with real values: `git describe` →
`SAVELOCKER_VERSION=0.3.2+1.cbee5f7`, the `+`→`-` conversion producing the image tag
`ghcr.io/skorcherx/savelocker:0.3.2-1.cbee5f7` alongside `:latest`, and the numeric split running
inside the image as `NUMERIC="0.3.2"` with `-p:InformationalVersion=0.3.2+1.cbee5f7`.

⏳ **Still unverified: a RUNNING container serving it.** The image has the values baked in, but
nothing has started it and called `/api/admin/status`. Needs `docker compose pull && docker compose
up -d` on unRAID. **Expect an amber `v0.3.2+1.cbee5f7` and the dev-build banner — that is correct**,
since `main` sits one commit past the v0.3.2 tag. The chip only goes green on a tagged release.

---

## ▶ NEXT ACTION: **Deploy the container, then backlog item 2.1**

**v0.3.4 is released and its image is stamped correctly** (`ghcr.io/skorcherx/savelocker:latest` and
`:0.3.4`, `SAVELOCKER_VERSION=0.3.4`). ⏳ **Nobody has pulled it yet** — the console still runs the
old image, so none of Tier 0 or Tier 1 is live. Copy `/data/savelocker.db` first: the
`AddConflictDedupe` migration applies on start and rolling back to 0.3.3 is not supported.

⚠️ **v0.3.4 needed a manual image re-run** — `docker-publish.yml` did not trigger on tags, so the
release shipped stamped `0.3.3+11.9ae9307`. Fixed (`tags: ['v*']`), but **confirm on the next release
that a `docker-publish` run exists for the tag** — it is unconfirmed whether `paths-ignore` also
filters tag pushes. `Gotchas.md` has the symptom and the manual recovery.

✅ **Tiers 0 and 1 are COMPLETE** (2026-07-23). The 2026-07-22 incident is fully remediated: the
self-conflict loop, the 75-row explosion, the unbounded storage, and every console surface that
misled during recovery. **2.1 (per-game conflict policy) is the highest remaining value** — a
`NewestWins` option would have made the whole incident a non-event.

⚠️ **NOTHING from 0.1, 0.2, 0.4 or Tier 1 is deployed.** All of it is server-side; the console ships
inside the server image. The unRAID container needs `docker compose pull && docker compose up -d`.
0.1 carries a **migration** (`20260723220958_AddConflictDedupe`) that applies on container start.
Only **0.0** reached the fleet, via the v0.3.3 agent release.

### ✅ Tier 1 is DONE (2026-07-23) — the console no longer needs a shell

- **1.1** every conflict rendered, newest-active first (was `.find()` over an oldest-first list).
- **1.2** *display half*: machine, time and size per option; adaptive `fmtSize` (fixed-MB showed every
  small save as "0.00 MB"). ⏳ **"Keep both" deferred** — needs a `Protected` flag on `SaveVersion`.
- **1.3** confirmation naming the unexpected consequence: *N newer saves stop being what machines pull*.
- **1.4** `POST /games/{id}/prune` + **Prune now**, reusing the upload path's own prune.
- **1.5** admin version download + per-row button. ⚠️ The route checks the version belongs to the game.
- **1.6** a `sync.conflict` event shows **Resolve**, not Dismiss — it is the one event that never
  self-heals. Client-side only; `HealthService.DismissAsync` is unchanged.

`run-agent-tests.ps1` is now **35** checks (was 20 this morning).

### ✅ 0.1 + 0.2 are DONE (2026-07-23)

Conflicts fold into one row per (game, head, machine) — **75 → 1** — carrying `Count`, `LastSeen` and
the stuck `MachineId`, and offering the **newest** divergent save rather than the oldest. Retention
now runs on the conflict path and on resolution, so a conflicted game no longer grows without bound.

⚠️ **Ordering trap the test caught and review did not:** resolution unpins a conflict's versions, so
pruning *before* `QueueResolutionPullsAsync` deletes the losing version that 0.4 reads to decide which
machines to notify — leaving the loser stuck. **Queue the pulls, then prune.** Commented at the site.

### ✅ 0.4 is DONE (2026-07-23) — released nowhere yet

`ResolveConflictAsync` enqueues a **guarded** pull for **both** machines in the conflict, so resolving
in the console actually reaches the fleet. The backlog entry said "the machine whose version lost";
that was wrong — the **winner** is stuck too, because its pointer still names the parent it presented
even though its content already matches the head. That is what stranded the maintainer on round 2.

⚠️ **Server-side change, so this one needs a container redeploy** (`docker compose pull && docker
compose up -d`) — unlike 0.0, which was agent-only.

### ✅ Task 0.0 is DONE and device-verified (2026-07-23)

The daemon no longer pushes from state the launch wrapper superseded. Verified on the Deck: 4 saves
through the real Steam launch path, **zero conflicts**, where every prior session conflicted on every
save. Record: `logs/2026-07-23_agent-stale-parent.md`. Locked decision: `Decisions.md` §10.

- **Fix A** — `SyncEngine` refreshes the parent from disk inside the per-game lock before push/pull.
- **Fix B** — `AgentConfig.Save()` can no longer write per-game sync state at all. All 17 of its
  callers write settings or the game list; `SaveGameSyncState` is the only writer. `CommandPoller`
  needed no edit.
- `run-concurrency-tests.ps1` is now **17 checks** (was 12); 6 and 7 proven to fail pre-fix.

⚠️ **The Deck's daemon is RUNNING again** — the `systemctl --user stop` workaround is retired.
⚠️ **This is released nowhere.** The Deck runs a hand-built `9.9.9-ci` test tarball, and **the Windows
agents still have the bug** (the tray is a long-lived host too). Ship it in the next release.

### The 2026-07-22 Octopath conflict storm — what happened

One play session produced **75 open conflicts and 2.66 GB on a game configured to retain 5**, and
escaping it required `curl` against the admin API. The full plan is `Backlog.md` → "🔴 ACTIVE —
conflict handling"; the narrative is `logs/2026-07-23_conflict-storm.md`.

**The server was correct on every request.** The agent misreported its parent version and the console
had no way to see or fix the result. Four defects, each hidden behind the previous one:

| # | Symptom | Cause |
|---|---|---|
| 1 | 75 conflicts, console offered one | `ConflictFlag` inserted per divergent push; console `.find()`s the **oldest** |
| 2 | 2.66 GB on retain-5 | prune unreachable while conflicted; every version pinned by an open conflict |
| 3 | Resolved → conflicted again | console resolution never tells the agent; its parent stays stale |
| 4 | Pulled → **still** conflicted every save | daemon never re-reads `config.json` (item 0.0) |

⚠️ **Start at 0.0, not 0.1.** Items 1–3 are consequences; #4 is the cause, and it stayed invisible
until the others were worked around. Details in `Gotchas.md` (three new entries).

⚠️ **Two traps this cost real time on, both now in `Gotchas.md`:** a *force*-push prunes nothing when
content is unchanged (`NoChange` returns above the prune call), and the **console's Pull button sends
`force: true`** while the CLI's `savelocker pull` is guarded — use the CLI to repair a stale parent.

Everything else that was queued is done: the three security-hardening items shipped in v0.2.0, the
container is updated, the fleet is rotated, and the Help KB is complete (below).

**The Help KB is DONE (2026-07-18).** ⚠️ Do not restart it from `CONTEXT.md`/`Backlog.md` history —
both files spent days pointing at `tasks/linux-kb-articles.md`, which had *already been deleted*, and
listing articles that had *already shipped*. On checking: `deck-supported-games` was written and
registered; `deck-troubleshooting` was folded into `troubleshooting.md` (the task file explicitly
allowed that); all four §4 edits were applied. The one real gap was newer than the task file —
v0.2.0's restore-refusal messages were undocumented — and is now `restore-safety.md`.
**Lesson: check the filesystem before trusting a task pointer in this vault.**

---

## Then: **Finish device-verifying the fresh-install enroll path**

Requires a machine where `%PROGRAMDATA%\SaveLocker` does **not** exist — i.e. not the maintainer's
daily driver. The *upgrade* path is well verified (0.1.8 → 0.2.0 on two machines, 2026-07-18); it is
the **fresh install** that has never been exercised (scenarios in the archived task
`logs/2026-07-14_installer-enrollment.md`):

- **Happy path:** on a machine where `%PROGRAMDATA%\SaveLocker` does *not* exist, run the installer,
  choose an enrollment file → the page shows the right server + machine name → install → the machine
  appears in **Configuration → Machines**, **online**, with its version.
- ⚠️ **The ACL trap** (verify right after the happy path): `icacls "%PROGRAMDATA%\SaveLocker"` — the
  interactive user needs **Modify**. The enroll runs via `ExecAsOriginalUser` precisely so the config
  dir is created de-elevated by the user that later rewrites it.
- Also: expired-token (page says so, install still succeeds), skip path (installs unenrolled), and the
  unattended switch `Setup.exe /SILENT /ENROLL="C:\path\policy.json"`.

Also unverified on a device: the **agent window itself** since the local-API auth change. The token is
injected into `index.html` and WebView2 must send it back on every `/api/*` call. It is proven in
tests and in a real browser, but nobody has confirmed the WinForms/WebView2 window renders and
functions on a device post-0.2.0 (the two re-registrations may have been done via the CLI).

**Do not host v0.1.6 for auto-update** — it aborts under /SILENT. Host **v0.2.0** or newer.

### Steam Deck hardware — NOW AVAILABLE (2026-07-19)

The maintainer now owns a Steam Deck. The long-standing "no hardware" blocker is resolved.
gamescope / Game Mode, the immutable rootfs, SD-card library paths, and suspend/resume **can now
be tested on real hardware**. KB claims about Game Mode and the Launch Options UI should be
validated on-device and updated from observation rather than documentation. Linux auto-update
(previously deferred partly for lack of hardware) is now unblocked on the hardware side.

Other open work is in `Backlog.md` — fresh installer-enrollment verification, code-signing the exe,
and deploying the net10 server to unRAID.

### 🐛 Phase 6 fixed a REAL data-loss bug (2026-07-14) — worth knowing about

`Directory.EnumerateFiles(..., AllDirectories)` **follows symlinks**, and a Wine prefix is full of
them. The archive leak was the *lesser* half. The dangerous half: **`RestoreArchive` deletes target
files that are absent from the archive**, so walking through a link it **deleted files outside the
save folder entirely.** The pre-fix harness run reproduced both for real. **Windows was affected too**,
via junctions.

- Fixed with a no-follow walk (`SaveArchive.EnumerateFilesNoFollow`): links are never archived, never restored, never deleted.
- ⚠️ **Do not "simplify" the link test to `FileAttributes.ReparsePoint`.** OneDrive files-on-demand placeholders are *also* reparse points — that version silently stops archiving every OneDrive save. It keys on `FileSystemInfo.LinkTarget`, which is non-null only for symlinks and junctions. See `Gotchas.md`.
- Also landed: `SaveDirSanity` (names a Wine prefix mistaken for a save folder, surfaced by `doctor`), a proven zip-slip rejection, and a **monotonic** settle gate — wall-clock counted suspended hours as elapsed, so a suspended Deck woke up and published a possibly mid-flush save.

### Phase 5, shipped 2026-07-14 — how a Deck's failures reach you

**The console is the Deck's UI** (`Decisions.md` §2). A headless box cannot toast, so the agent
reports to the server and the dashboard surfaces it: a **problem badge** in the NavBar (absent when
the fleet is healthy) opening a list of events with Dismiss, plus per-machine health on the Machines
card — online / offline / **never reported**, agent version, platform, last sync, unmapped games,
queued pushes.

- **Scope, and it matters:** the server already knows what happens *server-side* (a conflict is a `ConflictFlag` the moment the upload lands). Agents report only what the server **cannot infer** — blocked pull, missing save folder, rejected upload, settle timeout, unreachable server. The conflict event exists solely to name **which machine is stuck**.
- **Events deduplicate** on (machine, game, code) while open — a persistent fault bumps a count, it does not write a row every 20 s. A game that **syncs cleanly auto-closes** that machine's events for it, so a Deck that recovers leaves no stale alarm.
- **Pending events persist to disk**, because the most important thing to report — "I cannot reach the server" — happens precisely when reporting is impossible. It is delivered on the first contact after the network returns.
- **Every sync path reports**, not just the daemon: the launch wrapper (`ProtonRun`) *is* the Deck's Proton sync path and has no poller, so it flushes before exiting; one-shot `push`/`pull` do too.
- The Windows tray **also** reports (it toasts *and* reports), so the console is one honest view of the whole fleet.

### Phase 4, shipped 2026-07-13 — how enrollment works now

A machine is set up with **one file and one command**, and **no API key is ever copied by hand**:
Console → *Configuration → Enroll a machine* mints a **single-use, 15-min token** wrapped in a
policy file (server URL + games + settle delay); the agent runs `enroll --file <policy>` and trades
the token for its own machine key. The token's **raw value exists only in that one download** — the
server stores a hash.

- **Unsigned, on purpose** (`Decisions.md` §4) — the threat is a *malicious server URL*, not a bogus token, and a fresh agent has no trust anchor to check a signature against. Do not "fix" this with a PKI.
- **TOFU pin:** the agent pins the server's TLS public key at enrollment and **warns (never blocks)** if it changes — a hard fail would take a headless Deck offline on a routine cert renewal. `trust` shows it; `trust --accept` re-pins.
- A token minted **for a machine name binds it** — `--name` cannot override it. Redeeming an existing name **rotates** that machine's key: that is the re-enrollment path for a wiped device.
- `enroll` lives in **`Agent.Core`**, so Windows and Linux run the same implementation.

---

## Open

- **Code-sign the exe** — SmartScreen warns for unsigned installers. Explicitly deferred by the maintainer.
- ⚠️ **`%PROGRAMDATA%\SaveLocker` ACLs on a multi-user Windows box.** v0.2.0 added `api-token` (the local-API secret) to that directory. **Not a new exposure** — `config.json`'s machine key already sat there under the same ACL — but both are readable by other local users on a shared machine. `Backlog.md`, medium priority.
- **Deploy note for next time:** old agents are wire-compatible with the current server (v0.1.8 ↔ v0.2.0 changed **nothing** in `Contracts.cs`, `src/Server/` or `ApiClient.cs`), so the container and the fleet can be upgraded in either order and independently.

**Gotcha surfaced 2026-07-12:** with two agents, saves diverge → dashboard conflict when the pushing machine's known head ≠ current server head (another machine advanced it). A "behind" machine keeps conflicting until resolved (dashboard resolve → pull, or tray Force Pull); the agent doesn't auto-advance its parent on conflict. Version/glob skew between agents guarantees this — keep both agents identical. (This is the seed for the Help KB "Understanding conflicts" article.)

**🐛 Real bug fixed 2026-07-13 (found by the Phase 3 cross-OS test):** `ArchiveStore` persisted the archive's store path into the DB using `Path.Combine`, so a **Windows-hosted server wrote `gameid\versionid.zip`**. On Linux a backslash is a filename character, not a separator — the archive becomes unreachable, `/download` 404s, and the agent reports **"server has no saves yet"** while `status` still shows a head. Production is fine (server only runs in Docker/Linux) but **the dev workflow runs the server on Windows**, so a DB or backup carried from a dev box to Docker has unreachable archives. Fixed: canonical `/` on write, either separator tolerated on read. Rule: `Path.Combine` is for *this* machine *now* — anything persisted gets a `/`. See `Gotchas.md`.

See `Backlog.md` for the full list.

---

## Dev quick-reference

| Task | Command |
|------|---------|
| Run server | `cd src/Server && dotnet run` → http://localhost:5179 |
| Run dashboard | `cd web && npm run dev` → http://localhost:5173 |
| Build agent | `dotnet build src/Agent/SaveLocker.Agent.csproj --no-incremental` |
| Build installer | `.\installer\build-installer.ps1` |
| Run tests (Windows) | `.\tests\run-agent-tests.ps1` (server must be on :5179) |
| Run tests (Linux) | `pwsh tests/run-agent-tests.ps1` — same script, drives the Linux agent |
| Enrollment tests | `.\tests\run-enrollment-tests.ps1` (16 checks; needs :5179). Run it **after** the agent suite — it adds a game + machine to the DB |
| Agent integration | `.\tests\run-agent-tests.ps1` (**20** on Windows / 15 on Linux). Needs a server on :5179 — and `.verify/` cleared **in the same breath**, see `Gotchas.md` |
| Local agent API security | `.\tests\run-local-api-tests.ps1` (**22** checks). Starts its own daemon on **:5188** via `daemon --port`, so it never collides with a real agent on :5178. Needs nothing running. Now also proves the path browser is **rooted** — outside `$HOME`/Steam is refused, including via `..` and via a symlink that lives *inside* `$HOME` |
| Cross-process state | `.\tests\run-concurrency-tests.ps1` (12 checks; own server on **:5183**, own daemon on **:5189**). Daemon vs. a second process over `config.json`, the offline queue and health events. Verified to FAIL against pre-fix code |

**WSL is a working test bed — use it.** Ubuntu 24.04 is provisioned (see Toolchain below) with a clone at
`~/SaveLocker`. `dotnet` and `pwsh` are **not on a non-interactive PATH**, which makes them look absent;
export first. This is how to run the Linux-only harness and to exercise real `flock` and `0600` semantics
that Windows cannot show you:
```sh
wsl -d Ubuntu-24.04
export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
cd ~/SaveLocker && git fetch /mnt/e/Projects/SaveLocker <branch> && git reset --hard FETCH_HEAD
cp -r /mnt/e/Projects/SaveLocker/agent-ui/dist/. agent-ui/dist/   # avoids the Windows-npm-on-PATH trap
dotnet build src/Server/SaveLocker.Server.csproj src/Agent.Linux/SaveLocker.Agent.Linux.csproj --no-incremental
bash tests/linux/run-linux-tests.sh          # 33 checks
```
⚠️ Suites that need a server on :5179 must be given **isolated `Storage__DbPath` / `Storage__ArchiveRoot`**.
Reusing a dirty dev DB fails 12/16 of the enrollment suite for reasons that look exactly like a code regression.
| Health tests | `.\tests\run-health-tests.ps1` (17 checks). **Starts and stops its own server on :5181** — it has to, since one check pushes while the server is *down*. Needs nothing running |
| Hardening tests | `.\tests\run-hardening-tests.ps1` (14 on Linux / 13 on Windows; own server on :5182). Security: symlink escape on archive **and on restore-delete**, zip-slip. Windows uses junctions (no elevation); Linux uses symlinks |
| TOFU pin tests (TLS) | `.\tests\run-enrollment-tls-tests.ps1` (6 checks; starts its own HTTPS server on :5443). Needs `dotnet dev-certs https --trust` — local only, not in CI |
| Linux fake-game harness | `tests/linux/run-linux-tests.sh` (27 checks; starts its own server) |
| Cross-OS round-trip | `tests/cross-os/crossos.ps1 -Leg author\|roundtrip\|confirm` — one leg per OS; CI chains them by passing the server's state as an artifact |
| Password-hash compat | `.\tests\verify-password-compat.ps1` — builds a server from an older git ref, has it hash an admin password, then asserts the current code still verifies it |

**Always use `--no-incremental` for server builds** — stale DLL reuse has masked changes before. Stop the running agent/server first (they lock the DLLs).

**CI (`ci.yml`) runs 8 jobs on every PR:** `build-dotnet`, `build-web`, `build-agent-ui`, `docker-build` (builds the server image — publishes nothing), `agent-tests-linux` (agent **+ enrollment + health + hardening**), and the chained `crossos-author → crossos-roundtrip → crossos-confirm`. The cross-OS chain is the one that matters: it hands the **server's own state** (SQLite DB + archive store) between a Windows and an Ubuntu runner as an artifact, because runners cannot share a network.

### Toolchain (installed 2026-07-13 — a fresh session does not need to redo this)

| Where | What |
|------|------|
| Windows | .NET SDK **9.0.315 + 10.0.301** side by side. `global.json` pins **10.0.x** — that is why `dotnet --version` at the repo root says 10.0.301 and never silently picks another. Only **Windows PowerShell 5.1** (no `pwsh`), so scripts here must stay 5.1-compatible (no `??`, no `$IsWindows`, and a BOM-less `.ps1` is read as **ANSI** — keep them ASCII). |
| WSL (Ubuntu 24.04) | .NET SDK **9.0.315 + 10.0.301** in `~/.dotnet`; **pwsh 7.4.6** in `~/.local/pwsh` (symlinked to `~/.local/bin/pwsh`); **Node 22 via nvm** (`. ~/.nvm/nvm.sh`). Repo clone at `~/SaveLocker` on **ext4** — never build or test from `/mnt/*`. |

⚠️ **Inside WSL, `npm` resolves to the WINDOWS npm** on the shared PATH unless a Linux node is put first — the symptom is the baffling `error TS5083: Cannot read file 'C:/Windows/tsconfig.json'`. Source nvm and prepend it before running anything that shells out to npm (e.g. `packaging/linux/build-linux.sh`).

---

## Deployment
- **unRAID:** Docker on port 5080. `git push main` → Actions build → GHCR. To deploy: `docker compose pull && docker compose up -d`.
- **Tag a release:** ⚠️ **write `web/src/releases/<ver>.md` FIRST and commit it before tagging.**
  That one file is both the console's What's New entry and the GitHub Release body; `release.yml`
  reads it by path at tag time, so a tag pushed without it falls back to GitHub's generated commit
  list (a `::warning::` in the run, not a failure). Add the new entry to `web/src/releases/index.ts`
  in the same commit — the file is not picked up automatically.
  Then `git tag v0.2.0 && git push origin v0.2.0` → `release.yml` builds **both** agents → GitHub Release:
  - **Windows:** `SaveLocker-Agent-Setup-<ver>.exe` (Inno Setup, built on `windows-latest`).
  - **Linux / Steam Deck:** `savelocker-<ver>-linux-x64.tar.gz` (self-contained, built on **`ubuntu-latest`** — see below).

### How a Steam Deck user installs the agent
```
tar -xzf savelocker-<ver>-linux-x64.tar.gz
./SaveLocker/install.sh
savelocker enroll --file <policy.json>      # from the console: Configuration → Enroll a machine
savelocker doctor
```
Installs to `~/.local/share/SaveLocker`, symlinks `~/.local/bin/savelocker`, enables a `systemd --user`
unit. **Never `/usr`** — SteamOS's rootfs is immutable and wiped on update (`Decisions.md` §5).

- ⚠️ **The Linux tarball MUST be built on `ubuntu-latest`.** A self-contained .NET binary binds to the **build host's glibc**, and an older-glibc build runs on newer systems but *never the reverse*. Ubuntu 24.04 (glibc 2.39) is older than SteamOS's rolling Arch, so Ubuntu → Deck is forward-compatible. Build it on anything newer and users get `GLIBC_2.4x not found` — an error you cannot reproduce on the machine that built it.
- CI's **`package-linux`** job builds the tarball on every PR and *installs it into a throwaway HOME*, so packaging cannot rot silently between releases (it is otherwise only exercised on a tag — i.e. too late).
- **That tarball is now kept as a run artifact** (`savelocker-linux-x64-<sha>`, 14 days), so **you can put any commit on a real Deck without tagging**. Download it from the run's Artifacts, then
  `tar -xzf savelocker-9.9.9-ci-linux-x64.tar.gz && ./SaveLocker/install.sh`. It is stamped
  **9.9.9-ci** on purpose — the agent reports its version in every heartbeat, so a test build must be
  impossible to mistake for a release in the console. Before this, hand-building one in WSL was the
  only way to get code onto hardware, which is precisely why Deck-only paths stayed unverified.
- **Linux has no auto-update** (deliberate, not shipped). The update channel is installer-shaped and Windows-only; a Deck user re-runs `install.sh` from a newer tarball. See `Backlog.md`.
  - ⚠️ **Before v0.3.0 that upgrade path was broken** and looked like it worked: the agent was killed
    by SIGBUS mid-upgrade while the script printed "Installed." and exited 0. **Upgrading *to* v0.3.0
    from v0.2.0 still runs the OLD install.sh**, so stop the agent first:
    `systemctl --user stop savelocker.service`. From v0.3.0 onward the script handles it.

---

## Critical gotchas (read before touching builds or paths)
- Incremental builds can silently reuse stale DLLs → always `--no-incremental`
- Stop running agent/server before building (DLL file-lock)
- Dev storage uses `localstate/` not `data/` (Windows case-collision: `Data/` = source folder)
- `dotnet` may not be on PATH in an open shell after winget install — open a new shell
- OneDrive save paths block `Directory.Move` — RestoreArchive uses file-by-file copy to `_tempDir`
- PowerShell array splatting to native exes splits strings containing `:` character-by-character — always use `if/else` + `"--property:Key=Value"` long-form
- MinVer requires git access; silently fails to `0.0.0.0` on GitHub Actions Windows runners. CI must pass `--property:Version=$v --property:AssemblyVersion=$v` explicitly from `github.ref_name`
- See `Gotchas.md` for the full list with fixes

---

## Key files
| Topic | File |
|-------|------|
| Codebase map | `REPO_MAP.md` |
| System design | `Architecture.md` |
| Locked decisions | `Decisions.md` |
| Known traps | `Gotchas.md` |
| REST endpoints | `API Reference.md` |
| Dev build & run | `Build and Run.md` |
| Agent CLI | `web/src/help/cli-reference.md` (KB article) |
| Active backlog | `Backlog.md` |
| Session history | `logs/sessions.md` |
