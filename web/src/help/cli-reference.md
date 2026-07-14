# Agent CLI reference

## When to use the CLI

The agent runs as a **system-tray app** when launched with no arguments. Given arguments, it instead runs a single command, prints the result, and exits.

The CLI is the power-user and automation surface — scripting, diagnostics, and recovery. Everyday use goes through the tray UI and this dashboard; you never need the CLI for normal syncing.

## Running it

The installed `.exe` is a GUI-subsystem app, so **it does not print to a terminal**. Invoke the DLL through `dotnet` so output attaches to your console:

```sh
dotnet "C:\Program Files\SaveLocker Agent\SaveLocker.Agent.dll" <command> [options]
```

If you must run the `.exe`, redirect its output to a file:

```sh
"C:\Program Files\SaveLocker Agent\SaveLocker.Agent.exe" status > C:\temp\sl.txt 2>&1
```

## Global option

| Option | What it does |
|--------|-------------|
| `--config <path>` | Use a specific config file instead of `%PROGRAMDATA%\SaveLocker\config.json`. Lets several machine identities run side by side on one PC. |

## Commands

### Setup

| Command | Options | What it does |
|---------|---------|-------------|
| *(none)* | | Launch the system-tray app. |
| `enroll` | `--file <policy.json>`<br>`[--name <machineName>]` | **The easy way to set up a machine.** Create the file in the console (**Configuration → Enroll a machine**), copy it over, and run this. It sets the server URL, trades the file's single-use token for this machine's API key, pins the server, and picks up the games already defined on the server — no API key ever gets copied by hand. The file expires (15 min by default) and works **once**. If it was created for a specific machine name, that name wins over `--name`. |
| `register` | `--name <machineName>`<br>`--admin-password <pw>` | Register this machine by hand instead. Prints the API key and saves it to config. Re-registering an existing name **rotates** its key; once an admin password is set on the server, re-registration requires `--admin-password`. |
| `set-server` | `--url <url>` | Point the agent at a server (e.g. your Cloudflare Tunnel hostname). |
| `trust` | `[--accept]` | Show the server's pinned TLS key — recorded at `enroll`, and checked on every later connection. If the server's identity ever changes, the agent **warns** but keeps working. That is expected after a certificate renewal: confirm it was you, then `trust --accept` to pin the new key. If it wasn't you, stop — a `pull` writes files into your save folders. Servers reached over plain `http://` have no identity to pin. |
| `whoami` | | Print the stored machine name, ID, server URL, API key, and config path. Local only — does not contact the server. |

### Games

| Command | Options | What it does |
|---------|---------|-------------|
| `add-game` | `--name <name>`<br>`[--manifest <key>]`<br>`[--dir <path>]`<br>`[--proc <a,b>]` | Enroll a game. Creates the server-side game (matched case-insensitively, so it joins an existing one rather than duplicating it) and a local tracked entry. Without `--dir`, the save folder is auto-detected from the Ludusavi manifest. `--proc` lists process names (no `.exe`) that mean the game is running. |
| `list` | | List locally tracked games: name, save directory, process names. |
| `scan` | `[--no-cloud]` | Discover enrollment candidates — non-Steam shortcuts, installed Steam games, and folders under common save roots that match the manifest. `--no-cloud` hides games that already have Steam Cloud. Local only. |
| `search` | `<term>` | List Ludusavi manifest game names containing the term. |
| `resolve` | `<name>` | Show the save folder the manifest resolves to **on this machine**. Reports nothing if the folder doesn't exist yet. Local only. |
| `refresh-manifest` | | Re-download the Ludusavi manifest into the local cache. |

### Syncing

| Command | Options | What it does |
|---------|---------|-------------|
| `status` | | Per-game server state: head version and the machine it came from, lease holder, conflict flag. |
| `push` | `[gameName\|all]`<br>`[--force]` | Archive and upload saves. Skipped when nothing changed. A diverged upload becomes a **conflict** and leaves the head untouched. `--force` overwrites the server head with this machine's copy. |
| `pull` | `[gameName\|all]`<br>`[--force]` | Download and restore the head. **Guarded:** refuses to overwrite local saves that hold un-pushed changes. `--force` discards the local copy and takes the server's. |
| `hash` | `[gameName\|all]`<br>`--dir <path>`<br>`[--exclude <a,b>]` | Print the content hash of a save folder — the value the server compares to decide *changed*, *unchanged*, or *conflict*. Use it to check whether two machines really hold the same save. Identical bytes give an identical hash on **any** OS, so a Windows PC and a Steam Deck agree. Local only. |
| `log` | `[--n <count>]` | Print the last *n* lines of the agent log (default 50). |

Manual `push` and `pull` are **immediate** — they skip the settle gate that delays automatic backups. See **Save-in-use safety**.

## Examples

```sh
dotnet SaveLocker.Agent.dll enroll --file savelocker-enroll-steamdeck.json
dotnet SaveLocker.Agent.dll trust

dotnet SaveLocker.Agent.dll register --name "ThunderHorse"
dotnet SaveLocker.Agent.dll set-server --url https://sl.example.com
dotnet SaveLocker.Agent.dll whoami

dotnet SaveLocker.Agent.dll scan --no-cloud
dotnet SaveLocker.Agent.dll add-game --name "Celeste" --manifest "Celeste" --proc Celeste
dotnet SaveLocker.Agent.dll add-game --name "Octopath Traveler 0" --dir "C:\Users\me\AppData\Local\Octopath_Traveler0\Saved"

dotnet SaveLocker.Agent.dll status
dotnet SaveLocker.Agent.dll push all
dotnet SaveLocker.Agent.dll pull Celeste --force
dotnet SaveLocker.Agent.dll log --n 100
```

## Safe first sync between two machines

When both machines already have save data, one of them holds the progress you actually want to keep. Don't guess — make the server decide:

1. On the machine with the **real progress**, run `push`. It will report a conflict (both machines have history the server hasn't seen).
2. In the dashboard, **resolve the conflict**, choosing that machine's version.
3. On the other machine, run `pull`.

Pulling first on the wrong machine is what loses progress. The pull guard exists to catch exactly that, which is why an un-forced `pull` refuses to overwrite un-pushed local saves.
