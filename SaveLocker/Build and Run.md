# Build and Run

Full deployment reference also in `README.md`.

## Prereqs
- .NET 9 SDK. If `dotnet` isn't found in an open shell: `$env:Path = "$env:ProgramFiles\dotnet;" + $env:Path` (or open a new shell).
- Node.js 22 (for React dashboard dev server and agent UI builds).
- Inno Setup 6 (for installer builds): `winget install JRSoftware.InnoSetup`.

## Build

```sh
# Full solution
dotnet build SaveLocker.sln

# Force server recompile (use this when server edits seem ignored — see Gotchas.md)
dotnet build src/Server/SaveLocker.Server.csproj --no-incremental
```

**Stop the running agent/server before building** — they lock the DLLs.

## Run server (local dev)

```sh
cd src/Server
dotnet run
# API: http://localhost:5179   Health: /health   Swagger: /swagger
```

Dev state under `src/Server/localstate/` (db + archives). Deterministic test run:

```sh
$env:ASPNETCORE_URLS="http://localhost:5179"
$env:Storage__DbPath="...\some\path\db.sqlite"
$env:Storage__ArchiveRoot="...\some\path\archives"
dotnet run --no-build --no-launch-profile
```

## Run React dashboard (local dev)

```sh
cd web
npm run dev
# Dashboard: http://localhost:5173 (proxies /api + /art to :5179)
# LAN access: http://192.168.68.58:5173 (Vite bound to 0.0.0.0)
```

Server must be running for API calls to work. Two Vite processes will claim ports 5173 + 5174 — kill duplicates with `Get-Process node | Stop-Process`.

## Run agent (local dev)

```sh
dotnet build src/Agent/SaveLocker.Agent.csproj --no-incremental
# Then launch the built exe, or run via dotnet for console output:
dotnet src/Agent/bin/Debug/net10.0-windows/SaveLocker.Agent.dll
```

Agent UI served at http://localhost:5178.

## Deploy on unRAID (Docker / CI)

`git push` → GitHub Actions builds and pushes `ghcr.io/skorcherx/savelocker:latest`. To deploy on unRAID:

```sh
docker compose pull && docker compose up -d
```

Manual one-off build (no Compose):
```sh
docker build -t savelocker -f src/Server/Dockerfile .
docker run -d --name savelocker-server -p 5080:8080 \
  -v /mnt/user/appdata/savelocker:/data savelocker
```

Dashboard: `http://unraid-ip:5080`.

| Setting | Env var | Default (Docker) |
|---|---|---|
| SQLite path | `Storage__DbPath` | `/data/savelocker.db` |
| Archive root | `Storage__ArchiveRoot` | `/data/archives` |
| Art root | `Storage__ArtRoot` | `/data/art` |
| Backup root | `Storage__BackupRoot` | `/data/backups` |
| Installer root | `Storage__AgentInstallerRoot` | `/data/agent-installer` |
| Versions kept/game | `Storage__RetainVersionsPerGame` | `10` |

> **Existing Docker deployments** may have `/data/localgamesync.db` from before the rename. Either rename the file on the unRAID share or set `Storage__DbPath=/data/localgamesync.db`.

### Rolling a release out to the fleet

Console and agents are two halves and a release usually needs both — doing one and not the other
leaves Config reporting a version split.

- **Neither fleet is offered anything until the package is uploaded in Config → Agent updates. The
  GitHub Release asset is NOT automatically what agents are offered.** This is the step that gets
  missed, and since 2026-08-15 there are **two rows** to fill — Windows and Linux.
- **Windows agents** then prompt in the tray and install when the user accepts.
- **Decks** then update themselves: the daemon stages the tarball and installs it at its next start.
  A hand-run `install.sh` still works and supersedes anything staged. **The first** update onto a
  pre-0.5.5 Deck still has to be done by hand, because the agent that would fetch it is the one
  being replaced.
- **Console** redeploy is the `docker compose pull` above.

**Migrations:** only **v0.5.0** ever broke rollback — two schema migrations run on first start, so
**back up `/data` first** when crossing that boundary from anything older. Every release since has
been a clean container downgrade.

## Build agent installer

```sh
.\installer\build-installer.ps1
# Output: installer/dist/SaveLocker-Agent-Setup-{version}.exe (~47 MB, self-contained)
```

Stop the running agent first (holds the `SaveLocker.Agent` AppMutex). Version is derived by MinVer from the nearest git tag — push a `v*` tag to trigger the GitHub release CI instead of building locally for a real release.

## Linux agent (Proton / Steam Deck)

Build and test **inside WSL, on the ext4 home** (`~/SaveLocker`) — never from `/mnt/*`, where DrvFs breaks inotify, permissions, case-sensitivity and locking. `dotnet` lives in `~/.dotnet` and is not on a login shell's PATH; export it (or run a `.sh` file rather than fighting inline quoting — see `Gotchas.md`).

