# Backlog

Active items only. Completed work is in `logs/sessions.md`.

## Immediate — verify v0.1.2 end-to-end

The three v0.1.1 bugs are fixed and shipped in `v0.1.2` (see `logs/sessions.md` 2026-07-11). Remaining on-device checks:

- ✅ **Agent version display** — **verified on device 2026-07-12**: tray header shows `0.1.2` (was `0.0.0`, then `0.1.0`). Root cause was MinVer overriding version fields inside an MSBuild target; fixed with `MinVerVersionOverride` env var + reading `FileVersion` at runtime.
- ✅ **Silent auto-relaunch** — **verified on device 2026-07-12**: agent restarts and the tray icon reappears after a silent update (`skipifsilent` removal).
- **Verify installer persistence** — after `docker compose pull && up -d`, confirm the hosted installer survives (`Storage:AgentInstallerRoot=/data/agent-installer`). Test at the next server update.

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
