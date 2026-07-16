# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

## High priority
- **Code-signing** — installer + exe currently unsigned. SmartScreen warns on first run for new users. Options: EV certificate or Azure Trusted Signing.

## Planned — large
- **Linux agent (Proton / Steam Deck)** — ✅ **SHIPPED, all six phases** (2026-07-12 → 2026-07-14; PRs #1, #4, #5, #6). Design locked in `Decisions.md`; the plan and its outcomes are archived at `logs/2026-07-14_linux-agent.md`. Proton-only for v1, headless daemon serving the existing React UI, Steam launch-option wrapper as the sync trigger.
  - **Shipping (2026-07-14):** `release.yml` now builds the **Linux tarball on `ubuntu-latest`** and attaches `savelocker-<ver>-linux-x64.tar.gz` to the GitHub Release. Before this there was **no way for a Deck user to install the agent at all** — the packaging scripts existed but nothing ever ran them. CI's `package-linux` job now installs the tarball into a throwaway HOME on every PR so it cannot rot.
- **Device-verify fresh Windows installer enrollment.** The wizard and silent-upgrade guard shipped in v0.1.7; the enrolled-machine silent upgrade is verified. A clean-machine happy path, config-directory ACL, expired-token, skip, and `/SILENT /ENROLL=…` checks remain.
  - **Deferred: Linux auto-update.** The update channel (`/api/agent/latest` → hosted `.exe`) is installer-shaped and Windows-only. A Deck user currently re-runs `install.sh` from a newer tarball. Retrofitting it for a tarball is its own piece of work — worth doing before there are many Deck users, since a headless device that never updates is one nobody will notice is stale.
  - **ALL PHASES (0–6) DONE.** Proton saves are byte-identical to Windows saves (proven in CI, round-tripping Windows→Linux→Windows). Enrollment is one file and one command, with no API key ever copied by hand. A Deck's failures reach the console. And Phase 6 fixed a **real data-loss bug** that also affected Windows: the restore's delete pass followed symlinks and **deleted files outside the save folder** (junctions on Windows, and a Wine prefix is full of links).
  - **⚠️ THE ONLY THING LEFT IS HARDWARE.** No Deck is owned; everything is WSL + CI. **gamescope / Game Mode, the immutable rootfs, SD-card library paths and suspend/resume cannot be proven any other way.** Validate on a borrowed/used Deck or on Bazzite, or recruit a Deck-owning beta tester, **before shipping this to real users**. Built ≠ verified.

## Medium priority
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

_Dropped items (won't-do) are recorded in `logs/shipped-2026-07.md`._
