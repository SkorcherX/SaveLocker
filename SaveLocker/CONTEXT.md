# SaveLocker — Session Context

Read this and [[REPO_MAP]] at the start of every session. This file is a **short orientation** —
where the project is right now, and where to go next. Detail belongs in the files it links to; if a
section here starts growing, move it and leave a link.

**What:** Self-hosted Windows/Linux game save sync. Hub-and-spoke: a tray/headless agent on each PC
or Steam Deck syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard +
embedded React agent UI + a gamepad-native Deck Game Mode UI. See [[Architecture]].

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** `main`

**Released:** **v0.5.9** (tagged 2026-08-16) — notes in `web/src/releases/0.5.9.md`. Rolled out, and
**the Deck is on it**: it was applied by pressing *Install update now* in Game Mode, which is the
first time that feature has run on hardware (see below — it took a plugin fix). v0.5.8's rollout is
complete too.

The **decky-plugin** row of Config → Agent updates carries **v0.2.2**, and the Deck is on it. GHCR
package `savelocker` is **public** ([[Gotchas]]).

**Decky plugin:** its own repo, <https://github.com/SkorcherX/SaveLocker-Decky>, at **v0.2.2**
(tagged 2026-08-16 — the LD_LIBRARY_PATH fix below; v0.2.1 was the first release cut since the agent
gained the ability to install one).
Not on the Decky store and cannot honestly be submitted — see `logs/2026-08-15_decky-plugin.md`.
Since Phase 5 (below) the **agent keeps it updated**, so the custom-store setting is no longer the
only route to an update.