```sh
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
dotnet build src/Agent.Linux/SaveLocker.Agent.Linux.csproj --no-incremental

# Fake-game harness: no Steam, no Proton, no GPU, no Deck required.
# Starts its own server on :5179 with a throwaway DB, and runs against a fake HOME.
bash tests/linux/run-linux-tests.sh          # 216 checks
```

To pull Windows-side commits into the WSL clone:
```sh
git fetch /mnt/e/Projects/SaveLocker main && git checkout FETCH_HEAD
```

Game Mode UI, in a real window under WSLg — the loop that saves a Deck trip:
```sh
bash tests/linux/run-ui-wslg.sh --fixtures                     # populated screens, interactive
bash tests/linux/run-ui-wslg.sh --fixtures --nav-debug \
     --nav right --pointer 530,537 --screenshot /tmp/x.png     # scripted, unattended
```
`--nav` replays D-pad presses, `--nav-debug` overlays the live nav cursor, and `--pointer X,Y` parks
a stationary pointer. The last one exists because WSLg leaves the real pointer outside the window
while a Deck always has one — without it, hover-vs-focus bugs cannot reproduce off-device, and an
A/B comes back identical while proving nothing.

Release build (self-contained — SteamOS ships no .NET runtime):
```sh
bash packaging/linux/build-linux.sh          # -> artifacts/linux/savelocker-linux-x64.tar.gz
```
Build on the **oldest glibc** you intend to support (Ubuntu 24.04 → Deck is forward-compatible; the reverse is not).

## Decky plugin

**Lives in its own repository:** <https://github.com/SkorcherX/SaveLocker-Decky>. It is not a
subdirectory here and must not become one again — Decky's plugin database tracks plugins as
submodules whose *root* is the plugin, so a monorepo subdirectory cannot be packaged or listed.

Nothing in this repo depends on it *working*. The rule it relies on lives in
`Agent.Core/LaunchOptions.cs` and is covered by `run-linux-tests`; the plugin only reads the agent's
API and writes what it is told. Its own repo carries the build, the release workflow and the
install/debug notes.

**This repo does, however, distribute it.** The server hosts its release zip in a third installer
slot (`?platform=decky-plugin`, from `SkorcherX/SaveLocker-Decky`), and the Linux agent replaces the
files under `~/homebrew/plugins/SaveLocker` from it — see `Agent.Linux/DeckyPlugin.cs` and the
constraints in [[Gotchas]] → *Decky plugin*. To exercise that here, a fake plugin directory in the
fixture HOME is all `run-linux-tests` needs; no Decky, no Deck. To publish a plugin release to a real
server, upload the zip in **Config → Agent updates → Decky plugin** (type the version — Decky's
artifact is always `SaveLocker.zip`) or let the GitHub fetch read the tag.

## Throwaway test rig (`testenv`)

`.\tests\testenv.ps1 <command>`, run from Windows, drives a whole disposable fleet — a Windows
agent, a headless WSL agent, a dashboard container and (optionally) a real Steam Deck — all running
**beside**, never instead of, the installed release agent and any real save data. Commands:
`build`, `up`, `down`, `status`, `test`, `sync`, `logs`, `clean`. `-Only windows|linux|deck|console`
scopes `build`/`up` to one target; `down`/`status`/`logs`/`clean` always touch everything that's
configured. Isolation is `SAVELOCKER_STATE_ROOT`/`SAVELOCKER_TRAY_PORT`/`SAVELOCKER_RUNKEY_SUBPATH`
on Windows and `XDG_DATA_HOME` on Linux ([[Gotchas]] → *Test-only environment variables*) — never
the flags a real user session reads.

`clean` is the one command `down` is not: `down` only **stops** processes, and can leave a stale
container, its image and its data volume, and every test agent's on-disk state, behind. `clean`
stops everything first and then **deletes** it — Windows' whole `-StateRoot`, the WSL rig's state
dir, the Deck's deployed build and state, and the dashboard's container/image/volume. Rebuilding
after a `clean` is a plain `build` + `up` — nothing about the rig is meant to survive it, which is
the point of calling it something other than `down`.

