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

## .NET 9 is not in the Ubuntu 24.04 archive (WSL)
`sudo apt install dotnet-sdk-9.0` fails with **`Unable to locate package dotnet-sdk-9.0`**. .NET 9 was released *between* Ubuntu LTS releases, so it never landed in the 24.04 archive — apt offers only `dotnet-sdk-8.0` and `dotnet-sdk-10.0`. This is not a reason to switch distro (see `Decisions.md` §6: Ubuntu is chosen for CI parity and its older glibc).
- **Fix (no root):** `bash <(curl -fsSL https://dot.net/v1/dotnet-install.sh) --channel 9.0 --install-dir "$HOME/.dotnet"`, then export `DOTNET_ROOT` + `PATH` in `~/.bashrc`. Also avoids the known packages.microsoft.com ↔ Ubuntu-archive conflict on 24.04.
- **Apt-managed alternative:** `sudo add-apt-repository ppa:dotnet/backports`.
- **Do not** install `dotnet-sdk-10.0` just because apt offers it — the solution targets `net9.0` and EF Core is pinned to 9.0.x to stay off net10.
- The install script **does not resolve dependencies**; .NET needs `libicu` (present by default on Ubuntu 24.04, but check on a minimal image).

## PowerShell escapes with a backtick, not a backslash (cost a mis-installed SDK)
Running WSL commands from PowerShell, `"...\$HOME..."` does **not** escape `$HOME` — backslash is not PowerShell's escape character. PowerShell expands its *own* `$HOME` (`C:\Users\<you>`), eats the backslashes, and bash receives `C:Usersskorc`. This silently installed a .NET SDK into a junk folder **inside the repo** on the Windows drive, with a colon in its name that Windows itself cannot easily delete.
- **Fix:** pass the command in a **single-quoted** PowerShell string so `$VAR` reaches bash untouched, or (best) write a `.sh` file and run `wsl -- bash /mnt/c/path/to/script.sh`. Avoid inline quoting gymnastics entirely.

## Integration suite needs a fresh server DB
`tests/run-agent-tests.ps1` re-runs against whatever state the server already has. Wiping `.verify/` (the agents' configs) without also clearing the server DB makes the agents lose their version lineage while the server keeps its head — the "PC initial push" then legitimately reports CONFLICT and four tests fail for reasons that have nothing to do with your change.
- Run it against an empty `src/Server/localstate/savelocker.db` (delete the `savelocker.db*` files, restart the server, then run) — or don't wipe `.verify/` between runs.
- The suite also needs `%APPDATA%\LGSTestGame` to exist for the detection check; the script now creates it itself (2026-07-12).

## `CommonApplicationData` is `/usr/share` on Linux (agent state)
`Environment.SpecialFolder.CommonApplicationData` is `%PROGRAMDATA%` on Windows but **`/usr/share`** on Linux — not user-writable, and on SteamOS it is the **immutable rootfs, wiped on every update**. `AgentConfig.DefaultDir` used it, so the config, log and offline queue would have gone somewhere that either fails to write or silently vanishes on update.
- **Fixed (2026-07-12):** `DefaultDir` branches — `%PROGRAMDATA%\SaveLocker` on Windows, `$XDG_DATA_HOME` (or `~/.local/share`) `/SaveLocker` on Linux. Never install or store state under `/usr` (`Decisions.md` §5).

## `FileShare` is not enforced on Linux (settle gate)
`FileShare.Read` denying writers is a **Windows kernel semantic**. On Linux the open simply succeeds, so a lock probe written that way returns "nothing is locked" **on every check** — the settle gate would silently degrade to fingerprint-only and could archive a half-written save while a game is still flushing.
- **Fixed:** `FileLockProbe` walks `/proc/*/fd` for descriptors pointing into the save dir and reads `/proc/<pid>/fdinfo/<fd>` `flags` (**octal**; low 2 bits = access mode, `O_WRONLY=1`/`O_RDWR=2`) — what `lsof` does. Where it cannot answer it returns `Unavailable`, which the gate **logs**; it never reports a fictitious all-clear.
- `tests/linux/run-linux-tests.sh` pins this with a writer that writes once and then holds the descriptor open in silence: the fingerprint goes quiet at once, so a broken probe settles in ~3 s and a working one waits the full 8 s.

## Steam shortcut AppIDs: signed in the VDF, unsigned on disk
Steam stores a non-Steam shortcut's generated AppID as a **signed** int32 in `shortcuts.vdf`, but names its prefix directory `compatdata/<unsigned>/`. Using the signed value makes **every** Proton prefix lookup miss silently (it looks for `-1234567890`, the folder is `3060399406`).
- `SteamShortcuts.CompatDataId()` is the single place that converts; the Linux harness fixture uses a deliberately negative AppID so a regression fails the suite.

## `VAR=x out="$(cmd)"` does not export VAR (bash)
A prefix assignment only applies to a **command**; `A=1 out=$(cmd)` is two plain assignments, so `cmd` runs *without* `A` in its environment. This silently defeated the launch-wrapper tests, which exist precisely to check that `STEAM_COMPAT_DATA_PATH` / `SteamAppId` reach the agent.
- **Fix:** `export` them, run, then `unset`.

## git refuses to read the Windows repo from WSL
`git fetch /mnt/e/Projects/SaveLocker` from inside WSL fails with **`detected dubious ownership`** (the DrvFs files appear owned by another user).
- **Fix:** `git config --global --add safe.directory /mnt/e/Projects/SaveLocker/.git`. Fetching *source* across the mount is fine — but the **build and test must still run on ext4** (`~/SaveLocker`), never from `/mnt/*`.

## Docker DB path on existing deployments
The server Docker image defaults to `/data/savelocker.db` but existing deployments that were created before the rename may have `/data/localgamesync.db`. If the server fails to find the DB, either rename the file on the unRAID share or set the `Storage__DbPath` env var.
