# LocalGameSync

Sync save-game data for games **without native cloud save** between a gaming PC
and a laptop, using an always-on **unRAID server** as the authoritative hub.

The system prevents the classic "played on both machines and overwrote my
progress" failure with a **lease/checkout** model (like Steam Cloud's "in use")
backed by content-hash conflict detection, and gives you an **admin web
dashboard** to see who synced last, resolve conflicts, and roll back.

## How it works

```
  ┌─────────────┐         ┌──────────────────────────┐         ┌─────────────┐
  │  Gaming PC  │         │   unRAID (Docker)         │         │   Laptop    │
  │  Agent      │◄──────► │  Server API + Dashboard   │ ◄─────► │  Agent      │
  │ (tray .exe) │  HTTPS  │  + SQLite + archive store │  HTTPS  │ (tray .exe) │
  └─────────────┘         └──────────────────────────┘         └─────────────┘
                                      ▲
                                      │ CloudFlare Tunnel (remote laptop)
```

- **Agent** (`src/Agent`) — Windows tray app on each machine. Detects save
  locations from the community [Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest),
  watches for changes, pulls before a game launches and pushes after it exits,
  and offers manual Force Push / Force Pull.
- **Server** (`src/Server`) — ASP.NET Core + EF Core/SQLite, runs in Docker on
  unRAID. Stores versioned save archives, tracks leases, detects conflicts, and
  serves the admin dashboard.
- **Shared** (`src/Shared`) — wire DTOs, the deterministic archive/hash helpers,
  and the manifest loader / Windows path-token resolver.

### Save detection
Standalone games store saves in unpredictable places (`%APPDATA%`, `LocalLow`,
`Documents\My Games`, `Saved Games`, the install dir, …). Rather than re-mapping
thousands of games, the agent reads the **Ludusavi manifest** and resolves its
path tokens to concrete folders on the machine. Games not in the manifest are
added manually with `--dir`.

### Conflict prevention
On `upload`, the agent sends the version it last knew (`parent`). If that still
matches the server head, the upload **fast-forwards**. If the content is
identical, it's a **no-op**. If the head moved on (the other machine pushed
first), the upload is recorded as a **conflict** and the head is left untouched
for you to resolve in the dashboard. Leases stop most conflicts before they
happen; hashing is the safety net.

## Build

Requires the **.NET 9 SDK**.

```sh
dotnet build LocalGameSync.sln
```

## Run the server locally

```sh
cd src/Server
dotnet run
# Dashboard at http://localhost:5179  (health: /health)
```

State (SQLite + archives) goes under `src/Server/localstate/` in Development.

## Deploy the server on unRAID

Build and run the container, mounting a share for persistent state:

```sh
docker build -t localgamesync -f src/Server/Dockerfile .
docker run -d --name localgamesync \
  -p 8080:8080 \
  -v /mnt/user/appdata/localgamesync:/data \
  localgamesync
```

Expose it over the internet with your **CloudFlare Tunnel** by pointing a public
hostname at `http://localhost:8080`. Put CloudFlare Access in front for auth.

| Setting | Env var | Default |
|---|---|---|
| SQLite path | `Storage__DbPath` | `/data/localgamesync.db` |
| Archive root | `Storage__ArchiveRoot` | `/data/archives` |
| Versions kept per game | `Storage__RetainVersionsPerGame` | `10` |

## Agent usage (CLI)

The agent runs as a tray app when launched with no arguments. The same exe also
exposes CLI commands (the manual-override surface, and how you do first-time
setup). `--config <path>` lets you point at a specific config file.

```sh
# one-time
LocalGameSync.Agent register --name "GamingPC"
LocalGameSync.Agent register --name "GamingPC" --config C:\path\config.json

# add a game (auto-detect via manifest, or pass --dir)
LocalGameSync.Agent add-game --name "Celeste" --manifest "Celeste"
LocalGameSync.Agent add-game --name "MyGame" --dir "C:\Saves\MyGame" --proc mygame

# manual sync
LocalGameSync.Agent pull all
LocalGameSync.Agent push all
LocalGameSync.Agent status
LocalGameSync.Agent resolve --manifest "Celeste"   # show detected save dir(s)
```

Config lives at `%PROGRAMDATA%\LocalGameSync\config.json`. Set `ServerUrl` to
your tunnel hostname so the laptop can sync from anywhere.

## Tests

- `dotnet build LocalGameSync.sln` — compile everything.
- `.verify/run-agent-tests.ps1` — end-to-end agent ↔ server suite (detection,
  two-machine push/pull, conflict path). Requires the server running on
  `http://localhost:5179`.
