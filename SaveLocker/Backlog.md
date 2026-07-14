# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

## Immediate
- **Device-verify 5e (glob filters)** once v0.1.4 installs — add `*.log` to a game, sync, confirm the log isn't in the archive and a log-only change creates no new version. (Server/dashboard side already live after Docker redeploy.)
- **Device-verify save-in-use settle gate** (built 2026-07-12, not yet on device) — play a game that flushes slowly, exit, and confirm the agent log shows the settle wait then `save files settled.` before the push, and that the archived version is complete. Tune the delay in agent Settings → Sync Safety if a game needs longer.

## High priority
- **Code-signing** — installer + exe currently unsigned. SmartScreen warns on first run for new users. Options: EV certificate or Azure Trusted Signing.

## Planned — large
- **Linux agent (Proton / Steam Deck)** — design locked in `Decisions.md`; phased execution plan in `tasks/linux-agent.md`. Proton-only for v1 (Proton saves are byte-identical to Windows saves → no schema change), headless daemon serving the existing React UI, Steam launch-option wrapper as the sync trigger. Dev on WSL2 + a fake-game harness; **no Deck owned**, so hardware validation is a deferred risk.
  - **ALL PHASES (0–6) DONE.** Proton saves are byte-identical to Windows saves (proven in CI, round-tripping Windows→Linux→Windows). Enrollment is one file and one command, with no API key ever copied by hand. A Deck's failures reach the console. And Phase 6 fixed a **real data-loss bug** that also affected Windows: the restore's delete pass followed symlinks and **deleted files outside the save folder** (junctions on Windows, and a Wine prefix is full of links).
  - **⚠️ THE ONLY THING LEFT IS HARDWARE.** No Deck is owned; everything is WSL + CI. **gamescope / Game Mode, the immutable rootfs, SD-card library paths and suspend/resume cannot be proven any other way.** Validate on a borrowed/used Deck or on Bazzite, or recruit a Deck-owning beta tester, **before shipping this to real users**. Built ≠ verified.

## Medium priority
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

## Low priority / stretch
- **Agent local API → generated types** — `AgentApiServer.cs` is a raw `HttpListener` returning anonymous C# objects. Converting to ASP.NET Core minimal API would make it OpenAPI-introspectable so `agent-ui/src/types.ts` can be auto-generated (deferred from hygiene #5b — larger swing, touches WinForms STA + WebView2 lifetime).

_Dropped items (won't-do) are recorded in `logs/shipped-2026-07.md`._
