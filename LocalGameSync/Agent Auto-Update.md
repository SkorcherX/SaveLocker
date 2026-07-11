# Agent Auto-Update

Back to [[Home]]. Design note for agent versioning and self-update; implementation planned 2026-07-10.

## Problem
The agent has no canonical version string (only a hardcoded `"0.1.0"` in the Inno Setup script) and no way to notify users that a newer version is available or to apply an update automatically.

## Design decisions
- **Update source:** the SaveLocker server (`GET /api/agent/latest`). The admin controls when agents are offered a new version by setting `AgentUpdate:LatestVersion` + `AgentUpdate:DownloadUrl` in `appsettings.json`. Safe default: if the section is absent the endpoint returns 204 and the agent silently skips the check.
- **Update action:** download the new installer to `%TEMP%`, launch it with `/SILENT /FORCECLOSEAPPLICATIONS /NORESTART`, then exit. The existing Inno Setup `AppMutex=SaveLocker.Agent` matches the runtime mutex, so the installer can replace the exe cleanly.
- **Check timing:** on startup (fire-and-forget after tray init) and once every 24 h via a background timer. The user can also trigger a manual check from the tray menu. A 24 h cooldown is enforced via `AgentConfig.LastUpdateCheck` to avoid re-prompting on the same version every poll cycle.

## Version canonicalization
Add `<Version>0.1.0</Version>` to `SaveLocker.Agent.csproj` — the SDK stamps it into `AssemblyVersion`, `FileVersion`, and `AssemblyInformationalVersion`. `build-installer.ps1` reads `FileVersion` from the published exe and passes it to ISCC via `/DAppVersion=`, replacing the hardcoded `#define` in `SaveLocker.iss` with an `#ifndef` guard (so the script still builds standalone).

## Components

### Server: `GET /api/agent/latest` (agent-auth group)
Returns `AgentVersionInfo(LatestVersion, DownloadUrl)` from config, or 204 when unconfigured. DTO added to `SaveLocker.Shared/Contracts.cs`.

### Agent: `UpdateChecker` (new file)
- Reads current version from `Assembly.GetEntryAssembly().GetName().Version`.
- Calls the server endpoint via an `HttpClient` sharing the agent's base URL + API key.
- Compares with `System.Version`. Respects `AgentConfig.SkipVersion`.
- Returns a discriminated result: `UpToDate | UpdateAvailable(version, url) | Skipped | Failed`.
- On `UpdateAvailable`: streams the installer to `%TEMP%\SaveLockerSetup-{version}.exe`, then `Process.Start` with `/SILENT /FORCECLOSEAPPLICATIONS /NORESTART`. Never touches UI.

### Tray menu additions (`TrayApp.cs`)
- New item "Check for Updates" above Exit (relabelled to "Update to v{X.Y.Z}…" when update is pending).
- Balloon tip on update found; balloon click → confirmation dialog with three options: **Update Now**, **Skip This Version**, **Remind Me Later**.
- `AgentConfig` gains two new fields: `SkipVersion` and `LastUpdateCheck`.

### Local agent API (`AgentApiServer.cs`)
New `GET /api/agent-version` route returns `{ currentVersion, latestVersion, updateAvailable }` for the React settings page (reads the last cached `UpdateChecker` result — no extra HTTP call to the server).

## Files changed
| File | Change |
|---|---|
| `src/Agent/SaveLocker.Agent.csproj` | `<Version>0.1.0</Version>` |
| `installer/build-installer.ps1` | inject version into ISCC |
| `installer/SaveLocker.iss` | `#ifndef AppVersion` guard |
| `src/Shared/Contracts.cs` | `AgentVersionInfo` record |
| `src/Server/Program.cs` | `GET /api/agent/latest` |
| `src/Server/appsettings.json` | `AgentUpdate` section (empty defaults) |
| `src/Agent/AgentConfig.cs` | `SkipVersion`, `LastUpdateCheck` |
| `src/Agent/TrayApp.cs` | startup check, 24 h timer, menu item, prompts |
| `src/Agent/AgentApiServer.cs` | `GET /api/agent-version` |
| `src/Agent/UpdateChecker.cs` | **NEW** |

## Verification
1. Set `AgentUpdate:LatestVersion = "9.9.9"` in server `appsettings.json` → launch agent → balloon appears within seconds.
2. Tray → "Check for Updates" when up to date → "You're up to date" balloon.
3. "Skip This Version" → restart agent → no balloon.
4. `GET /api/agent/latest` with blank config → 204.
5. `GET /api/agent-version` (local) → correct `currentVersion`.
6. `build-installer.ps1` → installer title shows version from csproj.
