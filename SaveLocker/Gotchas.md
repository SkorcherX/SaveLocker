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
On **net10.0** since 2026-07-13; EF Core tracks the framework at **10.0.x**. (The old rule — "pin EF
Core to 9.0.x, 10.x requires net10" — is gone: the upgrade removed its reason. See
`Decisions.md → Runtime: .NET 10 LTS`.)

## The SDK is pinned in `global.json` — bump it and the Dockerfile together
`dotnet build` silently uses the **newest SDK installed** unless a `global.json` pins it. The CI
runners preinstall a newer SDK than we target, so before the pin, CI was building the net9.0 targets
with **SDK 10.0.301** while the dev box used 9.0.315. It worked — but a toolchain that silently
differs between CI and dev is exactly the sort of thing that makes a real bug unreproducible.
- `global.json` uses `rollForward: latestFeature`: any `10.0.x` SDK is accepted, but it will never
  roll forward to 11. A box with only the wrong major now fails **loudly**, which is the point.
- The Dockerfile **copies `global.json` in**, so the container is held to the same pin. Bump the pin
  and the `sdk:`/`aspnet:` image tags **together**, or the Docker build fails (loudly — by design).

## SQLitePCLRaw is pinned to 3.x on purpose — do not "simplify" it away
`Microsoft.Data.Sqlite.Core` resolves the SQLitePCLRaw **2.1.11** family, whose native lib bundles a
SQLite vulnerable to **CVE-2025-6965** (NU1903 High — memory corruption when aggregate terms exceed
the available columns). There is **no patched 2.x release**: the fix needs SQLite ≥ 3.50.2, which
only ships in the **3.x** line. `SaveLocker.Server.csproj` therefore pins
`SQLitePCLRaw.bundle_e_sqlite3` directly to lift the whole family (core / provider / config / lib).
- Removing that pin silently reintroduces the CVE — EF Core still resolves 2.1.11 on its own.
- The major bump is safe: the v3 notes state *"there are no code changes in SQLitePCLRaw.core"*, so
  the API `Microsoft.Data.Sqlite` compiles against is unchanged. The v3 breaking changes are the
  removal of classic Xamarin support and of bundles we do not use (`bundle_green`, `bundle_zetetic`…).
- **In v3 the LIB package version tracks SQLite's own version** — which is why it reads `3.53.x`
  rather than `3.0.x`. Do not "fix" that apparent mismatch.
- Verify the fix by asking the engine, not by reading a package number:
  `SELECT sqlite_version()` must be **≥ 3.50.2** (it is 3.50.4).
- Drop the pin only once EF Core resolves 3.x by itself.

## PBKDF2 parameters are part of the on-disk format
`Tokens.HashPassword` stores `v1:{salt}:{hash}` — the iteration count, salt size, hash size and
algorithm are all implied by that `v1` tag, not recorded in it. **Changing any of them invalidates
every stored password**, and the failure only appears in production on the one machine that already
has an admin password set. If they ever must move, bump the version tag and keep a `v1` verification
path. `tests/verify-password-compat.ps1` guards this: it makes a server built from an older ref hash
a password, then asserts the current code still verifies it (and still rejects a wrong one).

## Dev server port
`dotnet run` honours the launch profile (port 5179) unless you pass `--no-launch-profile` and set `ASPNETCORE_URLS` yourself.

## PowerShell + native stderr
Under `$ErrorActionPreference="Stop"`, a native command writing to stderr (e.g. an expected CONFLICT warning) terminates the script. Test scripts use `Continue` and parse output text instead.

## Installing the .NET SDK in WSL (Ubuntu 24.04)
Use the install script, not apt: `bash <(curl -fsSL https://dot.net/v1/dotnet-install.sh) --channel 10.0 --install-dir "$HOME/.dotnet"`, then export `DOTNET_ROOT` + `PATH` in `~/.bashrc`. No root needed, and it avoids the known packages.microsoft.com ↔ Ubuntu-archive conflict on 24.04.
- **Historical (net9 era, now moot):** `apt install dotnet-sdk-9.0` used to fail with `Unable to locate package` — .NET 9 shipped *between* Ubuntu LTS releases and never landed in the 24.04 archive, which offered only `dotnet-sdk-8.0` and `dotnet-sdk-10.0`. The old rule "do not install dotnet-sdk-10.0" is **dead**: net10 is now the target. Ubuntu is still the right distro (`Decisions.md` §6: CI parity + older glibc).
- Both SDKs can coexist; `global.json` decides which one is actually used.
- **Do not** install `dotnet-sdk-10.0` just because apt offers it — the solution targets `net9.0` and EF Core is pinned to 9.0.x to stay off net10.
- The install script **does not resolve dependencies**; .NET needs `libicu` (present by default on Ubuntu 24.04, but check on a minimal image).

