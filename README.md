# SaveLocker

**Save-game sync for games without cloud saves.** A Windows tray agent watches your save folders, pushes archives to a self-hosted server after each session, and pulls the latest save before you launch — so you can switch between machines without thinking about it.



---

![SaveLocker Dashboard](docs/screenshots/dashboard.png)

## Features

- **Automatic sync** — watches save folders, pushes on file-settle after the game exits and pulls before launch
- **Lease / checkout model** — one machine holds an exclusive lease while a game is running; other machines are warned before they can stomp each other's progress; lease auto-renews every 3 hours so long sessions never silently expire
- **Conflict detection** — content-hash comparison on every upload; diverged saves are flagged as conflicts rather than silently overwritten; resolve in the dashboard (pick a winner, roll back to any prior version)
- **Offline retry queue** — pushes that fail while the server is unreachable are queued to disk and automatically drained when the connection comes back
- **Game auto-detection** — reads the community [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest) to resolve save paths for thousands of games; also scans Steam shortcuts and installed titles
- **Per-machine save paths** — each machine stores its own resolved path; the server suggests a canonical path that agents auto-adopt when it exists locally
- **Web dashboard** — React SPA served by the server container; browse games, cover art, version history, per-machine activity, conflict resolution, audit log, and configuration
- **Agent UI** — React app served locally by the agent (port 5178, opened in an embedded WebView2 window); enrol games, manage settings, view sync status
- **Per-game retention limits** — keep *N* versions per game (global default 10, overridable per game); manual version delete
- **Admin password auth** — dashboard protected by PBKDF2-SHA256 password; unprotected on first run
- **Audit log** — every push, pull, lease, conflict, and admin action is recorded with machine + game + timestamp
- **Cover art** — fetches grid / hero / logo / icon from [SteamGridDB](https://www.steamgriddb.com/) and caches them in the server
- **Windows installer** — Inno Setup, machine-wide install to `Program Files`, optional auto-start on login, full uninstall reverts every system change

---

## Architecture

```
  ┌─────────────────────────────┐
  │  Windows machine (agent)    │
  │  ┌───────────────────────┐  │
  │  │  SaveLocker tray app  │  │
  │  │  React UI (WebView2)  │  │
  │  │  Folder watchers      │  │         ┌──────────────────────────────┐
  │  │  Process watcher      │  │◄──────► │  Server (Docker / unRAID)    │
  │  │  Offline queue        │  │  HTTP   │  ASP.NET Core  +  SQLite     │
  │  └───────────────────────┘  │         │  Archive store (zip files)   │
  └─────────────────────────────┘         │  React dashboard (wwwroot)   │
                                          └──────────────────────────────┘
  ┌─────────────────────────────┐                      ▲
  │  Windows machine (agent)    │◄────────────────────►│
  └─────────────────────────────┘
```

The server is the single source of truth. Agents only make outbound HTTP calls (no inbound ports required — works through NAT/firewalls). The agent polls for queued commands every ~20 seconds so the dashboard can trigger remote push/pull/sync/scan.

---

## Screenshots

### Agent

| Overview | Add Games |
|---|---|
| ![Agent overview](docs/screenshots/overview.png) | ![Agent add games](docs/screenshots/addGames.png) |

### System tray

![Tray context menu](docs/screenshots/tray.png)

### Installer

![Installer wizard](docs/screenshots/installer.png)

---

## Getting started

### 1 — Run the server

The server ships as a Docker image built and pushed automatically on every commit to `main`.

**Docker Compose (recommended):**

```yaml
services:
  savelocker:
    image: ghcr.io/skorcherx/savelocker:latest
    container_name: savelocker-server
    ports:
      - "5080:8080"
    volumes:
      - /mnt/user/appdata/savelocker:/data
    restart: unless-stopped
```

```sh
docker compose up -d
# Dashboard at http://<server-ip>:5080
```

**Environment variables:**

| Variable | Default | Description |
|---|---|---|
| `Storage__DbPath` | `/data/savelocker.db` | SQLite database path |
| `Storage__ArchiveRoot` | `/data/archives` | Save archive directory |
| `Storage__RetainVersionsPerGame` | `10` | Default versions kept per game |
| `SteamGridDB__ApiKey` | *(unset)* | Cover art — set here or in the dashboard |

### 2 — Install the agent

Download `SaveLocker-Agent-Setup-x.x.x.exe` from [Releases](https://github.com/SkorcherX/SaveLocker/releases), run it, and follow the wizard. No .NET runtime required — the installer is self-contained (~43 MB).

On first launch the agent offers to open Settings so you can enter your server URL and register the machine.

### 3 — Add games

In the agent window → **Add Games**: the agent scans for Steam titles and games matching the Ludusavi manifest. Check the ones you want, set save folders for any that couldn't be auto-detected, and click **Enroll**. You can also add games from the dashboard and they'll appear on each agent at the next poll.

---

## Building from source

**Requirements:** .NET 9 SDK, Node 20+, npm

```sh
git clone https://github.com/SkorcherX/SaveLocker.git
cd SaveLocker
```

### Server

```sh
cd src/Server
dotnet run
# API + dashboard at http://localhost:5179
```

The React dashboard is built separately for development:

```sh
cd web
npm install
npm run dev   # proxies /api to :5179 — open http://localhost:5173
```

### Agent

```sh
# Build (stop the running agent first — it locks the DLLs)
dotnet build src/Agent/SaveLocker.Agent.csproj --no-incremental

# Run (tray mode)
src/Agent/bin/Debug/net9.0-windows/SaveLocker.Agent.exe

# Run (CLI)
src/Agent/bin/Debug/net9.0-windows/SaveLocker.Agent.exe status
```

The agent UI is a Vite/React app in `agent-ui/` — `npm run dev` there (port 5177) proxies `/api` to the agent's local server on port 5178. MSBuild runs `npm run build` automatically and copies `dist/` into the output folder on every build.

### Installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
.\installer\build-installer.ps1
# Output: installer/dist/SaveLocker-Agent-Setup-0.1.0.exe
```

---

## CI / CD

Every push to `main` triggers a GitHub Actions workflow that:

1. Builds the React dashboard (`npm run build` in `web/`)
2. Publishes the ASP.NET server (`dotnet publish`)
3. Builds and pushes a Docker image to `ghcr.io/skorcherx/savelocker:latest`

[Watchtower](https://containrrr.dev/watchtower/) on the unRAID host polls GHCR every 5 minutes and auto-restarts the container on a new image — so a `git push` is a full deploy with no manual steps.

---

## How conflict resolution works

1. Agent on **Machine A** launches a game → acquires lease, pulls latest save
2. Agent on **Machine B** launches the same game → lease denied, agent UI pops up with a warning banner; B plays anyway (B's push on exit will land as a conflict)
3. Both machines push diverged saves → server records a **conflict**, leaves the prior head intact
4. Dashboard shows the conflict — pick which save wins, or roll back to any archived version
5. Next sync on each machine picks up the resolved head

---

## Project structure

```
SaveLocker/
├── src/
│   ├── Agent/          # Windows tray app + CLI (.NET 9, WinForms, WebView2)
│   │   └── agent-ui/   # React agent UI (Vite, TypeScript)  ← built into Agent output
│   ├── Server/         # ASP.NET Core server + EF Core / SQLite
│   └── Shared/         # Wire contracts, archive helpers, manifest loader
├── web/                # React admin dashboard (Vite, TypeScript, Tailwind CSS v4)
├── installer/          # Inno Setup script + wizard artwork
└── LocalGameSync/      # Obsidian vault — living project notes and decisions
```

---

## Roadmap

- [ ] Technical rename — solution, namespaces, exe → SaveLocker
- [ ] Code-signing — remove SmartScreen warning for new users
- [ ] macOS / Linux agent (server already runs cross-platform)
