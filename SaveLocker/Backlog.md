# Backlog

Not-yet-done work only. Shipped items are indexed in `logs/shipped-2026-07.md`
(full detail in `logs/sessions.md`).

## Immediate
- **Device-verify 5e (glob filters)** once v0.1.4 installs — add `*.log` to a game, sync, confirm the log isn't in the archive and a log-only change creates no new version. (Server/dashboard side already live after Docker redeploy.)

## High priority
- **Scheduled GitHub installer auto-poll** — follow-up to the shipped manual "Fetch latest from GitHub" button. A background service that periodically polls the GitHub Releases API and auto-fetches a newer installer (opt-in via config, e.g. `AgentUpdate:AutoFetchHours`). Mirror `LeaseSweeperService`'s `BackgroundService` + `IServiceScopeFactory` pattern; reuse `AgentInstallerService.FetchLatestFromGitHubAsync`.
- **Code-signing** — installer + exe currently unsigned. SmartScreen warns on first run for new users. Options: EV certificate or Azure Trusted Signing.

## Medium priority
- **Save-in-use safety** — auto-push on process-exit uses a 5 s quiet-period debounce. Some games write saves for several seconds after exit, risking a partial archive. Options: longer debounce, file-lock polling, or a user-configurable delay per game.
- **Registry-based saves** — the Ludusavi manifest has a `registry:` section; currently only `files:` paths are handled.
- **Multi-directory saves** — some games list multiple save paths in the manifest. The sync engine tracks one `SaveDirectory` per game; multi-dir support needs a schema change.

## Low priority / stretch
- **Agent local API → generated types** — `AgentApiServer.cs` is a raw `HttpListener` returning anonymous C# objects. Converting to ASP.NET Core minimal API would make it OpenAPI-introspectable so `agent-ui/src/types.ts` can be auto-generated (deferred from hygiene #5b — larger swing, touches WinForms STA + WebView2 lifetime).
- **SteamGridDB key in agent UI** — the key is configurable from the web dashboard but not from the agent-ui Settings view.
- **CloudFlare Access / remote access hardening** — currently blocked by Cloudflare Tunnel's 100 MB file limit (conflicts with large save archives). Re-evaluate when the upload model changes.