**Steam Deck target.** Unlike Windows/WSL there is no "this machine" default. Either copy
`tests/testenv.local.ps1.example` to `tests/testenv.local.ps1` (gitignored — it names your LAN, not
the project's) and fill in the two lines by hand, or write it with the rig itself:

```powershell
.\tests\testenv.ps1 deck-config -DeckHost deck@192.168.68.67 -DeckServerUrl http://192.168.68.58:5080
.\tests\testenv.ps1 deck-config                                  # prints what's currently saved
.\tests\testenv.ps1 deck-config -DeckHost deck@192.168.68.99      # updates just the host, keeps the URL
```

`testenv.ps1` loads `testenv.local.ps1` automatically on every run, so a DHCP lease moving the
Deck's IP means one `deck-config` call (or one edited line), not a shell profile or a re-typed env
var. An explicit `-DeckHost`/`-DeckServerUrl` on the command line always wins over the saved file.

**Authentication is whatever your `ssh`/`scp` already do — the script never sees or stores a
password.** Every Deck call shells out to the Windows OpenSSH client exactly as if you'd typed it:
with a key in `~/.ssh/authorized_keys` on the Deck it connects silently; without one, `ssh`/`scp`
fall back to an interactive password prompt right there in the console, which works but asks again
on every single call (`up` alone makes three). Key auth is worth setting up once:

```powershell
ssh-keygen -t ed25519                                    # skip if you already have a key
Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub | ssh deck@<deck-ip> "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys"
ssh deck@<deck-ip> echo ok                                # should now connect with no prompt
```

The last command asks for the Deck's password once, to write `authorized_keys`; nothing after that
does. SSH must already be enabled on the Deck (Desktop Mode → Konsole → `passwd` to set one if
you haven't, then `sudo systemctl enable --now sshd`).

`build -Only deck` cross-compiles a **self-contained** linux-x64 publish in WSL (the same packer
`packaging/linux/build-linux.sh` uses for a real release, just stamped with the test version) —
SteamOS ships no .NET runtime, so the WSL target's ordinary framework-dependent `dotnet build`
cannot run there. `up -Only deck` (or plain `up`/`build` once `SAVELOCKER_DECK_HOST` is set) `scp`s
the tarball to the Deck and installs it under `~/savelocker-test` with state in the sibling
`~/savelocker-test-state` — never `~/.local/share/SaveLocker`, which is both the real install's
binaries *and* its state ([[Gotchas]] → *Linux agent*). Leaving `SAVELOCKER_DECK_HOST` unset skips
the Deck silently wherever `-Only` defaults to `all`; passing `-Only deck` explicitly without it is
an error instead of a silent no-op. Because the Deck is only awake when woken (see the Deck
handoff section in [[CONTEXT]]), every SSH/`scp` call has a short connect timeout and reports
"unreachable" rather than hanging or aborting the rest of the rig.

## Tests

```sh
.\tests\run-agent-tests.ps1   # Windows agent; server must be on :5179
bash tests/linux/run-linux-tests.sh   # Linux agent; starts its own server (run in WSL)
```

Scratch state written to `.verify/` (Windows) and `.verify-linux/` (Linux), both git-ignored.

**Before running any suite, read [[Gotchas]] → Testing and Test harness.** Two traps recur: clear
`.verify/` and `src/Server/localstate/` *together*, and start the dev server with
`ASPNETCORE_ENVIRONMENT=Development` *and* explicit `Storage__*`, or it opens `/data/savelocker.db`
(i.e. `E:\data\`) and hangs on a stale migrations lock.

### Suite baseline

Quote these as a pair with the date — a bare number means nothing on its own.

| Where | Counts |
|---|---|
| Windows, local | win agent bug bounty **114** (reads **113/114** since 2026-08-14 — see [[Backlog]]) · server bug bounty **164** · agent 47 · hardening 33 · local-api 30 · concurrency 23 · health 19 · enrollment 18 · enrollment-TLS 6 |
| Linux, local (WSL ext4) | `run-linux-tests` **63** on `main`, **69** at `4c9f5f5`, **84** after Phase 2, **117** after Phase 3, **123** after Phase 4 of the auto-update work, **137** after Phase 1, **154** after Phase 2, **161** after the Deck hardware pass, **197** after Phase 5 and **208** once the agent UI read the plugin's state live, both of `logs/2026-08-15_decky-plugin.md`, then **216** after `logs/2026-08-15_install-update-now.md` (2026-08-15, same clone) |
| Linux, in CI | agent 43 · hardening 37 · local-api 30 · concurrency 23 · health 19 · enrollment 16 |
| Detection | sweep **271/298 (90.9%)** at the default 300 sample, 17 pinned |

The two platforms differ by design — each suite skips the other's cases. The detection drop from
99.0% is the install-directory guard and is deliberate ([[Backlog]] → file-level saves); `main` reads
394/396 at a **400**-sample run, so quote the sample size or the two are not comparable.

Server build 0/0. Console lint and build clean. The Windows agent build has **one** pre-existing
warning (MSB3277, a WindowsBase 4.0/5.0 conflict from WebView2) — a second one means something.

`run-winagent-tests.ps1` owns :5189 (+ :5190–:5198) and `.verify-winagent`. Slow by design (~5 min,
mostly real waits on lease renewal, lock contention and WebView2 startup). **It drives two real tray
processes**, so it needs an interactive desktop session and silently skips those blocks without one.

## Regenerate OpenAPI types (web dashboard)

```sh
# 1. Start the server: cd src/Server && dotnet run
# 2. In another terminal:
cd web && npm run gen:api
# Regenerates web/src/api-types.ts from http://localhost:5179/openapi/v1.json
```

Commit the updated `src/Server/openapi.json` snapshot alongside any API changes.
