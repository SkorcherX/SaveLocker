# Gotchas

Traps that have already cost time. Read before touching builds, paths, or the running server.

## Inno `NextButtonClick` fires in `/SILENT` — returning False ABORTS the silent install
Cost the fleet's auto-update between v0.1.6 and v0.1.7 (2026-07-14). The agent auto-updates by running
the installer with **`/SILENT`** (`TrayApp.cs`). The installer-enrollment wizard page validated its
input in `NextButtonClick` and returned `False` (plus a `MsgBox`) when no enrollment file was chosen.
In silent mode Inno **still calls `NextButtonClick`** even though the page is never shown, and there
`False` doesn't "stay on the page" — there is no page — it **terminates Setup**. The abort happens
during page navigation, *before* the install step, so files are not replaced; but `TrayApp` has
already exited to release its mutex, so the agent is left **stopped** until the next login (the Run-key
auto-start) brings it back. Not bricked, but every machine that attempted the update went dark.
- **Fix:** `if WizardSilent then exit;` at the top of `NextButtonClick` — never validate or prompt in
  silent mode. Also `ShouldSkipPage` hides the enroll page entirely on an already-enrolled machine, so
  interactive upgrades don't demand a file either.
- **Rule:** any installer `[Code]` that can block navigation or show UI must guard on `WizardSilent`.
  The agent's most load-bearing behaviour (silent auto-update) exercises exactly that path.

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

## `FileVersionInfo` returns NULL on Linux — the agent reported a fake version for its whole life
Fixed 2026-07-14. `UpdateChecker.CurrentVersion` read the exe's **Win32 version resource** via `FileVersionInfo.GetVersionInfo(Environment.ProcessPath)`. The published `savelocker` is a **native ELF apphost**, which has no such resource: `FileVersion` is `null`, so it silently fell back to the hard-coded `new Version(0, 1, 0)`.
- Consequence: **every Steam Deck reported `v0.1.0` to the console forever**, whatever it was actually running — and Phase 5's heartbeat sends that version to the dashboard, so it was a lie in the UI, not just a cosmetic default.
- **Fix:** try the version resource first (unchanged on Windows, where it is proven), then fall back to the managed **`AssemblyFileVersion` attribute**, which is the value the Win32 resource is generated from and is readable on every platform.
- `savelocker doctor` now prints the version, which is how this is verified end-to-end in CI: `package-linux` installs the tarball and greps for the version it stamped.

