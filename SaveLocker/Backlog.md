# Backlog

Active items only. Completed work is in `logs/sessions.md`.

## Immediate — verify on device

- **Sync toaster reduction** (committed `777b9ab`, not yet in a release) — a dashboard sync now fires **one** summary toast with the save timestamp instead of 4. Routine engine progress is log-only; only conflicts/blocked-pulls/offline-retries/lease warnings toast. Verify on device: dashboard sync → single toast; force a conflict → still alerts. Note: auto pre-launch/post-exit syncs are now silent on success too (by design — flag if unwanted).

_v0.1.2 fully verified on device (2026-07-12): version display, silent auto-relaunch, and installer persistence across a Docker update all confirmed. See `logs/sessions.md`._

## High priority
- **Scheduled GitHub installer auto-poll** — the manual "Fetch latest from GitHub" button shipped (2026-07-11). Follow-up: a background service that periodically polls the GitHub Releases API and auto-fetches a newer installer (opt-in via config, e.g. `AgentUpdate:AutoFetchHours`). Mirror `LeaseSweeperService`'s `BackgroundService` + `IServiceScopeFactory` pattern; reuse `AgentInstallerService.FetchLatestFromGitHubAsync`.
- **Code-signing** — installer + exe currently unsigned. SmartScreen warns on first run for new users. Options: EV certificate or Azure Trusted Signing.
- **Per-game glob filters** — include/exclude file patterns before archiving (e.g., exclude `*.log`, `*.tmp`). Upload limit may need raising at the same time (`Storage__MaxUploadMb` config or Kestrel `MaxRequestBodySize`).

## Medium priority
- **Save-in-use safety** — auto-push on process-exit uses a 5 s quiet-period debounce. Some games write saves for several seconds after exit, risking a partial archive. Options: longer debounce, file-lock polling, or a user-configurable delay per game.
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

## Low priority / stretch
- **Agent local API → generated types** — `AgentApiServer.cs` is a raw `HttpListener` returning anonymous C# objects. Converting to ASP.NET Core minimal API would make it OpenAPI-introspectable so `agent-ui/src/types.ts` can be auto-generated (deferred from hygiene #5b — larger swing, touches WinForms STA + WebView2 lifetime).
- **SteamGridDB key in agent UI** — the key is configurable from the web dashboard but not from the agent-ui Settings view.
- **CloudFlare Access / remote access hardening** — currently blocked by Cloudflare Tunnel's 100 MB file limit (conflicts with large save archives). Re-evaluate when the upload model changes.
