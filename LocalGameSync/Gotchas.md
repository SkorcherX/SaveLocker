# Gotchas

Back to [[Home]]. Traps that have already cost time. Read before touching builds,
paths, or running the server.

## Windows folder case-collision (data loss!)
Windows is **case-insensitive**: `src/Server/Data/` (entity source) and a runtime
`src/Server/data/` (SQLite dir) are the **same directory**. A cleanup
`Remove-Item data -Recurse` once deleted `Data/Entities.cs` + `Data/AppDbContext.cs`.
- **Fixed:** dev storage moved to `localstate/` (see
  `src/Server/appsettings.Development.json`) so no `data/` is ever created in the
  project. Never name a runtime/output dir the same (case-insensitively) as a code
  folder.

## Stale incremental builds
`dotnet build` sometimes did **not** recompile the Server after edits — a stale
DLL got reused and masked changes (e.g. new endpoints 404'd at runtime).
- **When server changes seem ignored:** build with `--no-incremental` and confirm
  the DLL's LastWriteTime is newer than the edited source. The running server used
  `dotnet run --no-build`, so always rebuild first.

## dotnet not on shell PATH
Installed via winget; machine PATH is updated but **open shells don't see it**.
Prepend `"$env:ProgramFiles\dotnet"` to `$env:Path` or open a new shell.

## Agent CLI output
The agent is a WinExe (GUI subsystem). Launching the installed `.exe` from
PowerShell or CMD shows **no stdout/stderr** — the shell doesn't wait for a WinExe
or doesn't pipe its output. Two workarounds:
- **Redirect to file:** `"C:\Program Files\SaveLocker Agent\LocalGameSync.Agent.exe" <cmd> > C:\temp\lgs.txt 2>&1`
- **Read the log** (preferred): `C:\ProgramData\SaveLocker\agent.log` — the
  agent writes all sync events and full exception stack traces there (rolling
  1 MB, keeps one `.old`). Use the `log` CLI sub-command to tail it:
  `LocalGameSync.Agent.exe log > C:\temp\lgs.txt 2>&1`

## OneDrive save paths and RestoreArchive
If a game's save folder is inside an OneDrive-managed tree
(`C:\Users\<name>\OneDrive\…`), the original atomic-swap restore
(`Directory.Move` to rename the target folder aside) fails with
**"Access to the path '…' is denied"** — OneDrive's filesystem reparse points
block the rename even when OneDrive is not running.
- **Fixed (2026-06-23):** `SaveArchive.RestoreArchive` now accepts an optional
  `stagingRoot`; `SyncEngine` passes `_tempDir`
  (`C:\ProgramData\SaveLocker\tmp`) so staging always lives outside the
  OneDrive tree. Restore is file-by-file copy rather than directory rename.
  Verified on Wideboy with Octopath Traveler 0 saves in OneDrive Documents.

## WebView2 sizing at high DPI (WinForms)
`Form.ClientSize` units are **physical pixels** even when `DeviceDpi > 96`. WebView2
divides physical pixels by `devicePixelRatio` (= DeviceDpi ÷ 96) to arrive at CSS pixels.
At 150% DPI (`DeviceDpi = 144`), setting `ClientSize = new Size(900, 600)` produces only
**600×400 CSS pixels** — the React 900 px layout overflowed and only ~62 px of the 212 px
sidebar was visible.

- **Fix in `AgentWindow` constructor:** scale by `DeviceDpi / 96f` so WebView2 gets the
  intended CSS viewport:
  ```csharp
  var dpiScale = DeviceDpi / 96f;
  ClientSize = new Size((int)(900 * dpiScale), (int)(600 * dpiScale));
  ```
- `Application.SetHighDpiMode(HighDpiMode.PerMonitorV2)` does **not** change the
  physical-pixel coordinate behaviour in this scenario — the scale factor must be applied
  explicitly to `ClientSize`.
- Diagnosed via JS executed after `NavigationCompleted`:
  `window.innerWidth`, `window.innerHeight`, `window.devicePixelRatio`.

## FolderBrowserDialog silently returns Cancel from agent API server
`AgentApiServer` handles HTTP requests on ThreadPool (MTA) threads. `FolderBrowserDialog.ShowDialog()`
called on an MTA thread returns `DialogResult.Cancel` immediately without showing anything.
The original fix used `SynchronizationContext.Post()`, but `SynchronizationContext.Current`
is **null** when `TrayApp` constructs — `Application.Run` hasn't installed the
`WindowsFormsSynchronizationContext` yet, so the captured context is the base class which
just queues to the ThreadPool (also MTA).
- **Fixed (2026-06-25):** `ShowFolderPickerAsync` spawns a **dedicated STA thread** per
  call (`thread.SetApartmentState(ApartmentState.STA)`), parents the dialog to
  `Application.OpenForms[0]` so it appears in front of the agent window, and resolves a
  `TaskCompletionSource<string?>` when dismissed. No dependency on the WinForms sync context.

## EF Core version
Pin EF Core to **9.0.x** (`9.0.9` used). 10.x requires net10 and won't restore on net9.

## Dev server port
`dotnet run` honours the launch profile (port 5179) unless you pass
`--no-launch-profile` and set `ASPNETCORE_URLS` yourself.

## PowerShell + native stderr
Under `$ErrorActionPreference="Stop"`, a native command writing to stderr (e.g. an
expected CONFLICT warning) terminates the script. Test scripts use `Continue` and
parse output text instead.