## Shell scripts must be LF — a CRLF `install.sh` breaks every Deck
There was **no `.gitattributes`** until 2026-07-14; Git happened to store `packaging/linux/*.sh` as LF, by luck rather than rule. A `.sh` committed with CRLF from a Windows checkout fails on Linux with `bad interpreter: /usr/bin/env bash^M`, and systemd refuses to parse a CRLF unit file.
- `.gitattributes` now pins `*.sh` and `*.service` to `eol=lf`.
- Note the Windows **working tree** is still CRLF (that is correct, and Git converts on commit) — so **copying a `.sh` straight from `E:\` into WSL gives you a CRLF file that bash rejects.** Strip it (`sed -i 's/\r$//'`) when hand-copying for a local test. CI checks out LF and is unaffected.

## Never skip files by `FileAttributes.ReparsePoint` — OneDrive placeholders are reparse points too
The archiver must not follow symlinks (see below). The obvious implementation — skip any entry with `FileAttributes.ReparsePoint` — is **a silent data-loss bug**: OneDrive **files-on-demand placeholders are also reparse points**, so that check would quietly stop archiving every save in a OneDrive folder, and the user would never be told.
- **Use `FileSystemInfo.LinkTarget is not null`** (`SaveArchive.IsLink`). It is non-null only for the *symlink* and *junction* reparse tags — exactly the set we mean — and null for cloud placeholders.
- `tests/run-hardening-tests.ps1` guards this ("ordinary nested files still sync"), but the real OneDrive case cannot be reproduced in the harness. Do not "simplify" that check.

## `Directory.EnumerateFiles(..., AllDirectories)` FOLLOWS symlinks — and the restore pass DELETES
Fixed 2026-07-14 (Phase 6). The default recursive enumeration follows symlinks and junctions, and a Wine prefix is full of them. Two consequences, the second far worse than the first:
- **Archive:** a save folder containing a link to `$HOME` or `/etc` pulls that target **into the archive** and uploads it.
- **Restore (the data-loss one):** `RestoreArchive` deletes target files that are absent from the archive. Walking through a link, it **deletes files outside the save folder entirely.** The pre-fix harness run confirmed this for real — it deleted a file in a sibling directory.
- **Fix:** `SaveArchive.EnumerateFilesNoFollow` / `EnumerateDirsNoFollow` — a manual walk that skips links rather than descending into them. Links are never archived, never restored, and never deleted.
- Windows is affected too, via **junctions** (which need no elevation to create — which is why the harness uses them there).

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
- ⚠️ **Pointing the server at an isolated `Storage__DbPath` counts as "cleared the server DB"** and
  hits row 2 above. That habit is correct for `run-enrollment-tests.ps1` (whose docs call for it) and
  actively wrong here on its own — it resets one half of a pair. Walked into again on 2026-07-19: an
  isolated DB with a stale `.verify/` gave exactly the 3 predicted failures, and they were briefly
  mistaken for a pre-existing bug in the pull path. Removing `.verify/` gave 10/10 with no code change.
- The suite does **not** wipe `.verify/` itself, unlike `.verify-health/` and friends, and that is
  deliberate: wiping it alone against a persistent dev DB triggers row 1 instead. It also shares
  `.verify/` with the two enrollment suites.
- Ordering matters when chaining suites: run `run-agent-tests.ps1` (which wants a fresh DB) **before** `run-enrollment-tests.ps1` (which adds a game and a machine to it).
- The suite also needs `%APPDATA%\LGSTestGame` to exist for the detection check; the script now creates it itself (2026-07-12).

## `dotnet ef` tools must match the EF runtime major — or `migrations remove` eats the WRONG migration
The tool is installed globally and does **not** track the project. With `dotnet-ef` **9.x** against EF Core **10.x** (2026-07-14):
- `migrations add` "succeeds" but writes a model snapshot the runtime rejects. The server then refuses to boot with **`PendingModelChangesWarning`** — the new migration exists, yet the model "has pending changes".
- The recovery reflex, `dotnet ef migrations remove`, then **deleted a different, already-committed migration** (`AddEnrollmentTokens`) instead of the one just added, leaving its table uncreated. Every enrollment endpoint began returning **500 `no such table: EnrollmentTokens`** — a failure that looks nothing like its cause.
- **Fix:** `dotnet tool update --global dotnet-ef --version "10.*"` **first**. Then `git status src/Server/Migrations/` before and after any `migrations remove` — if a file you did not create shows as deleted, restore it (`git checkout --`) and regenerate yours on top.

## Running the server DLL directly ignores `launchSettings.json`
`dotnet run` reads `Properties/launchSettings.json`; **`dotnet bin/.../SaveLocker.Server.dll` does not.** Launching the DLL therefore binds Kestrel's default **:5000** (not :5179) and loads the **Production** config (`Storage:DbPath = /data/savelocker.db`, not `localstate/`). The symptom is a test suite whose every check fails on "connection refused" while a server is plainly running.
- A script that starts the server itself must pass both explicitly: `ASPNETCORE_URLS`, and either `ASPNETCORE_ENVIRONMENT=Development` or the `Storage__*` variables. This is what CI already does.
- A test that needs to **restart** the server must **own** it (its own port + state dir, as `run-health-tests.ps1` does). You cannot correctly restart a server someone else started: its storage path is not knowable from the outside, and guessing it silently brings the server back on an **empty database**.

## PowerShell `.Count` on a single object can hit a DTO field named `count`
`AgentEventDto` has a `count` field (the dedupe counter). Property lookup is case-insensitive, so `$events.Count` on a **single** result returns **the event's dedupe count**, not the number of events — an assertion that then measures the wrong number and can pass while proving nothing.
- Always force a real collection: `@($x | Where-Object {...}).Length`.

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

## The agent's local API is a *management* API — don't treat loopback as authentication
`AgentApiServer` (shared by the Windows tray and the Linux daemon) can re-point this machine at
another server, re-register it, and change what it syncs. It originally shipped unauthenticated with
`AllowAnyOrigin`, and returned the machine's server API key from `/api/state` and `/api/config`.
"It only listens on localhost" is **not** a defence: every process running as that user can reach it,
and so can any web page the user has open — a page can POST to `http://localhost:5178`, and a DNS
rebind makes the socket loopback while the `Host` header still says `evil.com`.
- **Fixed 2026-07-18** (`Decisions.md` §7): 32-byte token in `{configDir}/api-token` (0600) required
  on every `/api/*` call, Host + Origin validated, no CORS policy at all, key never serialized.
- **`--lan` was withdrawn.** It bound all of the above to every interface. It now *exits non-zero*
  rather than being ignored — a silently-accepted flag would leave someone believing they still had
  remote access. Remote access is an SSH tunnel: `ssh -L 5178:localhost:5178 <user>@<host>`.
