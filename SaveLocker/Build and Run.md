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

## Tests

```sh
.\tests\run-agent-tests.ps1   # Windows agent; server must be on :5179
bash tests/linux/run-linux-tests.sh   # Linux agent; starts its own server (run in WSL)
```

Scratch state written to `.verify/` (Windows) and `.verify-linux/` (Linux), both git-ignored.

In-process tests and the multi-folder suite:

```sh
dotnet test tests/SaveLocker.Core.Tests   # archive + agent-core unit tests; some start the server's build output
.	estsun-multipath-tests.ps1           # several save folders: two machines through the real CLI and server
```

**Before running any suite, read [[Gotchas]] → Testing and Test harness.** Two traps recur: clear
`.verify/` and `src/Server/localstate/` *together*, and start the dev server with
`ASPNETCORE_ENVIRONMENT=Development` *and* explicit `Storage__*`, or it opens `/data/savelocker.db`
(i.e. `E:\data\`) and hangs on a stale migrations lock.

### Suite baseline

Quote these as a pair with the date — a bare number means nothing on its own.

| Where | Counts |
|---|---|
| Windows, local | win agent bug bounty **114** (reads **113/114** since 2026-08-14 — see [[Backlog]]) · server bug bounty **164** · agent 47 · delta upload **17** (new 2026-08-22, `run-delta-upload-tests.ps1`) · hardening 33 · local-api 30 · concurrency 23 · health 19 · enrollment 18 · enrollment-TLS 6 |
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
