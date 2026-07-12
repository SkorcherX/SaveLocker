# CLI Reference

The agent (`SaveLocker.Agent`) runs as a tray app with **no args**; **with args** it runs a one-shot command and exits. Run via `dotnet <…>\SaveLocker.Agent.dll <command> [options]` so console output attaches (the `.exe` is a GUI-subsystem app and won't print to a terminal).

The CLI is the power-user / automation surface. Everyday use goes through the tray UI and dashboard.

## Global option
- `--config <path>` — use a specific config file instead of `%PROGRAMDATA%\SaveLocker\config.json`. Lets multiple identities run side by side (used heavily in tests).

## Commands

| Command | Options | What it does |
|---|---|---|
| *(none)* | | Launch the system-tray app. |
| `register` | `--name <machineName>` `--admin-password <pw>` | Register this machine with the server. Prints the API key and saves it to config. Re-registering the same name **rotates** the key; once the server has an admin password set, re-registration requires `--admin-password`. |
| `whoami` | | Print the stored machine name, id, server URL, API key, and config path. Local-only. |
| `set-server` | `--url <url>` | Set the server URL in config (e.g. the CloudFlare Tunnel hostname). |
| `add-game` | `--name <name>` `[--manifest <key>]` `[--dir <path>]` `[--proc <a,b>]` | Enroll a game: creates the server `Game` (matched case-insensitively) and a local tracked entry. If `--dir` is omitted, auto-detected from the Ludusavi manifest. `--proc` = process names (no `.exe`) that mean the game is running. |
| `list` | | List locally tracked games (name, save dir, process names). |
| `status` | | Per-game server state: head version + origin machine, lease holder, conflict flag. |
| `push` | `[gameName\|all]` `[--force]` | Archive + upload saves. Skipped if unchanged. Diverged uploads become a **conflict** (head untouched). `--force` overwrites the server head. |
| `pull` | `[gameName\|all]` `[--force]` | Download + restore the head. **Guarded:** refuses to overwrite local saves with un-pushed changes. `--force` discards local and takes the server copy. |
| `scan` | `[--no-cloud]` | Discover enrollment candidates: non-Steam Steam shortcuts, installed Steam games, and folders under common save roots matching the Ludusavi manifest. `--no-cloud` hides Steam Cloud games. Local-only. |
| `search` | `<term>` | List Ludusavi manifest game names containing the term. |
| `resolve` | `<name>` | Show the save dir(s) the manifest resolves to on this machine. Local-only. |
| `refresh-manifest` | | Re-download the Ludusavi manifest into the local cache. |
| `log` | `[--n <count>]` | Print the last *n* lines of the agent log (default 50). |

## Examples
```sh
dotnet SaveLocker.Agent.dll register --name "ThunderHorse"
dotnet SaveLocker.Agent.dll whoami
dotnet SaveLocker.Agent.dll set-server --url https://sl.example.com
dotnet SaveLocker.Agent.dll add-game --name "Octopath Traveler 0" --dir "C:\Users\me\AppData\Local\Octopath_Traveler0\Saved"
dotnet SaveLocker.Agent.dll add-game --name "Celeste" --manifest "Celeste" --proc Celeste
dotnet SaveLocker.Agent.dll scan --no-cloud
dotnet SaveLocker.Agent.dll status
dotnet SaveLocker.Agent.dll push all
dotnet SaveLocker.Agent.dll pull all
dotnet SaveLocker.Agent.dll pull Celeste --force
dotnet SaveLocker.Agent.dll log --n 100
```

## Safe initial-sync procedure
On the machine holding real progress: `push` first (it conflicts), then **resolve in the dashboard choosing that machine's version**, then `pull` on the other machine.
