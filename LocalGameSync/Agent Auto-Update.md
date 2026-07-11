# Agent Auto-Update

Back to [[Home]]. Full design + implementation for agent versioning and self-update. Initial implementation 2026-07-10; extended with MinVer, release CI, server-hosted installer, and console management UI 2026-07-11.

## Problem
The agent had no canonical version string (only a hardcoded `"0.1.0"` in the Inno Setup script) and no way to notify users that a newer version is available or to apply an update automatically.

## Versioning — MinVer (2026-07-11)
Version is now **derived from git tags** via the [MinVer](https://github.com/adamralph/minver) SDK package. The manual `<Version>0.1.0</Version>` in `SaveLocker.Agent.csproj` has been removed.

- Tag `v0.2.0` on a commit → that build produces `0.2.0` in all assembly version fields.
- Between tags (dev builds) → `0.2.0-alpha.3+abc1234` (commit count + short hash).
- `MinVerMinimumMajorMinor=0.1` ensures untagged builds floor at `0.1.x-alpha` instead of `0.0.0-alpha`.

`build-installer.ps1` reads `FileVersion` from the published exe (which MinVer stamps as `major.minor.patch.0`) and passes it to ISCC via `/DAppVersion=`. `SaveLocker.iss` uses an `#ifndef AppVersion` guard so standalone ISCC builds default to `"dev"`. No other script changes needed.

**To cut a release:**
```powershell
git tag v0.2.0
git push origin v0.2.0
# → triggers the release CI workflow (see below)
```

## Release CI — GitHub Actions (2026-07-11)
`.github/workflows/release.yml` fires on any `v*` tag push. Runs on `windows-latest`:
1. `actions/checkout@v4` with `fetch-depth: 0` (MinVer needs full history).
2. `actions/setup-node@v4` + `actions/setup-dotnet@v4`.
3. `choco install innosetup -y`.
4. `.\installer\build-installer.ps1`.
5. `softprops/action-gh-release@v2` — creates a GitHub Release and attaches `SaveLocker-Agent-Setup-{version}.exe` with auto-generated notes.

Regular `main` branch pushes (server Docker CI) are unaffected — they use the existing `docker-publish.yml`.

## Update source: server-hosted installer (2026-07-11)
The server now **stores and serves the installer binary itself**, replacing the original static-config approach. The admin uploads the installer through the dashboard; agents download it directly from the server. The config fallback (`AgentUpdate:LatestVersion` / `AgentUpdate:DownloadUrl`) still works for backward compatibility or external URLs.

### Server: `AgentInstallerService` (new)
`src/Server/Services/AgentInstallerService.cs` — manages the installer binary on disk.
- Storage root: `Storage:AgentInstallerRoot` config key (default `data/agent-installer/`).
- Stores one `*.exe` + an `installer-info.json` sidecar `{ version, fileName, uploadedAt, sizeBytes }`.
- On upload: deletes any previous exe before writing the new one (only one version hosted at a time).
- `GetInfo()` / `SaveAsync()` / `Delete()` / `GetInstallerPath()`.

### Server: `GET /api/agent/latest` (updated)
Now checks the filesystem first (via `AgentInstallerService`). If an installer is hosted:
- Returns `AgentVersionInfo(info.Version, "{scheme}://{host}/api/agent/installer/download")`.

If no installer on disk, falls back to the static `AgentUpdate:LatestVersion` + `DownloadUrl` config. If neither is set, returns 204 (agent skips silently).

### Server: installer management endpoints (admin-auth)
- `GET /api/admin/agent-installer` → `AgentInstallerStatus { version, fileName, uploadedAt, sizeBytes }` or 204.
- `POST /api/admin/agent-installer?version={v}` (multipart `file` field) → `AgentInstallerStatus`. Replaces the previous installer.
- `DELETE /api/admin/agent-installer` → 204. Removes the hosted installer; agents stop being offered updates.

### Server: `GET /api/agent/installer/download` (public)
Streams the hosted installer binary. Public (no auth) so:
- Agents download it automatically (their `UpdateChecker._http` sends the `X-Api-Key` header regardless, but the endpoint doesn't require it).
- The admin can also use it as a direct download link from the dashboard.
Returns 404 if no installer is hosted.

### `AgentInstallerStatus` DTO
Added to `src/Shared/Contracts.cs`: `record AgentInstallerStatus(string Version, string FileName, DateTime UploadedAt, long SizeBytes)`.

## Console "Agent Updates" card (2026-07-11)
New card in the Configuration page (`web/src/components/ConfigView.tsx`):
- **Status row** — shows hosted version badge, filename, size, upload date, "Download ↓" link, and a **Delete** button. Shows "none — agents won't be offered updates" when empty.
- **Upload form** — file picker (`.exe`), version field (auto-populated by parsing the filename `Setup-{version}.exe`; overridable), **Upload installer** button with disabled/loading state.
- ConfigView manages its own installer state via `useEffect` on mount; independent of the main 15 s polling loop.
- New API calls in `web/src/api.ts`: `installerStatus()`, `uploadInstaller(formData, version)`, `deleteInstaller()`.
- `AgentInstallerStatus` added to `web/src/types.ts` (hand-written; run `npm run gen:api` after server update to sync the generated file).

## Live version string in agent UI (2026-07-11)
`GET /api/agent/latest` and `GET /api/agent-version` already existed. The `/api/state` response now also includes `currentVersion` (read from `UpdateChecker.CurrentVersion` — the `Assembly.GetEntryAssembly().GetName().Version` stamped by MinVer). The React UI (`agent-ui/src/App.tsx`) replaces the hardcoded `"Agent v1.0"` subtitle with `Agent v{state?.currentVersion ?? '…'}`. `AgentState` type in `agent-ui/src/types.ts` gains `currentVersion: string`.

## Agent: `UpdateChecker` (original implementation, 2026-07-10)
- `UpdateChecker.CurrentVersion` — `Assembly.GetEntryAssembly().GetName().Version` (stamped by MinVer).
- `CheckAsync()` — calls `GET /api/agent/latest`, compares with `System.Version`, respects `AgentConfig.SkipVersion`. Returns discriminated result: `UpToDate | Available(version, url) | Skipped | Failed`.
- `DownloadInstallerAsync()` — streams installer to `%TEMP%\SaveLockerSetup-{version}.exe`, then `Process.Start` with `/SILENT /FORCECLOSEAPPLICATIONS /NORESTART`. Never touches UI.

## Tray UX (`TrayApp.cs`)
- Startup check fires 5 s after launch (fire-and-forget).
- `System.Threading.Timer` re-checks every 24 h; 24 h cooldown via `AgentConfig.LastUpdateCheck`.
- Tray menu: "Check for Updates" (relabelled to "Update to v{X.Y.Z}…" + bold when update pending).
- Balloon tip on update found; balloon click → confirm dialog: **Update Now / Skip This Version / Remind Me Later**.

## Local agent API (`AgentApiServer.cs`)
`GET /api/agent-version` → `{ currentVersion, latestVersion, updateAvailable }` for the React settings page. Also `currentVersion` is now included in `GET /api/state` (no extra call needed from the UI).

## Files changed summary
| File | Change |
|---|---|
| `src/Agent/SaveLocker.Agent.csproj` | Removed `<Version>`, added MinVer package + `MinVerMinimumMajorMinor` |
| `.github/workflows/release.yml` | **NEW** — tag-triggered installer build + GitHub Release |
| `installer/build-installer.ps1` | Reads `FileVersion` from published exe (unchanged — already did this) |
| `installer/SaveLocker.iss` | `#ifndef AppVersion` guard (unchanged from 2026-07-10) |
| `src/Shared/Contracts.cs` | `AgentVersionInfo` (2026-07-10) + `AgentInstallerStatus` (2026-07-11) |
| `src/Server/Services/AgentInstallerService.cs` | **NEW** |
| `src/Server/Program.cs` | Updated `/api/agent/latest`; added 3 admin + 1 public installer routes |
| `src/Server/appsettings.json` | `AgentUpdate` section (empty defaults, 2026-07-10) |
| `src/Agent/AgentConfig.cs` | `SkipVersion`, `LastUpdateCheck` (2026-07-10) |
| `src/Agent/TrayApp.cs` | Startup check, 24 h timer, menu item, prompts (2026-07-10) |
| `src/Agent/AgentApiServer.cs` | `GET /api/agent-version` (2026-07-10); `currentVersion` in `/api/state` (2026-07-11) |
| `src/Agent/UpdateChecker.cs` | **NEW** (2026-07-10) |
| `agent-ui/src/types.ts` | `currentVersion` field in `AgentState` |
| `agent-ui/src/App.tsx` | Live version string from state |
| `web/src/types.ts` | `AgentInstallerStatus` interface |
| `web/src/api.ts` | `installerStatus`, `uploadInstaller`, `deleteInstaller` |
| `web/src/components/ConfigView.tsx` | "Agent Updates" card |

## Admin workflow (end-to-end)
1. Cut a release: `git tag v0.2.0 && git push origin v0.2.0`.
2. GitHub Actions builds `SaveLocker-Agent-Setup-0.2.0.exe` and publishes a GitHub Release.
3. In the dashboard → Configuration → **Agent Updates**: upload the exe (version auto-parsed from filename).
4. Connected agents check in within 24 h (or manually via "Check for Updates" tray item) and are offered the update.
5. Agent downloads from `GET /api/agent/installer/download`, launches silently, exits.
6. To roll back: upload an older installer (previous version) or click Delete to stop offering updates.

## Verification (original 2026-07-10)
1. Set `AgentUpdate:LatestVersion = "9.9.9"` in server `appsettings.json` → launch agent → balloon appears within seconds.
2. Tray → "Check for Updates" when up to date → "You're up to date" balloon.
3. "Skip This Version" → restart agent → no balloon.
4. `GET /api/agent/latest` with blank config → 204.
5. `GET /api/agent-version` (local) → correct `currentVersion`.
6. `build-installer.ps1` → installer filename shows MinVer-stamped version.
