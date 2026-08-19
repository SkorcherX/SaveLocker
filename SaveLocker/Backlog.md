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

- **One game, several real sources — the dedupe rule needs a second dimension, and the choice needs
  to be visible.** Scoped 2026-08-19; **step 1 (the tie-break bug) shipped the same day**, steps 2–4
  remain. Asked for directly, using Cyberpunk as the example:
  MoonDeck's streaming shortcut and a genuinely-installed copy (Steam, or Epic/GOG through Heroic) can
  both legitimately exist for the same game on one device, and the question was three-fold — what to
  do about it, whether the dashboard should even know, and whether multiple local sources should
  enroll under one title.
  <br>**A concrete bug first, exposed by today's own MoonDeck fix.** `LinuxGameScanner.ScanAsync`'s
  final dedupe (`GroupBy(NormalizeName).OrderByDescending(has-a-save-dir).ThenBy(HasSteamCloud)
  .First()`) ties on `HasSteamCloud`, and a `SteamShortcut` candidate is *always* `HasSteamCloud:
  false` — "the same game can be both an installed Steam title and a shortcut to a DRM-free build;
  enrolling the one Cloud doesn't cover is the more useful pick," which is sound when the shortcut
  really is an alternate, independent build. Before today, a MoonDeck shortcut never resolved a save
  dir at all, so it never reached that tie — the first sort key (has-a-save-dir) already picked the
  genuine install correctly, for the wrong reason. Now that MoonDeck shortcuts resolve (this session's
  fix), if the SAME game is ALSO genuinely installed with real Steam Cloud, both candidates resolve, the
  MoonDeck one's hardcoded `false` wins the tie, and the candidate that survives is a streaming
  *pointer*, not the local install — recording the pointer's own synthetic AppID as `SteamAppId`, which
  the launch wrapper will only ever see during a MoonDeck session, never a genuine local one. The DRM-free
  reasoning the rule was built for doesn't apply to a pointer at all; it needs its own signal (e.g.
  `SteamShortcut.MoonDeckAppId is not null`, already known from this session's fix) rather than reusing
  `HasSteamCloud`. Small, precise, worth fixing on its own rather than folding into the larger piece
  below.
  <br>**The larger piece: discovery currently decides and hides, rather than showing its work.** The
  GroupBy dedupe silently picks ONE candidate per normalized name and the user never sees that there
  were two or three — not in `scan`, not in the Add Games UI, not in `doctor` (today's duplicate-AppID
  note only compares raw `shortcuts.vdf` entries against each other, never a shortcut against a
  SteamInstalled or Heroic candidate of the same name — the exact Cyberpunk shape asked about here
  would currently say nothing). That was a reasonable default when the "duplicate" was almost always
  genuinely redundant (Steam Cloud already covers the installed copy). It stops being reasonable once a
  "duplicate" can be a mere pointer to the SAME data, or genuinely divergent — two real, independently-
  played installs (Steam Cyberpunk at hour 20, a fresh Epic install never launched) that a silent pick
  would make one of them invisible.
  <br>**Should the dashboard handle it?** Only the part it already does, and no schema change: the
  server already converges "the same game from different machines" correctly via
  `ManifestLoader.CanonicalName` — one `Game`, many `MachineSavePath` rows, exactly the shape it needs.
  The new question — several local sources for the same game *on one machine* — is a discovery-time,
  agent-side concern, not a server one: it never needs to know a choice was made, only which single path
  this machine currently maps, precisely what it already stores. Pushing "which of several local copies"
  into the server model would be solving a local problem non-locally for no benefit.
  <br>**Should multiple local sources enroll to one title?** Yes to one title, no to each syncing
  independently — and the two cases genuinely differ in risk, worth telling apart from the native-Linux
  item above rather than solving the same way. Same-platform duplicates (Steam vs Epic vs GOG, all
  Windows/Proton, or a MoonDeck pointer at one of them) are the SAME underlying build's save format
  regardless of storefront — safely interchangeable, so "one title, one active local path, switchable"
  is enough; the mechanism already exists (`add-game --name X --dir <other-path>` re-maps today, it's
  just not discoverable). Cross-platform duplicates (Windows/Proton vs native-Linux) are NOT
  interchangeable — that is the whole reason the native-Linux item above needs `Game.Platform`
  isolation. Conflating the two would either over-restrict the safe case or under-protect the unsafe
  one.
  <br>**Proposed shape:**
  1. ~~Fix the tie-break: a MoonDeck-resolved candidate loses to a genuine SteamInstalled/Heroic
     candidate of the same name when both resolve, regardless of `HasSteamCloud`.~~ **DONE, same
     day.** `ScanAsync` now carries a local `ViaMoonDeck` flag per shortcut candidate (true only when
     `SuggestSaveDirAsync` resolved through the MoonDeck fallback prefix, not the shortcut's own) and
     sorts it ahead of the `HasSteamCloud` tie-break. Pinned by a new fixture (`Fake Dual Source
     Game`: genuinely installed with real Steam Cloud, also pointed at by a MoonDeck shortcut) —
     `run-linux-tests.sh` 227 → 230. Confirmed no regression on the real Deck across all four games
     from the sessions above.
  2. Extend the existing duplicate-AppID `doctor` note to compare ACROSS `ScanSource`s, not only within
     raw shortcuts — the Cyberpunk shape (shortcut vs. genuinely installed) is invisible to it today.
  3. Record what discovery is doing, not just its result: when the dedupe collapses more than one real
     candidate, keep the runner-up(s) — visible in `doctor`, and in the Add Games UI as a small
     "N other copies found" affordance on the row, so switching later is a menu pick instead of a
     remembered CLI flag.
  4. No new field is required to make a manual switch possible — `add-game --dir` already re-maps an
     existing tracked game. Step 3 is entirely about discoverability, not a missing mechanism.
  <br>**Also closes a stale pointer:** `CONTEXT.md`'s Next action list has referenced "Check the live
  server for duplicate games ([[Backlog]])" without a matching entry existing here — the server-side
  half of that (merging two Games that already diverged under different spellings) is a real, separate
  gap from the agent-side discovery question above and still has no plan; noted here so the pointer
  resolves to something.

- **Case-insensitive path matching inside a Wine/Proton prefix.** Scoped 2026-08-19, not built.
  Found by reading Ludusavi's own resolver (`src/scan.rs`, `src/path.rs`) side by side with ours
  while chasing the Borderlands 2 bug in `logs/2026-08-19_moondeck-save-detection.md`: Ludusavi
  never does an exact-case check inside a Steam compatdata prefix — it glob-matches the filesystem
  and forces case-insensitivity there unconditionally (`add_path_insensitive!`), and everywhere else
  picks sensitivity from the SAVE'S declared platform (`os: windows` → insensitive), never from the
  host OS. `PathResolver.ResolveConcrete` does the opposite: one exact-case `Directory.Exists` against
  the fully-expanded string. That gap is exactly why Borderlands 2 needed a hand-written fix — Wine
  created `My games`, the manifest and the working prefix both say `My Games` — and it will recur for
  any other game a re-created prefix, a different Proton version, or a different Wine build happens to
  case a folder differently, not just a relocated install.
  <br>**Shape of the fix:** only `PathResolver.Proton`/`.Wine` need this — `.Windows()` never should,
  NTFS is already case-insensitive there. Can't bolt a case-insensitive check onto the END of
  `ResolveConcrete`: it builds one path string and checks it once, and any ONE of several segments
  (`Documents`, `My Games`, the game's own folder) could be the mismatched one, not just the last.
  Needs to walk segment-by-segment, resolving each one against its parent's REAL directory listing
  case-insensitively (`Directory.GetDirectories(parent).FirstOrDefault(d =>
  Path.GetFileName(d).Equals(segment, OrdinalIgnoreCase))`) and building the actual on-disk path as it
  goes. Decide the tie-break up front, don't leave it implicit: if a parent genuinely has both
  `My games` and `My Games` (a plausible OUTCOME of the exact relocation bug that motivated this),
  prefer an exact-case match when one exists, else pick deterministically (newest mtime is probably
  the honest answer, matching "which copy is actually being written to").
  <br>**Where:** `src/Shared/PathResolver.cs`, shared by the shortcut and installed-game scan paths
  both — this subsumes, not replaces, today's Borderlands-2 fix: that one fixes "resolved the wrong
  PREFIX entirely" (a different library); this fixes "resolved the right prefix, wrong casing inside
  it," which can happen with no relocation involved at all.
  <br>**Tests:** a fixture Wine prefix with one wrong-cased folder resolving correctly; a fixture with
  BOTH castings present, pinning the tie-break; a check that the Windows-host resolver's behavior is
  provably unchanged (never even reaches the new code path).

- **Native Linux save support** — sync a native-Linux-built game's save alongside its Windows/Proton
  counterpart, without letting the two formats corrupt each other. Asked for directly 2026-08-19,
  same session as the fixes above: "I want the freedom to just store it in the dashboard and try — if
  it works ok, if it doesn't I have my OS save already there." Scoped, not built.
  <br>**The data already exists for this.** The SAME manifest SaveLocker downloads carries `os: linux`
  save-path entries — seen directly while investigating A Short Hike and Roadwarden today:
  `<xdgConfig>/unity3d/adamgryu/A Short Hike/GameSaveNew.mountain`, `<home>/.renpy/roadwarden`.
  `Decisions.md` §1 keeps native Linux out of scope for a SYNC-safety reason, not a detection one: "a
  Proton save is byte-identical to a Windows PC's, so the existing content-hash lineage works with
  zero schema change" — a native Linux build's save format carries no such guarantee, so restoring a
  Linux-format save over a Windows-format one (or the reverse) risks a save the target game can't read
  or silently misinterprets.
  <br>**The user's own framing narrows the real risk usefully.** PUSHING a save is a pure read —
  archiving whatever a native Linux build wrote can never damage anything, whatever the format turns
  out to be. The danger is entirely on PULL, which overwrites live files. Discovery and backup can be
  as exploratory as the user wants; only restore needs to be made safe.
  <br>**The footgun to design around, found by reasoning through it rather than by hitting it:**
  enrollment already makes "the same canonical manifest name" auto-join one Game
  (`ManifestLoader.CanonicalName` — "makes both machines converge" is the whole point of it). Add
  native-Linux discovery with no other change, and a Windows PC and a Linux-native Deck both playing
  "A Short Hike" would silently join the SAME Game the first time either enrolled it — exactly the
  cross-format mixing `Decisions.md` warns against, by DEFAULT, not as an edge case.
  <br>**Proposed shape**, mirroring `tasks/LinuxAutoUpdate.md`'s multi-session structure:
  1. *Detection* — `PathResolver` gains a native-Linux host resolver (parallel to `.Windows()`) with
     the XDG tokens the manifest actually uses (`<xdgData>`/`<xdgConfig>`, same `$XDG_*`-or-fallback
     pattern `HeroicRoots.Find()` already has, just not centralized). `ManifestLoader` gains an
     `IsLinuxSave` filter parallel to `IsWindowsSave`, and `ResolveSaveDirectories` takes which one to
     apply instead of hardcoding Windows. Telling a native install from a Proton one apart isn't
     reliable from Steam's ACF alone (Steam can silently offer either build for the same game), so
     `LinuxGameScanner` should try BOTH resolvers and surface whichever actually resolves — the same
     "report the disagreement, don't guess" instinct behind Heroic's split-prefix note — rather than
     trying to classify the install up front.
  2. *Data model — keep them from ever merging.* A `Game.Platform` field decided at creation; the
     uniqueness key auto-join relies on becomes (canonical name, platform), not canonical name alone.
     This has to land BEFORE step 1 reaches anyone with the same game on two different platforms, or
     the footgun above is live from day one. Two Games can share a display name; they must never share
     a version lineage.
  3. *Pull-time guard, belt-and-braces.* Even with #2 preventing the dangerous case structurally, check
     platform again immediately before restore — mirrors `SavePathGuard`'s own "validated at five entry
     points, re-checked again right before use" pattern, and this codebase's standing preference for
     defense in depth over trusting one earlier gate.
  4. *UI* — a badge, not a name suffix: two cards both titled "A Short Hike" with a distinguishing tag
     reads better than inventing a second string identity for one real game. The Add Games filter row
     already has the right slot (`FILTERS` in `AddGamesView.tsx`, `AddFilter` in `Ui/UiApp.cs`).
  <br>**Related — read together with Emulator saves, above.** An emulator's own save-state format is
  genuinely platform-neutral (RetroArch's format doesn't care what OS RetroArch runs on), which is why
  that item was already flagged as the first thing that could sync Deck↔Windows with no Proton
  involved. A native-Linux GAME build carries no such guarantee — its save format is whatever the
  developer's Linux port happened to use, unrelated to the Windows build's — which is exactly why this
  item needs the platform-isolation step (#2) that emulator saves may not.
  <br>**Deliberately not attempted in phase 1:** cross-format conversion or merging — nothing about the
  manifest or the archive format gives any basis for that, and guessing at compatibility between two
  save formats is precisely the mistake `logs/2026-08-19_moondeck-save-detection.md` argues against
  throughout.

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

- **Surface duplicate-shortcut-name warnings outside `doctor`.** `logs/2026-08-19_moondeck-save-detection.md`
  added a `doctor` note for a game name backed by two different Steam AppIDs (found on the
  maintainer's own Deck: HITMAN 3, Minit, Moving Out, Animal Crossing, Metal Gear, Waydroid) — Steam
  launches only one, and scan's dedupe can silently pick the dead one. `doctor` is CLI/headless-only;
  neither the agent-ui Add Games view nor the Game Mode UI reads its output at all, so a user who
  never opens a terminal never sees the warning. Worth a chip on the affected candidate once there is
  evidence this recurs for more than one Deck.

- **Linux agent secret permissions and state layout.** `config.json` contains a long-lived machine key; file privacy depends on the launching shell's umask. Enforce `0700` on private state directories and `0600` on config, queue, health, and log files in code, including CLI enrollment paths. Consider separating immutable app files from mutable XDG config/state so upgrades cannot overlap the executable tree.

- **Upload only changed files, not the whole save folder.** Raised 2026-08-19 (maintainer plays
  Cyberpunk 2077 and Fallout: New Vegas, both of which keep many largely-static per-slot save files —
  a session that changes one slot still re-zips and re-uploads every other unchanged one today,
  because change detection is one aggregate hash over the whole folder
  (`SaveArchive.HashDirectory`), not per-file. Real payoff for a Deck on a constrained uplink, same
  cost center as the parked `tasks/OfflineBackoff.md` (bytes instead of requests). Decisions to
  settle first (copy-forward reconstruction vs. a content-addressable store, and where the
  fast-forward-only scope boundary sits) and the measurement that proves it:
  `tasks/PerFileDeltaUpload.md`.

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
