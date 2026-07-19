# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

> **All three Linux/Deck security-hardening items shipped in v0.2.0** (PR #8, 2026-07-18) and the
> operational follow-up is done (container updated, fleet keys rotated). Indexed in
> `logs/shipped-2026-07.md`; rationale in `Decisions.md` §7–§9; narrative in `logs/sessions.md`.

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

### Quick guardrails

- **`pull` with empty save dir must fail cleanly, not crash.** Currently throws an `ArgumentException`
  stack trace. Should print: `'<game>' has no save directory set. Run: savelocker add-game --name
  "<name>" --dir <path>` and exit with a non-zero code. No restore attempted.

- **`add-game` should warn when `--dir` doesn't exist yet.** Silently accepts a non-existent path
  today. Add: `Warning: save directory does not exist — run the game first or verify the path.`
  This would have caught the wrong AppID/prefix path before the nested-folder disaster.

- **`doctor` should point at `scan` when appid is missing.** The `appid: (none — the launch wrapper
  cannot match this game)` message is correct but gives no hint. Append: `Run 'savelocker scan' to
  find the correct AppID, then re-add with --appid.`

- **`install.sh` should print the full-path launch option at the end.** Game Mode does not have
  `~/.local/bin` on PATH so `savelocker run -- %command%` silently prevents the game from launching.
  The install script should print: `Steam Launch Options: /home/$USER/.local/bin/savelocker run --
  %command%`. The KB article (`installing-the-agent.md`) should call this out too.

### Console-driven path setup (eliminates CLI typing on Deck)

- **Make the per-machine save path field obviously per-machine in the console.** The field is
  currently easy to set on the wrong machine's row. Label it `"Save path on [Machine Name]"` and
  scope it visually and editably to that machine's row only.

- **Surface scan candidates in the console when a Deck reports an unmapped game.** When the agent
  health event says a game is unmapped, show the path `scan` found alongside the "set path" field so
  the user can confirm and apply with one click rather than typing it.

- **`scan` should offer to auto-apply when a tracked game is unmapped.** If `scan` finds a candidate
  whose name matches a tracked game with no save dir, prompt: `Found save path for '<game>' —
  apply? (y/n)`. Eliminates `add-game` in the common case.

### Agent UI path browser (controller-navigable, no terminal required)

- **Add a path mapping flow to the agent UI (`localhost:5178`).** For any tracked game with no save
  dir, show a "Set save path" button instead of the current CLI instruction. Opens a simple
  directory browser (read the filesystem, list folders) navigable with the controller. On confirm,
  calls the equivalent of `add-game --dir` internally. No terminal or SSH required.

- **Pre-fill scan candidates in the agent UI path browser.** When `scan` finds a match for an
  unmapped game, open the browser pre-navigated to that path so the user just confirms.

## Medium priority
- **Windows: `%PROGRAMDATA%\SaveLocker` ACLs on a multi-user box.** The local API token (`api-token`, 0600 on Linux) has **no POSIX mode on Windows** — it inherits the ACL of `%PROGRAMDATA%\SaveLocker`, which the installer widens to give the interactive user Modify. On a machine with several local users, another user may be able to read it and drive this machine's agent. Note this is **not a new exposure**: `config.json` in the same directory already holds the long-lived machine key under the same ACL. Fix both together — tighten the directory ACL to the enrolling user + SYSTEM, or move mutable per-user state out of the machine-wide directory. `run-local-api-tests.ps1` only asserts the file *exists* on Windows; give it a real ACL assertion once the model is decided.
- **Linux agent secret permissions and state layout.** `config.json` contains a long-lived machine key, but file privacy currently depends on the launching shell's umask. Enforce `0700` on private state directories and `0600` on config, queue, health, and log files in code, including CLI enrollment paths. Consider separating immutable app files from mutable XDG config/state so upgrades and permission repair cannot overlap the executable tree.
- **Harden the `systemd --user` unit.** Keep the non-root per-user design, but add and Deck-test conservative restrictions such as `UMask=0077`, `NoNewPrivileges=yes`, `PrivateTmp=yes`, `ProtectSystem=full`, `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6`, `RestrictSUIDSGID=yes`, and `LockPersonality=yes`. Mirror every directive in both `packaging/linux/savelocker.service` and `SystemdAutoStart.UnitFile()` so the UI toggle cannot regenerate a weaker unit. Do not add `ProtectHome` (save access), `ProtectProc` (Linux writer probe), or `MemoryDenyWriteExecute` (.NET JIT). Record `systemd-analyze --user security savelocker.service` before/after on a Deck.
- **Linux release provenance.** Pin GitHub Actions in `release.yml` to full commit SHAs, especially the release-upload action with `contents: write`; publish SHA-256 checksums and a GitHub artifact attestation for the tarball; enable immutable releases using a draft → attach all assets → publish flow. Document the verification command beside the Deck install instructions.
- **Constrain external manifest paths.** The Ludusavi manifest is downloaded from mutable `master`, and expanded templates are not currently proven to remain inside the intended Proton prefix. Pin or integrity-verify an approved manifest revision, canonicalize resolved paths, reject `..`/symlink escapes outside allowed roots, and test a hostile manifest entry. Preserve explicit manually mapped portable-save paths as a separate trusted-user path.
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

_Dropped items (won't-do) are recorded in `logs/shipped-2026-07.md`._