- ⚠️ **Don't "fix" the UI by re-adding CORS.** The bundled UI is same-origin; if it cannot call the
  API, the cause is a missing token, not a missing CORS header. The token arrives by being injected
  into `index.html` at serve time — so `index.html` must never be served as a plain static file
  (the guard rewrites `/index.html` to `/` for exactly this reason).
- Under `vite dev` the page comes from Vite, not the agent, so the placeholder is never replaced;
  `vite.config.ts` reads the token off disk and injects the header in the proxy instead.

## The agent is two processes — in-process locks and whole-file writes are not enough
Autorun keeps the **daemon** alive while Steam starts **`savelocker run -- %command%`** as a second
process (on Windows: the tray, plus any CLI command). They share `config.json`, `offline-queue.json`,
`health-events.json` and the temp archive dir. A `SemaphoreSlim` or `lock` does **nothing** here.
- **The symptom is a conflict, not a crash.** A daemon that loaded `config.json` at startup and later
  calls `Save()` writes its **stale** copy back, erasing the `LastKnownVersionId` the other process
  just recorded. The next push presents a stale parent, the server rejects it, and the machine
  **conflicts with itself** — identical in the dashboard to the genuine two-machine divergence, so it
  is easy to misdiagnose for a long time. Fixed 2026-07-18 (`Decisions.md` §8).
- **Use `SaveGameSyncState`, not `Save()`, for per-game sync fields.** It re-reads under the lock and
  merges. `Save()` is still last-writer-wins and that is fine only for settings a human edits.
- ⚠️ **Take BOTH locks.** `AgentStateLock` is a `flock`, owned by the *process* — two threads in one
  process both acquire it and neither blocks. The in-process semaphore is still required. Removing
  either one leaves a real hole.
- ⚠️ **A lock timeout deliberately proceeds rather than throwing.** A lock file left by a crashed
  process must not be able to block syncing forever. Do not "harden" this into a hard failure.
- **Never give a temp archive a fixed name.** `{gameId}-push.zip` was shared by every process; the
  first push to finish deleted the other's archive mid-upload. Names carry PID + GUID now.
- **State lives beside its config file**, not in `AgentConfig.DefaultDir`. With `--config` those
  differ, and a process resolving the wrong one keeps a private queue nobody ever drains.

## A concurrency test that races identical short-lived processes proves nothing
The first version of `run-concurrency-tests.ps1` launched four `push` processes at once and asserted
config integrity. It passed **against the broken code**: process startup dominates, so the write
windows never overlapped. The damage needs the real shape — a **long-lived process holding state it
loaded minutes ago** (the daemon) versus a short-lived one. Ordering is then enforced by waiting on
observable state instead of hoping for a race.
- Same trap in the queue check: asserting on a game the daemon *watches* proves nothing, because its
  folder watcher already pulled that game into the daemon's memory. The discriminating case is a game
  **only** the other process ever touches.
- **Rule: revert the fix and confirm the test fails.** Both traps above were caught that way, and
  only that way.

## `dotnet` and `pwsh` look "not installed" in WSL from a non-interactive shell
`wsl -d Ubuntu-24.04 -- bash -lc 'dotnet --version'` reports **command not found** even though the SDK
is installed, because `~/.dotnet` and `~/.local/bin` are only added to PATH by the interactive
profile. It is easy to conclude from this that WSL is unprovisioned and to fall back to
Windows-only testing — which silently skips every Linux-specific behaviour (`flock`, `0600` modes,
the launch wrapper, the whole `tests/linux/` harness).
- Always `export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH` first.
- Quoting through `wsl.exe -- bash -lc '...'` mangles nested `$( )`. Pipe a heredoc to `bash -s` instead.

## A dirty dev DB fails the enrollment suite in a way that looks like a code regression
Starting the server without isolated storage and then running `run-enrollment-tests.ps1` gives
**12/16 failures**, beginning at "mint returns a raw token" — which reads as broken enrollment code.
It is leftover state (an already-redeemed token, an admin password set by an earlier run).
- Always give a test server its own `Storage__DbPath` and `Storage__ArchiveRoot`, the way
  `run-health-tests.ps1` and `run-hardening-tests.ps1` already do. With a clean DB: 16/16.

## A test whose setup silently fails passes VACUOUSLY — assert the setup too
The new write-through-link and zip-bomb checks in `run-hardening-tests.ps1` all passed on the first
run **while testing nothing**. The `Invoke-RestMethod` upload that plants the hostile archive was
404ing inside a bare `catch { }`, so the server had no archive, the pull answered *"server has no
saves yet"*, and every "the outside file was not overwritten" assertion was trivially true.
- **The 404 cause:** resolving the game id by matching `name` against `/api/games` returned **two**
  ids, so `"$server/api/games/$id/upload"` interpolated an array and the route did not match. Take
  the id from the agent's own `config.json` (`.Games[0].GameId`) — that is the id the agent will
  actually pull, so upload and pull cannot disagree.
