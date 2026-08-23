# SaveLocker — Session Context

Read this and [[REPO_MAP]] at the start of every session. This file is a **short orientation** —
where the project is right now, and where to go next. Detail belongs in the files it links to; if a
section here starts growing, move it and leave a link.

**What:** Self-hosted Windows/Linux game save sync. Hub-and-spoke: a tray/headless agent on each PC
or Steam Deck syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard +
embedded React agent UI + a gamepad-native Deck Game Mode UI. See [[Architecture]].

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** `main`

**Released:** **v0.5.8** (tagged 2026-08-16) — notes in `web/src/releases/0.5.8.md`. **Not rolled
out**: see Next action. It is agent-side only — "Install update now" and the words around it.

v0.5.7's rollout (2026-08-15) is complete: console redeployed, the Windows agent took it from the
tray's *Check for updates*, and **the Deck updated itself** — see below, it is the first time that
has happened. The **decky-plugin** row of Config → Agent updates is still **empty**, so no Deck is
offered a plugin update — the release to put in it exists (v0.2.1, below) and uploading it is Next
action 2. GHCR package `savelocker` is **public** ([[Gotchas]]).

**Decky plugin:** its own repo, <https://github.com/SkorcherX/SaveLocker-Decky>, at **v0.2.1**
(tagged 2026-08-15 — the first plugin release cut since the agent gained the ability to install one).
Not on the Decky store and cannot honestly be submitted — see `logs/2026-08-15_decky-plugin.md`.
Since Phase 5 (below) the **agent keeps it updated**, so the custom-store setting is no longer the
only route to an update.

**Console UI/audit cleanup (2026-08-16, this session, on `main` but NOT in v0.5.8 — that was already
tagged).** Four small tasks, one commit each, all with a real bug caught during manual browser
verification and fixed before shipping — write-ups in `logs/2026-08-16_*.md`:
- NavBar button sizing + Refresh icon-only, Info-severity agent events no longer read as
  "problems" (`75d2df7`, `ab9d9da` for the follow-up checkbox-color fix).
