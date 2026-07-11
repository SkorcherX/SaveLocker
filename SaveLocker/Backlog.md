# Backlog

Active items only. Completed work is in `logs/sessions.md`.

## Immediate — v0.1.1 bugs (block next release)

See `tasks/001_v0.1.1_bugs.md` for full investigation details and exact file locations.

- **Bug: Agent UI shows "AGENT V0.0.0"** — `build-installer.ps1` passes `--property:Version=$v` but MinVer overrides `AssemblyVersion` separately (still `0.0.0.0` when git is inaccessible in CI). Fix: add `--property:AssemblyVersion=$AppVersion` to the same `dotnet publish` call. Then retag to verify.
- **Bug: No auto-relaunch after silent update** — `skipifsilent` flag removed from `SaveLocker.iss [Run]` section (committed). Needs a real end-to-end update test against a new installed build to confirm.
- **Bug: Uploaded installer lost on Docker update** — `AgentInstallerService` defaults to `AppContext.BaseDirectory/data/agent-installer` (inside container, wiped on update). Fix: add `"AgentInstallerRoot": "/data/agent-installer"` to the `Storage` block in `src/Server/appsettings.json`.

## High priority
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