- **The rule:** every fixture step that must succeed gets its own `Check`. "The hostile archive
  reached the server" is now an assertion, not an assumption.
- **And still revert the fix to confirm the test fails.** These checks flip 7 results against pre-fix
  code, including the one that matters: the file outside the save folder IS overwritten.

## `dotnet build a.csproj b.csproj` does not build both
Passing two project paths to one `dotnet build` silently does not do what it looks like — the second
is not built. In WSL this left a **17-minute-stale `SaveLocker.Shared.dll`** in the agent's output,
so the hardening suite ran against the OLD archive code and reported 7 failures that had already been
fixed. It reads exactly like the fix not working on Linux.
- Build each project in its own `dotnet build` invocation, and when a result is surprising, check the
  output DLL's timestamp before debugging the code.

## The vault can point at a task file that no longer exists
`CONTEXT.md` and `Backlog.md` both named `tasks/linux-kb-articles.md` as the next action for days
after that file was **deleted** (in `ff2c375`), and both listed articles — `deck-supported-games`,
the four §4 edits — that had **already shipped**. Starting from either doc would have meant
rewriting finished work.
- **Check the filesystem before trusting a task pointer.** `ls web/src/help/` answered in one command
  what the vault got wrong in two files.
- A deleted task file is recoverable and worth recovering: `git log --all -- <path>` then
  `git show <commit>:<path>`. That is what established the real remaining scope here.
- The same class of staleness put `v0.1.7` in `CONTEXT.md` while `v0.1.8` was tagged. **This vault
  drifts; verify claims against the repo.**

## `npm run gen:api` in `agent-ui/` targets a REAL running agent
`agent-ui/package.json` hardcodes `openapi-typescript http://localhost:5178/openapi/v1.json`, and
**:5178 is the port an installed agent already listens on.** On a machine where SaveLocker is
installed (i.e. the maintainer's), regenerating agent-UI types silently reads the contract from the
*installed release build* instead of the dev build you just compiled.
- The symptom is not an error. The new schemas are simply **absent** from `src/api-types.ts`, and
  `tsc` then fails on the types you expected to exist — which looks like the endpoint being wrong.
- The local-API test suite already avoids this by using **:5188**; do the same here. Start the dev
  daemon on a free port and generate against it:
  ```
  dotnet src/Agent.Linux/bin/Debug/net10.0/savelocker.dll daemon --port 5190 --config <scratch>/config.json
  cd agent-ui && npx openapi-typescript http://localhost:5190/openapi/v1.json -o src/api-types.ts
  ```
- Before believing a regeneration, grep the output for a symbol you just added.
- Related: a running agent/daemon also **locks the build output DLLs**. `MSB3027 … locked by ".NET
  Host (<pid>)"` means a daemon you started for verification is still alive.

## A test suite that passes may never enter the state where the bug lives
Three of the four bugs found in one afternoon on a real Deck (v0.3.0) were invisible to a **green**
test suite, each for the same structural reason: the suite never put the system in the state where
the failure was possible.

| Bug | The state the suite never entered |
|---|---|
| `savelocker status` 401s | The server had **no admin password**, and `AdminPasswordFilter` passes everything through in that state. The command called an admin route with a machine key and passed anyway. |
| `install.sh` kills the agent (SIGBUS) and exits 0 | Installs went into a **throwaway HOME with nothing running**. The failure needs a *running* daemon holding the files. |
| 3 agent-suite checks "fail" | `.verify/` was reused while the server DB was fresh — the [documented pairing](#integration-suite-clear-the-server-db-and-verify-together) above. The inverse of the same blind spot: state that *was* carried over. |

- **Ask what state the suite never creates.** "No admin password", "nothing running", "empty
  directory" and "first run" are the usual suspects — all of them are the *easy* setup, which is
  exactly why they end up hardcoded into a harness.
- Every one of these was fixed with a test that **enters that state**: set a password and re-run;
  install over a live daemon; reset both halves of the state pair. Each was verified to FAIL against
  the pre-fix code — a regression test that never failed proves nothing.
- Corollary for this project: **the agent has a state the server never sees** (a running process, a
  populated `$HOME`, a stale Proton prefix). CI exercises the wire protocol well and the agent's
  *environment* poorly. Real hardware is still finding things CI cannot.