- Audit Log gained CSV export, and three new audited events: agent/plugin package updates,
  human-readable conflict-resolution detail (which machine's version won/lost), and exclude-glob
  diffs (`de6c7a4`, `6d594e3`).
- Agent Updates card is now read-only status + a single Edit modal (bulk "fetch all", per-package
  controls, GitHub `SHA256SUMS*.txt` hash verification) — `5672547`.
- The auto-fetch interval became a real schedule: disabled/hours/weekly/monthly, with the
  weekly/monthly modes computing their next occurrence server-side
  (`AutoFetchScheduler.ComputeNextRun`, tested by `tests/run-schedule-tests.ps1`) — `290ca2c`.
<br>**Not yet in a tagged release** — next release's notes need to cover all four. `AutoFetchSchedule`
replaces the old `SetAutoFetchHoursRequest` on the wire (`/api/settings/agent-update-auto-fetch`'s
body shape changed), and `AgentInstallerStatus` gained a `Source` field — both are additive/back-
compat server-side (old DB rows with no schedule read as `mode: "hours"`, exactly as before), but any
external script POSTing the old `{ hours }` shape to that endpoint needs updating.

**`testenv` gained a Steam Deck target and a real `clean`, and both are now proven on real
hardware (2026-08-17–19, across sessions).** Asked for directly: push a test build to the real Deck
beside the installed release agent, the way the rig already does for Windows and WSL, and be able to
delete every test build/state it leaves behind — not just stop the processes, which `down` already
did. `tests/testenv.ps1 build -Only deck` cross-publishes a self-contained linux-x64 tarball in WSL
(`packaging/linux/build-linux.sh`, same packer a real release uses); `up` `scp`s it to
`~/savelocker-test` on the Deck (state in the sibling `~/savelocker-test-state`, never
`~/.local/share/SaveLocker`) over SSH, gated by `$env:SAVELOCKER_DECK_HOST` /
`$env:SAVELOCKER_DECK_SERVER_URL` — unset skips the Deck silently. Editing those two settings got
its own command, `deck-config`, writing a gitignored `tests/testenv.local.ps1` that's loaded
automatically (a DHCP-leased Deck IP moves; retyping an env var every shell was the wrong friction).
`clean` stops everything first and then deletes it: Windows' `-StateRoot`, the WSL rig's state dir,
the Deck's deployed build+state, and the dashboard container **and its image and data volume** (a
plain `down` left all three of those behind). Design detail: *Throwaway test rig* in
[[Build and Run]] and *`tests/testenv.ps1`* in [[Gotchas]].
<br>**The first real `up` against hardware (2026-08-19) found three genuine bugs, not zero** — worth
internalising as a reminder that "the self-contained publish works" is not the same claim as "the
rig works": a fresh worktree's `agent-ui` had never had `npm install` run, which the Windows build
needs internally but never checked for; both bash scripts' `cmd_up` read the daemon's auth token
exactly once, racing the daemon still writing it; and the Deck's daemon — this is the one that would
have made the whole feature useless in practice — **did not survive its own SSH session closing**,
because `setsid`/`disown` guard against a different mechanism than the one that killed it
(`logind`'s `KillUserProcesses` tracks by cgroup, not POSIX session ID). Fixed with
`systemd-run --user --scope`, confirmed by starting a throwaway process, closing the SSH connection,
and finding it still running from a second one. All four commands (`build`/`up`/`down`/`clean`) now
verified working end-to-end against the real Deck, the real WSL clone, and Docker, with the real
installed Deck agent (pid confirmed unchanged throughout) and its config never touched. Full
write-up of all three: [[Gotchas]] → *`tests/testenv.ps1`*.

---

## Where things stand

**Shipped in v0.5.8: "Install update now"** (`logs/2026-08-15_install-update-now.md`, all three
phases). `/api/agent-version` now distinguishes **staged** (downloaded,
SHA-256-verified, smoke-tested, applying it is a file copy that works offline) from merely
**available** (the server is offering something newer; taking it needs a download that can fail) —
they were one field and only the first may be offered as an instant install. It also publishes
`stagedBlockedReason`, a finished sentence naming the game whose being open defers the swap, because
restarting then **succeeds and changes nothing**, which is the worst thing a button can do.
Game Mode's dead-end *"Nothing to do."* is replaced by an instruction probed for this device: the
Desktop-mode switch only cycles the unit when lingering is **off**, and the KB recommends
`enable-linger` in three places. `run-linux-tests` 208 → **216/216**; the new checks were confirmed
to fail against deliberately broken builds.

**The plugin half shipped first, as SaveLocker-Decky v0.2.1** (2026-08-16, before the agent half was
tagged). Its Update section reads `stagedVersion`, so it shows nothing until a v0.5.8 agent is
actually installed — which is why cutting it first was safe, and it gives Phase 5's never-exercised
plugin update channel something harmless to carry. **Nothing in either half has run on hardware**, and
the one genuine unknown is whether Decky's backend gets a usable `systemctl --user`.

Everything else below is released and rolled out.

**v0.5.6 (2026-08-15)** carries a **save-losing fix** found on real hardware. A tracked game with no
recorded `SteamAppId` matched nothing in the launch wrapper, so it played with **no sync at all** and
said so only to `agent.log` — two of four games on the maintainer's Deck were in that state, and a
Khazan session on 2026-08-11 was never pushed. A Proton prefix is named for the AppID that launches
into it, so the save path already carried the answer; the agent now resolves it everywhere and the
daemon records it (`b31e160`). Also in the release: the launch-option rule and API (`logs/2026-08-15_decky-plugin.md`
Phases 1–2), and `doctor` reporting whether a game's launch options were ever set.

**Phase 5 (2026-08-15, this session)** closes the plugin's update story. Decky holds exactly one
custom-store URL and it *replaces* the official store while set, so nobody leaves it there and in
practice nobody was ever prompted about a plugin update. The agent does it instead, through the
channel it already uses for its own updates: the server hosts the plugin zip in a **third installer
slot** (`?platform=decky-plugin`, from the plugin's own repo — the slot carries its own GitHub repo)
and the Linux agent replaces the files under `~/homebrew/plugins/SaveLocker`, which Decky hot-reloads
within a second. It refuses rather than half-installs: every destination is proven writable before a
byte is written, because the plugin directory is root-owned 755 and `plugin.json` is root's outright.
`run-linux-tests` 161 → **197/197**. **It has not run on hardware** — see Next action.

**The Decky plugin is done and working on hardware** — Phases 1–4. It sets Steam launch options
(which the agent *cannot*: Steam rewrites its own config on exit), and its Quick Access panel shows
lease warnings, sync status, push/pull per game or all, and `doctor` on demand. Phases 1–2 are in the
agent and covered by `run-linux-tests`; 3–4 are the plugin and were verified on the Deck.

**Proven on hardware by the v0.5.7 rollout (2026-08-15) — two things that had never been seen.**
The Deck **staged and applied an update entirely by itself**: it noticed v0.5.7, downloaded and
verified it, and installed it at the next start with nobody typing anything. Everything the feature
needed had been in place since v0.5.5 and every piece was proven separately, but the unattended run
had never been watched, and it was the standing caveat in v0.5.7's release notes. The **Game Mode
"update is ready" notice** (`Ui/UiApp.cs`) was seen for the first time in the same pass. The trigger
was a plain **reboot** — chosen over Desktop mode precisely because it needs no terminal, which is
the argument `logs/2026-08-15_install-update-now.md` is built on.

Still unseen on hardware: the **agent UI's Updates panel**, everything about the plugin updating
itself (nothing has been uploaded to the decky-plugin row yet), and all of "Install update now" —
in particular **whether Decky's backend has a usable `systemctl --user`**, which is the one genuine
unknown in that feature and the thing to watch on the first press.

Everything shipped before this: `logs/sessions.md` (reverse-chronological) and
`logs/shipped-2026-07.md`.

## Next action

1. **Roll out v0.5.8.** Redeploy the console (`docker compose pull && docker compose up -d`), then
   upload **both** agent packages in Config → Agent updates — the Windows installer and the Linux
   tarball — because neither fleet is offered anything until those rows are filled ([[Build and Run]]
   → *Rolling a release out*). The Deck should then update itself again, unattended, which is now the
   second time that has been watched rather than the first.
2. **Prove Phase 5 on the Deck — the release to carry it now exists.** The harness covers the agent's
   half with a fake plugin directory; what it cannot cover is the part that makes the feature work at
   all — Decky noticing the files change and reloading. That mechanism *was* observed by hand during
   Phase 4 (`touch` as the desktop user, and repeated `scp`s of real builds), so this is not a guess,
   but the agent doing it by itself has never happened on hardware, and neither has the server
   hosting a real plugin zip. **Upload SaveLocker-Decky v0.2.1's `SaveLocker.zip` in Config → Agent
   updates → Decky plugin** (type the version — Decky's artifact is always `SaveLocker.zip`) and
   watch the Deck pick it up. The Deck is on plugin v0.2.0, so it is genuinely behind. A manual
   reinstall through Decky always works, and is what the refusal path tells the user to do. Details
   and the two deviations from the plan: `logs/2026-08-15_decky-plugin.md`.
   <br>**Do 1 before this**, or the button v0.2.1 adds stays invisible and the pass proves only half
   of what it could: the plugin reads `stagedVersion`, which only a v0.5.8 agent publishes.
3. **Whenever the next release after v0.5.8 is cut, fold in the console UI/audit cleanup above** —
   it's on `main` but landed after v0.5.8 was tagged, so its release notes say nothing about it.
3. **Check the live server for duplicate games.** Canonical naming stops *new* splits; it does not
   merge what a server already holds under two spellings, and there is no merge tool ([[Backlog]]).

Everything else — the WA-03 second-account ACL test, the remaining Windows manual gates, the LAN
enrollment-URL check, the missing LA-04/05/06/07 regression tests, and the `WA-01`/113-of-114
baseline drift — is prioritised in [[Backlog]].

## Handoff: working on the Decky plugin

Read `logs/2026-08-15_decky-plugin.md` and [[Gotchas]] → *Decky plugin* first. Both carry
things that were measured on hardware and cannot be re-derived by reading code.

**The Deck.** `deck@192.168.68.67`, key auth is set up (see the maintainer — the key is deliberately
not named here). It is awake only when the maintainer wakes it. Useful state on it:
- agent **v0.5.6 (the released build**, checksum-verified against the published `SHA256SUMS-linux.txt`),
  service `savelocker.service` active, 4 tracked games, all with launch options confirmed;
- plugin at `~/homebrew/plugins/SaveLocker`, and a config backup at
  `~/savelocker-decky-backup-20260815-112213` (`config.json`, `shortcuts.vdf`, `localconfig.vdf`);
- `~/savelocker-plugin-stage/` is the staging directory for anything needing `sudo`.

**How to iterate on the plugin without sudo** — this is also exactly the mechanism Phase 5 automates:
`scp` a new `dist/index.js` (and `main.py`) straight into `~/homebrew/plugins/SaveLocker/`, and Decky
hot-reloads within a second. Works because a non-`_root` plugin's files are chowned to the desktop
user and the plugin ships the `debug` flag. **`plugin.json` is root-owned and needs `sudo`.**

**Debugging the plugin frontend.** It logs nowhere on disk. Tunnel CEF —
`ssh -N -L 8080:127.0.0.1:8080 deck@…` — then `curl http://localhost:8080/json` for targets and drive
CDP over the websocket. **Discard the console buffer before measuring**: `Runtime.enable` replays
history, which already caused one wrong conclusion. Log state at *render* time, not in handlers.

**Plugin releases.** Tag the plugin repo; its workflow builds the zip, publishes it, and regenerates
`store/plugins` with the artifact's SHA-256 — Decky verifies that hash, so index and artifact must
come from the same job. Bump `package.json` too: Decky reads the installed version from it.

## Parked on a branch — `offline-backoff-task` (`bee3116`, pushed, not merged)

`main` carries **no** task file now. Do not conclude there is no open work. `tasks/OfflineBackoff.md`
and its Backlog entry exist only on that branch, deliberately (2026-07-29) — the task was written and
the work deferred. `git checkout offline-backoff-task` to pick it up.

An agent that cannot reach the server retries forever at a fixed cadence: the offline drainer every
30 s, *re-archiving the save folder each time before it finds out*, and the command poller every 20 s
carrying the reconcile, the command fetch and the heartbeat — roughly five failed requests a minute,
indefinitely. On a Deck away from home that is battery and metered traffic spent on nothing.
**Backing off only the drainer does not achieve the goal**, which is why the task covers all three
loops.

---

## Where to look

| Topic | File |
|-------|------|
| Codebase map | [[REPO_MAP]] |
| System design | [[Architecture]] |
| Locked decisions (don't re-litigate) | [[Decisions]] |
| Recurring traps — **read before any build, test or path work** | [[Gotchas]] |
| Dev build/run, deploy, test suites + baselines | [[Build and Run]] |
| REST endpoints | [[API Reference]] |
| Agent CLI | `web/src/help/cli-reference.md` (KB article) |
| Prioritised open work | [[Backlog]] |
| Shipped release notes | `web/src/releases/*.md` (the console page *and* the GitHub Release body) |
| Session history | `logs/sessions.md` |
| Write-ups | `logs/2026-08-14_steam-cloud-from-manifest.md` · `logs/2026-08-09_heroic-detection.md` · `logs/2026-07-29_winagent-bugbounty.md` · `logs/2026-07-29_linuxagent-bugbounty.md` · `logs/2026-07-27_console-bugbounty.md` |
