# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

## Immediate
- **Device-verify 5e (glob filters)** once v0.1.4 installs — add `*.log` to a game, sync, confirm the log isn't in the archive and a log-only change creates no new version. (Server/dashboard side already live after Docker redeploy.)
- **Device-verify save-in-use settle gate** (built 2026-07-12, not yet on device) — play a game that flushes slowly, exit, and confirm the agent log shows the settle wait then `save files settled.` before the push, and that the archived version is complete. Tune the delay in agent Settings → Sync Safety if a game needs longer.

## High priority
- **SQLitePCLRaw → 3.x (security: CVE-2025-6965, High)** — `dotnet build` reports NU1903 for `SQLitePCLRaw.lib.e_sqlite3` (memory corruption in SQLite's aggregate-term handling). **Pre-existing, not caused by the net10 upgrade** — net9 shipped 2.1.10 with the same advisory; SDK 10 simply audits transitive packages by default and started reporting it. **There is no patched 2.x release**: the fix needs SQLite ≥ 3.50.2, i.e. SQLitePCLRaw **3.x**, a major bump of the native provider *underneath EF Core* (EF 10 resolves 2.1.11). Deliberately kept out of the net10 PR — SQLite is the one component where a subtle break silently corrupts save data. Do it as **its own change**, and lean on the test net (the cross-OS byte-compare is a genuinely good safety net for a DB-provider swap). Practical exposure is low meanwhile: exploitation needs attacker-controlled *query structure*, and all SQL is EF-generated and parameterized. Re-check whether a later EF Core 10.0.x resolves 3.x on its own before forcing an override.
- **.NET 10 (LTS) upgrade** — **has a deadline.** .NET 9 is STS and goes out of support **10 Nov 2026** (already in maintenance: security fixes only); .NET 10 is LTS to Nov 2028. Decision locked in `Decisions.md → Runtime: .NET 10 LTS`; phased plan in `tasks/dotnet-10-upgrade.md`. **Do it before Linux agent Phase 4** — the test net (Windows 10/10, Linux 10/10, harness 27/27, cross-OS byte-compare in CI) is at its strongest right now, and Phases 4–6 then get written on net10 instead of ported to it. Also folds in a `global.json` to stop CI and dev silently using different SDKs.
- **Code-signing** — installer + exe currently unsigned. SmartScreen warns on first run for new users. Options: EV certificate or Azure Trusted Signing.

## Planned — large
- **Linux agent (Proton / Steam Deck)** — design locked in `Decisions.md`; phased execution plan in `tasks/linux-agent.md`. Proton-only for v1 (Proton saves are byte-identical to Windows saves → no schema change), headless daemon serving the existing React UI, Steam launch-option wrapper as the sync trigger. Dev on WSL2 + a fake-game harness; **no Deck owned**, so hardware validation is a deferred risk.
  - **Phases 0–3 done.** "Proton saves are byte-identical to Windows saves" is no longer an assumption — Phase 3 proves it in CI, round-tripping a save Windows→Linux→Windows byte-for-byte with matching hashes.
  - **Next: Phase 4** — enrollment token + policy import (single-use ~15-min token, `savelocker enroll --file <policy>`; no signing, TOFU-pin the server).
  - Phase 5 (agent health reporting) **ships with Linux, not after** — a headless spoke cannot surface a conflict, so without it a Deck failure is invisible.

## Medium priority
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

## Low priority / stretch
- **Agent local API → generated types** — `AgentApiServer.cs` is a raw `HttpListener` returning anonymous C# objects. Converting to ASP.NET Core minimal API would make it OpenAPI-introspectable so `agent-ui/src/types.ts` can be auto-generated (deferred from hygiene #5b — larger swing, touches WinForms STA + WebView2 lifetime).

_Dropped items (won't-do) are recorded in `logs/shipped-2026-07.md`._