## PowerShell escapes with a backtick, not a backslash (cost a mis-installed SDK)
Running WSL commands from PowerShell, `"...\$HOME..."` does **not** escape `$HOME` — backslash is not PowerShell's escape character. PowerShell expands its *own* `$HOME` (`C:\Users\<you>`), eats the backslashes, and bash receives `C:Usersskorc`. This silently installed a .NET SDK into a junk folder **inside the repo** on the Windows drive, with a colon in its name that Windows itself cannot easily delete.
- **Fix:** pass the command in a **single-quoted** PowerShell string so `$VAR` reaches bash untouched, or (best) write a `.sh` file and run `wsl -- bash /mnt/c/path/to/script.sh`. Avoid inline quoting gymnastics entirely.

## Integration suite: clear the server DB and `.verify/` TOGETHER
`tests/run-agent-tests.ps1` re-runs against whatever state both sides already hold — the server's DB **and** `.verify/` (the agents' configs *and their save folders*). The two must be reset **as a pair**. Clearing either one alone produces confident, plausible failures that have nothing to do with your change:

| What you cleared | What breaks | Why |
|---|---|---|
| `.verify/` only | "PC initial push" reports **CONFLICT**, ~4 fail | Agents lost their version lineage; the server kept its head. |
| Server DB only | "Laptop pull restores save" is **BLOCKED**, ~3 fail | `.verify/laptop_save` still holds files from the last run, so the pull correctly refuses to overwrite what looks like un-pushed local progress. |

- **Do:** stop the server, delete `src/Server/localstate/savelocker.db*` **and** `src/Server/localstate/archives/` **and** `.verify/`, restart the server, then run.
- Ordering matters when chaining suites: run `run-agent-tests.ps1` (which wants a fresh DB) **before** `run-enrollment-tests.ps1` (which adds a game and a machine to it).
- The suite also needs `%APPDATA%\LGSTestGame` to exist for the detection check; the script now creates it itself (2026-07-12).

## A green pin/TLS test can be green because it never connected
Verifying TOFU pinning taught this twice (2026-07-13). Plain **http has no server identity to pin**, so an http harness can only assert the agent records *nothing* — every interesting pin assertion passes **vacuously**. And on an https harness, a `status` run against a server with **no games** iterates an empty list, makes **no HTTP request**, completes **no TLS handshake**, and the pin check passes without ever running.
- **Rule:** a test of a connection-time behaviour must assert that a connection actually happened. Give the fixture server a game, and prove the negative case fails (tamper the pin and require the warning).
- `tests/run-enrollment-tls-tests.ps1` needs a trusted dev certificate (`dotnet dev-certs https --trust`), which is why it is a local check and not a CI job.

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

## Never `Path.Combine` a path you are about to persist (archives unreachable cross-OS)
`ArchiveStore.RelativePath` built the store path with `Path.Combine` — and that string is **saved in the DB** as `SaveVersion.ArchivePath`. A **Windows-hosted** server therefore wrote `gameid\versionid.zip`. On Linux a backslash is a legal *filename character*, not a separator, so the lookup misses, `DownloadVersionAsync` returns null, `/download` 404s, and the agent reports **"server has no saves yet; nothing to pull"** — while `status` cheerfully shows a head. It is silent and it looks exactly like data loss.
- Production was accidentally safe (the server only ever runs in Docker/Linux, so it is self-consistent), but **the dev workflow runs the server on Windows** — so a DB or backup moved from a Windows-hosted server to the Docker one has unreachable archives.
- **Fixed (2026-07-13):** the persisted path is always `/`-separated (built by interpolation, never `Path.Combine`); `FullPath` accepts either separator so older Windows-written rows still resolve. Found by the Phase 3 cross-OS round-trip.
- **Rule:** `Path.Combine` is for touching *this* machine's filesystem *now*. Anything that outlives the process — DB column, JSON config, wire DTO — gets a canonical `/`.

## The server-readiness probe must hit an unauthenticated route
`GET /api/games` looks like the obvious "is it up?" probe but it is an **agent** route: without `X-Api-Key` it answers **401**, so a `curl -sf` / `Invoke-RestMethod` readiness loop never sees success and silently burns its entire timeout (60 s per run in `tests/linux/run-linux-tests.sh`) before carrying on anyway.
- Use **`/api/admin/status`** — the only route with no auth filter on it.

## A leaked test server poisons every later run
If a harness spawns the server and then throws *before* the handle reaches its `finally`, the process keeps running — still holding the port **and the state directory**. The next run's `Remove-Item -Force` on the state dir fails silently (files locked), its own server cannot bind, and its readiness probe succeeds **against the stale server**. Every assertion then runs against another run's state, and the resulting conflict looks like a cross-OS hash bug.
- `crossos.ps1` now kills what it spawned on the timeout path, refuses to start when the port is already in use, and refuses to proceed if it cannot clear the state dir. Prefer failing loudly over asserting against someone else's server.

## `Copy-Item -Recurse src dst` copies *into* dst when dst exists
It does not overwrite the destination tree — it nests a copy inside it. A leftover target from a previous run silently accumulates (`local-save/expected/...`), and the test then archives and pushes the polluted tree.
- Always `Remove-Item -Recurse -Force $dst` first.

## Docker DB path on existing deployments
The server Docker image defaults to `/data/savelocker.db` but existing deployments that were created before the rename may have `/data/localgamesync.db`. If the server fails to find the DB, either rename the file on the unRAID share or set the `Storage__DbPath` env var.