**Fork port, item 10 — emulator saves, Group E readers (2026-10-22, branch `port/emulator-group-e`, off
`main`, awaiting review).** The reader half of Marwanello #68 (`4290a2a`): Cemu (Wii U), Azahar (3DS, Citra's
old folders too) and Xenia (Xbox 360, both layouts, device saves as a `device` folder); names from Cemu's
title cache, the 3DS ROM's NCCH product code (No-Intro) and the Xbox 360 title ID (Redump + No-Intro), added
to the bundled title table. Agent only, no API change. **Phase 5c (Xbox 360 profile at launch, DLC check and
DLC hand-over) was left out on purpose** — see [[Backlog]]; each machine syncs a Xenia game into its own
profile's folder. Verified: Core.Tests 247 (25 new; two 5c tests left out, two rewritten for own-profile
folders), the PowerShell suites unchanged, Linux 239. **Not done:** real emulator installs. Next: Group F,
Switch (fork #73).

**Fork port, item 10 — emulator saves, Group D (2026-10-22, merged to `main` at `38dca35`).** Marwanello #65 (`ff2444a`): PPSSPP, RPCS3, Vita3K, shadPS4 — one game per game-ID folder
(`TitleFolders`), named by Redump's PSP/PS3 sets or the save's PARAM.SFO (`ParamSfo`); states-only PSP games
listed, and a states-only push never empties another machine's real save. Agent only, no server change.
Verified: Core.Tests 222 (30 new), the PowerShell suites unchanged, Linux 239. **Not done:** real emulator
installs, UI by eye. Next: Group E, title-ID folders — Cemu, Azahar, Xenia (fork #68).

**Fork port, item 10 — emulator saves, Group C (2026-10-22, merged to `main` at `d065c52`).** Marwanello #63 (`a97f507`): PCSX2, DuckStation, Dolphin, PrimeHack; a memory card every
game shares is listed but never a game (`ScanCandidate.NotSyncable`, SaveDirSanity warning); saves from
different emulators never join (`GameDto.Emulators`); console saves named by serial from a Redump-derived
table (`ConsoleTitles`, CC BY-SA 4.0) with the region beside the name — **games are now unique by (name,
region)**, migration `AddGameRelease`. Not ported: the PR's conflict-resolution change (needs the fork's
conflict UI), the release chip and other redesign UI, testenv fixtures. Verified: Core.Tests 192 (41 new),
the PowerShell suites unchanged, Linux 239. **Not done:** real emulator installs, UI by eye. Next: Group D,
Sony PARAM.SFO (fork #65).

**Fork port, item 10 — emulator saves, Group B (2026-10-22, merged to `main` at `9b66b3b`).** Marwanello #61 (`860c642`): melonDS, Supermodel, Model 2 and ScummVM on a shared
`RomSaves` reader (`EmulatorSaves.Sources`); a game is found by its files under any name (D1); Add games can
link an emulator save by hand (`GET /api/candidates/links`, `ServerGameLink`, rebuilt in the inline UI
style); EmuDeck's untouched preinstalled saves listed only when the server keeps one. No server change.
Verified: Core.Tests 151 (41 new), the PowerShell suites unchanged, Linux 239. **Not done:** real emulator
installs, the linking UI checked by eye. Next: Group C, memory cards (fork #63).

**Fork port, item 10 — emulator saves, RetroArch group (2026-10-22, merged to `main` at `b150d9d`).** Marwanello #60 (`f2b0e4b`): RetroArch `.srm` saves found in EmuDeck's
`Emulation/saves/retroarch` or a standalone `retroarch.cfg`, one game per ROM scoped to its files, plus a
`states` folder for its save states. The server decides an emulator game's name (`Enroller.ServerNameFor`),
and an unscoped game is refused when joining a scoped one would back up nothing (shown as "Not added: …").
Each machine records how it found a game: migration `AddMachineGameSources`, `PUT /api/agent/source/{gameId}`,
`GET /api/games/{id}/sources`; shown as a plain line in the console's per-machine table and the agent's
tracked-games list. Emulators filter (+ emulator row) in Add games and Game Mode. Not ported: the fork's
redesign UI, enroll progress, Playnite/MoonDeck tags, `testenv emu-fixture`. Verified: Core.Tests 110 (35
new), backups 86, multipath 39, bug bounty 194, delta 29, health 38, hardening 41, local-api 36, concurrency
23, agent 47, enrollment 18, Linux 239. **Not done:** a real EmuDeck/RetroArch run (Deck), the UI checked by
eye. Next groups (fork plan): B ROM-named saves, C memory cards, D PARAM.SFO, E title-ID folders, F Switch.

**Fork port, item 9 — multiple save folders (2026-10-21, merged to `main` at `d2a8410`).** Marwanello
#56–#58: a game can sync several folders (extra ones under `.savelocker/paths/<key>/` in the archive; the
primary stays at the root, so old versions and hashes are unchanged). Unmapped folders sync through shadow
copies; `add-path` / `remove-path`; "Also found" manifest suggestions in Add games and on Overview; an Extra
save folders section on the console's game page. New migration `AddMultipleSavePaths`. New
`tests/SaveLocker.Core.Tests` (xUnit, 75/75) and `tests/run-multipath-tests.ps1` (39/39). **Not done:** the
Deck Game Mode (ImGui) folder screens, an OS notice for suggestions (log only), the agent UI's new card
checked by eye, any Deck/real-game run.

**Fork port, round 2 (2026-10-21).** Merged to `main`: save safety (item 11, Marwanello `3f28f71`:
excluded files kept on pull, an emptied folder held until *Delete everywhere* / *Restore here*, server
409s an unforced empty upload) and duplicate-shortcut warnings (item 19, `da6d118`).
Backups + encryption at rest (item 12, backend of `82ab5c8`) are on branch `port/backups-encryption`,
rebased onto `main` after multiple save folders, awaiting review: zip backups (database + latest saves), a
UTC daily/weekly schedule, restore with a safety backup, a before-upgrade snapshot, ticketed downloads, a
plain console Backups tab, and the SteamGridDB key encrypted with Data Protection. **Deploy note: the key
ring is `/data/keys` — keep it with the database; a backup restored elsewhere reads the SteamGridDB key as
unset.** Verified after the rebase: backups 86, multipath 39, Core.Tests 75, server bug bounty 194, delta 29,
health 38, hardening 41, local-api 36, concurrency 23, enrollment 18 and agent 47 (both against a :5179
server; agent needs `.verify/` cleared), Linux 239. Remaining: 10 emulator saves.

**Fork port (2026-10-21, merged to `main` at `0552155`, not yet released).** Cherry-picked
from Marwanello/SaveLocker (`-x` keeps the source SHAs): chunked upload that is all-or-nothing, Linux/Deck
save detection plus case-insensitive Wine/Proton paths, per-file delta upload (new `SaveVersionFile`
migration), the conflict card's file-count/newest-mtime delta, and per-game alias + pull-before-launch +
HasSteamCloud. Not ported: their sync-activity UI (PR #9), their Deck testenv (#4), and everything from
the redesign onward. Verified: full build, delta upload 29/29, server bug bounty 194/194, hardening 33/33.
`run-linux-tests` 237/237 (main was 216). **Not yet:** a Deck hardware check of the detection changes.

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

**"Install update now" ran on hardware for the first time (2026-08-16) — and needed a plugin fix to
do it.** The Deck is now on **agent v0.5.9**, applied from Game Mode by pressing the button, with the
staged marker gone. The first press failed with *"savelocker.service is not installed, so there is
nothing to restart"* while that unit was present, **enabled and active**. Two bugs, both in
SaveLocker-Decky (`0b988c0`, released as **v0.2.2**):
- **Decky Loader is a PyInstaller one-file bundle**, so it puts its private `/tmp/_MEIxxxxxx` copies
  of libcrypto and friends on `LD_LIBRARY_PATH`, and **every process the plugin backend spawns
  inherits it**. `systemctl` loaded Decky's older `libcrypto.so.3` and died in the dynamic linker —
  `version 'OPENSSL_3.4.0' not found` — before running a line. `main.py`'s `_clean_env()` now
  restores `LD_LIBRARY_PATH_ORIG` (PyInstaller's own saved copy) for systemd, `doctor` and
  push/pull alike. **This hazard applies to anything a Decky plugin execs**, not just us.
- The message itself: `describeRestartFailure` matched a bare `'not found'`, which that linker error
  contains and means nothing like, so a link failure was reported as a missing install. Now anchored
  to the unit's own name, with systemctl's verbatim text as the fallback.
<br>So **the one genuine unknown named below is answered: Decky's backend does get a usable
`systemctl --user`** — once its environment is cleaned up first.

---

## Where things stand

**Security, 2026-10-21 (PR `security/emudhra-trust-refresh`):** eMudhra CA key compromise — Node pinned
to **22.23.4** (Dockerfile + CI/release) for its compromised-intermediate denylist, and the runtime image
upgrades `ca-certificates` on every build. Agents rely on the OS trust store (`ServerHttp.cs`), so Windows
Update / SteamOS updates cover them. **PHP2Shell does not apply** — no PHP anywhere in the stack.
⏳ After merge: redeploy the unRAID container so it picks up the refreshed trust store.

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

**Phase 5 is proven on hardware, and was proven earlier than this file claimed.** The Deck's
`agent.log` shows the daemon's own 6-hourly check installing the plugin unattended **twice on
2026-08-15** — `plugin check: updated v0.1.0 → v0.2.0` at 17:16 and `→ v0.2.1` at 19:59 — so the
decky-plugin row was never empty and the channel was never unexercised. Both claims sat in this file
for a day. A third update, `v0.2.1 → v0.2.2`, ran on 2026-08-16 and was watched directly: the server
hosted the zip, the agent wrote 4 files (leaving root's `plugin.json` alone) and Decky hot-reloaded
within the same second. **Lesson for this vault: "unseen on hardware" is a claim about the logs, and
the logs are on the device — check them before repeating it.**

Still unseen on hardware: the **agent UI's Updates panel**. "Install update now" and the plugin
update channel are both off this list.

Everything shipped before this: `logs/sessions.md` (reverse-chronological) and
`logs/shipped-2026-07.md`.

## Next action

1. **Whenever the next release after v0.5.9 is cut, fold in the console UI/audit cleanup above** —
   it's on `main` but landed after v0.5.8 was tagged, so no release notes mention it.
2. **Check the live server for duplicate games.** Canonical naming stops *new* splits; it does not
   merge what a server already holds under two spellings, and there is no merge tool ([[Backlog]]).

Everything else — the WA-03 second-account ACL test, the remaining Windows manual gates, the LAN
enrollment-URL check, the missing LA-04/05/06/07 regression tests, and the `WA-01`/113-of-114
baseline drift — is prioritised in [[Backlog]].

## Handoff: working on the Decky plugin

Read `logs/2026-08-15_decky-plugin.md` and [[Gotchas]] → *Decky plugin* first. Both carry
things that were measured on hardware and cannot be re-derived by reading code.

**The Deck.** `deck@192.168.68.73`, key auth is set up (see the maintainer — the key is deliberately
not named here). **The address is DHCP and moves often** (.67 → .73 → .76 on 2026-08-16, back to .73
by 2026-10-10 — try each before concluding it is asleep),
so a refused connection may be that rather than a sleeping Deck — and a *new* address with the same
host key is the same machine, not a warning worth acting on. It is awake only when the maintainer
wakes it. Useful state on it:
- **Decky Loader v3.2.10** (updated from v3.2.8 on 2026-10-10; plugin v0.2.2 loaded cleanly on it,
  no changes needed — the `_clean_env()` restore degrades safely if Decky ever stops polluting
  `LD_LIBRARY_PATH`);
- agent **v0.5.9**, service `savelocker.service` active and enabled, 4 tracked games, all with
  launch options confirmed;
- plugin **v0.2.2** at `~/homebrew/plugins/SaveLocker`, installed by the agent itself, and a config
  backup at
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
