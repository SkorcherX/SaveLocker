# Gotchas

Traps that have already cost time. Read before touching builds, paths, or the running server.

## Stale incremental builds (most common)
`dotnet build` sometimes did **not** recompile the Server after edits — a stale DLL got reused and masked changes (e.g. new endpoints 404'd at runtime).
- **Always build with `--no-incremental`** and stop the running agent/server first (they lock the DLLs).
- Confirm the DLL's `LastWriteTime` is newer than the edited source.

## Windows folder case-collision (data loss!)
Windows is **case-insensitive**: `src/Server/Data/` (entity source) and a runtime `src/Server/data/` (SQLite dir) are the **same directory**. A `Remove-Item data -Recurse` once deleted `Data/Entities.cs` + `Data/AppDbContext.cs`.
- **Fixed:** dev storage moved to `localstate/` (see `src/Server/appsettings.Development.json`) so no `data/` is ever created in the project. Never name a runtime/output dir the same (case-insensitively) as a code folder.

## dotnet not on shell PATH
Installed via winget; machine PATH is updated but **open shells don't see it**.
Prepend `"$env:ProgramFiles\dotnet"` to `$env:Path` or open a new shell.

## Agent CLI output
The agent is a WinExe (GUI subsystem). Launching the installed `.exe` from PowerShell or CMD shows **no stdout/stderr**. Two workarounds:
- **Redirect to file:** `"C:\Program Files\SaveLocker Agent\SaveLocker.Agent.exe" <cmd> > C:\temp\sl.txt 2>&1`
- **Read the log** (preferred): `%PROGRAMDATA%\SaveLocker\agent.log` — rolling 1 MB, keeps one `.old`. Tail it with the `log` CLI sub-command: `SaveLocker.Agent.exe log > C:\temp\sl.txt 2>&1`

## OneDrive save paths and RestoreArchive
If a game's save folder is inside an OneDrive-managed tree (`C:\Users\<name>\OneDrive\…`), `Directory.Move` fails with **"Access to the path '…' is denied"** — OneDrive's reparse points block the rename even when OneDrive is not running.
- **Fixed (2026-06-23):** `SaveArchive.RestoreArchive` accepts an optional `stagingRoot`; `SyncEngine` passes `_tempDir` (`C:\ProgramData\SaveLocker\tmp`) so staging lives outside the OneDrive tree. Restore is file-by-file copy rather than directory rename.

## WebView2 sizing at high DPI (WinForms)
`Form.ClientSize` units are **physical pixels** even when `DeviceDpi > 96`. WebView2 divides by `devicePixelRatio` (= DeviceDpi ÷ 96) to get CSS pixels. At 150% DPI (`DeviceDpi = 144`), `ClientSize = new Size(900, 600)` produces only **600×400 CSS px** — the React layout overflowed.
- **Fix in `AgentWindow` constructor:**
  ```csharp
  var dpiScale = DeviceDpi / 96f;
  ClientSize = new Size((int)(900 * dpiScale), (int)(600 * dpiScale));
  ```
- `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` does **not** change the physical-pixel coordinate behaviour here — the scale factor must be applied explicitly.

## FolderBrowserDialog silently returns Cancel from agent API server
`AgentApiServer` handles HTTP requests on ThreadPool (MTA) threads. `FolderBrowserDialog.ShowDialog()` called on an MTA thread returns `DialogResult.Cancel` immediately without showing anything. `SynchronizationContext.Post()` doesn't work either because `SynchronizationContext.Current` is **null** when `TrayApp` constructs — `Application.Run` hasn't installed the `WindowsFormsSynchronizationContext` yet.
- **Fixed (2026-06-25):** `ShowFolderPickerAsync` spawns a **dedicated STA thread** per call, parents the dialog to `Application.OpenForms[0]`, resolves a `TaskCompletionSource<string?>` when dismissed.

## EF Core version
Pin EF Core to **9.0.x**. 10.x requires net10 and won't restore on net9.

## Dev server port
`dotnet run` honours the launch profile (port 5179) unless you pass `--no-launch-profile` and set `ASPNETCORE_URLS` yourself.

## PowerShell + native stderr
Under `$ErrorActionPreference="Stop"`, a native command writing to stderr (e.g. an expected CONFLICT warning) terminates the script. Test scripts use `Continue` and parse output text instead.

## Integration suite needs a fresh server DB
`tests/run-agent-tests.ps1` re-runs against whatever state the server already has. Wiping `.verify/` (the agents' configs) without also clearing the server DB makes the agents lose their version lineage while the server keeps its head — the "PC initial push" then legitimately reports CONFLICT and four tests fail for reasons that have nothing to do with your change.
- Run it against an empty `src/Server/localstate/savelocker.db` (delete the `savelocker.db*` files, restart the server, then run) — or don't wipe `.verify/` between runs.
- The suite also needs `%APPDATA%\LGSTestGame` to exist for the detection check; the script now creates it itself (2026-07-12).

## Docker DB path on existing deployments
The server Docker image defaults to `/data/savelocker.db` but existing deployments that were created before the rename may have `/data/localgamesync.db`. If the server fails to find the DB, either rename the file on the unRAID share or set the `Storage__DbPath` env var.
