# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

---

## High priority

**All three bug bounties shipped in v0.5.0 (2026-07-29).** Code is on `main`; what remains is the
verification that did not happen before the tag. Write-ups:
`logs/2026-07-29_winagent-bugbounty.md`, `logs/2026-07-29_linuxagent-bugbounty.md`,
`logs/2026-07-27_console-bugbounty.md`.

- **v0.5.0 post-release verification.** Ordered by what carries the most risk of the release notes
  being wrong:
  - ~~**Run `tests/linux/run-linux-tests.sh` in WSL** (40 checks)~~ — **DONE 2026-08-08, 40/40.**
    It had never passed: `Storage__AgentInstallerRoot` was unset, so the server died at startup
    *after* migrations and 16 server-dependent checks failed as though the agent were broken. Fixed
    in the harness (PR #36). This closes the Linux auto-start and `doctor` gap.
  - **Deck verification** — the five scenarios in `logs/2026-07-29_linuxagent-bugbounty.md` →
    Verification. Hardware available since 2026-07-19. Fold in the two v0.5.1 Deck fixes while
    there (one ring on open with A working; a resting cursor must not paint a second selector) and
    a real save-path detection check now that `<base>` resolves from `StartDir`.
  - **Second-Windows-account ACL test (WA-03).** The one with a user-visible consequence: the
    credentials are ACL-locked to the enrolling account and asserted against the well-known SIDs,
    but no second account has ever tried to read them, and it is unconfirmed that the enrolled user
    can still sync *and take a silent update* after a reboot. **v0.5.0's notes describe the change
    rather than promising the guarantee, and Known Issues says so — reword
    `web/src/releases/0.5.0.md` once this passes.** That file is both the console page and the
    GitHub Release body, so one edit fixes both.
  - Remaining Windows gates: fresh-VM install, a real game, a real non-Steam Steam shortcut, and the
    first-run Settings deep link on a cold WebView2 profile (the automated test drives the same code
    path through a refused launch, because the prompt is a modal dialog no test can answer).
  - **LAN enrollment-URL check** on the real deployment (`logs/2026-07-27_console-bugbounty.md` →
    Verification).

- **`WA-01 the dashboard is told the real reason` fails on pristine `main`** (found 2026-08-14).
  `run-winagent-tests.ps1` reads **113/114** at `ff4464b` with no local changes — confirmed by
  building and running a detached worktree, so it is not the `.verify/` trap. The dashboard command
  executes and returns a result; the result text no longer matches `running`. Either fix it or
  re-baseline the 114 in [[Build and Run]]. Not investigated.
  <br>**It did NOT reproduce on 2026-08-15** (full 114 ran, WA-01 passed, the only two failures were
  an unrelated bug in that session's own change). So it is **intermittent**, not a standing failure —
  which rules out re-baselining as the fix and makes a timing dependency the thing to look for.
  Treat a green WA-01 as evidence of nothing until it is understood.

- **v0.5.4 surfaces that shipped without hardware coverage.** Neither can lose save data — worst
  case is a list that filters oddly — which is why they shipped, but both are unverified: the Heroic
  **store** sub-chips (the test Deck has no Heroic games, so the chip correctly never rendered) and
  the Game Mode filter row's gamepad navigation. Nothing drives the agent-ui React chips in any
  suite either.

- **Emulator saves.** Not implemented at all: RetroArch, Dolphin, PCSX2, DuckStation and friends
  keep saves and save-states in their own per-emulator trees, and the Ludusavi manifest does not
  describe them, so nothing in discovery can find them today. On a Deck this is a large share of
  what people actually play. Once it lands, add an **Emulator** filter to the Add Games chip row
  (`agent-ui/src/components/AddGamesView.tsx` → `FILTERS`, and the Game Mode mirror in
  `Ui/UiApp.cs` → `AddFilter`) — the row is built to take another entry. Note the save-variant
  problem from Decisions.md §1 applies here too: an emulator save is platform-neutral, so it is
  the first candidate that could sync between a Deck and a Windows PC without Proton involved.

- **File-level saves — the 24 games the install-root guard now refuses.** A save location is a
  DIRECTORY throughout, so a manifest entry like `<base>/Save.dat` can only resolve to the whole
  install folder. Refusing that was right (it would archive the game, and restore's delete pass
  would prune another machine's installation), but it costs **8% of the manifest**: sweep went
  99.0% → 90.9%, every new miss a `MISS(<base>)`. Recovering them properly means archiving the
  matching FILES rather than their containing directory — a change to the archive model, touching
  `SaveArchive`, the settle gate and restore. Measure before building: some of the 24 have another
  path that now wins instead (Cave Story+ did), so the true loss is smaller than 24.
  <br>**Manifest-wide sizing (2026-08-14):** of 21,061 entries with save paths, **702** have a save
  set that trims to `<base>` and nothing else — those are refused outright — and **169** more have
  `<base>` as one of several, so they lose a path but keep an answer. That 702 is the ceiling on
  what this item can recover. Counted from `data/manifest.yaml` by trimming each save template at
  its first wildcard, the same rule `PathResolver` applies.

- **Missing regression tests from the Linux bounty — LA-04/05/06/07.** Folder-watcher refresh,
  multi-game add, the Game Mode window crash and the settings-write clobber all have code fixes and
  no tests. This is why several items above have to be checked by hand.

- **No markdown table renders in the console** (found 2026-08-15). `HelpView.tsx:113` and
  `WhatsNewView.tsx:100` use a bare `<ReactMarkdown>`; tables are a GFM extension and need
  `remark-gfm`. **`cli-reference.md` is almost entirely tables**, so the KB's most-used page is a
  wall of raw pipe characters for every user today. Needs the plugin *and* table styling that
  respects the bounded-flex-column trap in [[Gotchas]]. `agent-update.md` was deliberately written
  without tables because of this; it would read better as one once fixed.

- **Self-host the console fonts.** The console loads Inter and JetBrains Mono from Google Fonts at
  runtime, so on a LAN box with no internet it renders in fallback fonts. CS-13 fixed the import
  being *discarded*, not the dependency. Needs woff2 subsets for five Inter weights; the Deck UI
  already vendors TTF Regular/SemiBold in `src/Agent.Linux/Ui/Fonts/` (SIL OFL).

- **Device-verify fresh Windows installer enrollment.** The wizard shipped in v0.1.7; the upgrade path is well verified. The **fresh install** (clean box, no `%PROGRAMDATA%\SaveLocker`) has never been exercised. Scenarios archived in `logs/2026-07-14_installer-enrollment.md`:
  - Happy path: run installer, choose enrollment file → page shows server + machine name → install → machine appears online in Machines.
  - ACL trap: `icacls "%PROGRAMDATA%\SaveLocker"` — interactive user needs Modify.
  - Expired-token, skip, and `/SILENT /ENROLL="C:\path\policy.json"`.

## Medium priority

- **Interactive setup guide in the console.** Raised 2026-08-14 alongside the savelocker.com plan. A
  first-run walkthrough that takes a new user from an empty server to a syncing game, in the console
  itself rather than in prose: mint an enrollment file → wait for the machine to appear → add a first
  game → confirm a push landed, each step self-checking against live state (Machines, GameState) so
  it advances on its own and cannot claim a step is done when it is not. Two reasons this is not
  documentation: the marketing site's Get Started has to end *somewhere*, and the natural place is
  "open the console, it takes it from here" — and the Deck's launch-option step (`savelocker run --
  %command%`) is the single most-missed action in the whole flow, with nothing today that notices it
  was skipped. Re-runnable from Help, and resumable — a user enrolls the Deck days after the PC.
  Reuses the Help KB shell (`web/src/help/`) for the article surface; the checks are `/api/overview`
  + machines. Deck Game Mode already has a "Next step" card (`Ui/UiApp.cs`) — same idea, wider scope.

- **Decky plugin — Phase 5 only.** Phases 1–4 are **done and verified on hardware** (shipped in
  v0.5.6 and plugin v0.2.0); the plugin lives at <https://github.com/SkorcherX/SaveLocker-Decky>.
  What remains is **the agent keeping the plugin updated**, so users are not stuck choosing between
  Decky's custom-store setting — which replaces the official store while it is set, so nobody leaves
  it there — and never being told an update exists. The mechanism is already proven on a Deck: a
  non-`_root` plugin's files are chowned to the desktop user, and with the `debug` flag Decky hot
  reloads within a second of a file changing, so the agent can update the plugin with no sudo and no
  restart. First install still needs Decky (one paste), and `plugin.json` is root-owned so the agent
  can never replace it. Plan and corrections: `logs/2026-08-15_decky-plugin.md` → Phase 5.

- **Decky plugin: left-stick scrolling in the QAM is steppy.** The D-pad is fine. Steam scrolls by
  moving focus, so the stick steps between focus targets rather than scrolling freely, and the panel
  has many — `doctor` output is one target per line, which is what makes it reachable at all. The
  lever is *fewer stops*: collapse the output behind a "show output" expander so those rows only
  exist when wanted. Cosmetic, and only noticeable after running doctor.

- **Linux agent secret permissions and state layout.** `config.json` contains a long-lived machine key; file privacy depends on the launching shell's umask. Enforce `0700` on private state directories and `0600` on config, queue, health, and log files in code, including CLI enrollment paths. Consider separating immutable app files from mutable XDG config/state so upgrades cannot overlap the executable tree.

- **Linux auto-update.** The update channel (`/api/agent/latest`) is installer-shaped and Windows-only. A Deck user currently re-runs `install.sh` from a newer tarball. Worth doing before there are many Deck users — a headless device that never updates is one nobody will notice is stale.
  <br>**Planned 2026-08-14 → `tasks/LinuxAutoUpdate.md`** (four phases, one per session). It absorbs
  the two items below it — the unit hardening because Phase 3 edits both unit sources anyway, and
  release provenance because it is the same trust story. Two things the plan turns on: `systemctl
  --user stop` kills the whole cgroup, so the apply cannot run as a child of the daemon; and the
  Linux install prefix **is** the state dir, so an update is a per-file copy and never a directory
  swap.

- ~~**Harden the `systemd --user` unit.**~~ **DONE** — shipped in v0.5.5, and measured on the Deck
  2026-08-15: `systemd-analyze --user security savelocker.service` → **8.5 EXPOSED**. All seven
  settings are confirmed in effect (`NoNewPrivileges`, `PrivateTmp`, `LockPersonality`,
  `RestrictSUIDSGID`, `UMask`, `PrivateMounts` and the `RestrictAddressFamilies` allow-list — the
  blocked families read ✓ and the three we permit read ✗, which is the allow-list working).
  `ProtectSystem=full` reads ✗ only because it is not `strict`; its description confirms it is set.
  <br>**Do not chase the 8.5.** The score is dominated by rows that do not apply to a `--user` unit
  or that were rejected on purpose — see [[Gotchas]] → *Testing*. `ProtectHome`, `ProtectProc` and
  `MemoryDenyWriteExecute` remain refused for the documented reasons (save access, the writer probe,
  the .NET JIT).
  <br>**If it is ever revisited**, the honest candidates are `SystemCallArchitectures=native`,
  `RestrictNamespaces`, `ProtectKernelTunables/Logs/Modules`, `ProtectControlGroups`, `ProtectClock`,
  `ProtectHostname`, `RestrictRealtime` and `SystemCallFilter=@system-service`. None is free: the
  daemon spawns processes (the staged binary's smoke test, `systemctl`), and the unit file's own
  comment is the standing warning — a device that will not start at all is far worse than one that
  scores badly. Each needs a Deck test, not a reading.

- **Linux release provenance.** *(Folded into `tasks/LinuxAutoUpdate.md` Phase 4 — note the update channel does NOT depend on it: the server hashes the bytes it stored.)* Pin GitHub Actions in `release.yml` to full commit SHAs; publish SHA-256 checksums and a GitHub artifact attestation for the tarball; use draft → attach all assets → publish flow. Document the verification command beside the Deck install instructions.

- **Constrain external manifest paths.** The Ludusavi manifest is downloaded from mutable `master`; expanded templates are not proven to stay inside the intended Proton prefix. Pin or integrity-verify an approved manifest revision, canonicalize resolved paths, reject `..`/symlink escapes outside allowed roots, test a hostile manifest entry. Preserve explicit manually mapped portable-save paths as a separate trusted-user path.

- **Deferred: one state owner for the Linux agent** — wrapper→daemon IPC over a Unix socket, standalone fallback when no daemon is up. The locking in `Decisions.md` §8 makes the current two-owner model *correct*; IPC would make it *simple*. Worth doing before the state files grow further.

## Planned / future

- **Game Mode UI reflects a stale game list.** `savelocker ui` only *reads* local `config.json`; it never reconciles with the server (only the daemon does, every 20s — `CommandPoller.ReconcileGamesAsync`). So a game deleted in the console still shows in Game Mode until the daemon runs, and there is no in-UI way to untrack. Deferred 2026-07-24 (maintainer chose to keep Phase 3 lean). Fix when revisited: reconcile-on-launch (+ periodic) in `savelocker ui`, optionally a per-game "Stop tracking" that also deletes server-side so the daemon does not re-adopt it (`CommandPoller.cs:157`).

- **The other stores' cloud flags.** `ManifestLoader.ManifestCloud` parses only `steam`, because
  that is the only flag a surface acts on. The manifest also marks `gog` (3,232), `epic` (739),
  `origin` (239) and `uplay` (106). These matter for **Heroic** candidates, which are exactly the
  GOG/Epic/Amazon games currently flagged `HasSteamCloud: false` — correct as far as it goes, but it
  means a GOG game that GOG Galaxy already syncs is offered as if nothing covers it. Needs a
  per-store flag on `ScanCandidate` rather than a second bool, and a decision about whether the
  default view should hide those too (Galaxy sync is opt-in per game, unlike Steam Cloud — so
  probably not, which is why this is not high priority).
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.
- **File-count / newest-mtime delta in conflict UI** — would help disambiguate conflict options. The server does not store it; needs computing at upload time or deriving from the archive on demand. Not done; everything else in conflict Tier 1 is complete.

_Dropped items (won't-do) are recorded in `logs/shipped-2026-07.md`._
