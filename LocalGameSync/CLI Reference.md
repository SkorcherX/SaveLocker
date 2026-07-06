# CLI Reference

Back to [[Home]]. The agent (`LocalGameSync.Agent`) runs as a tray app with **no
args**; **with args** it runs a one-shot command and exits. Run via
`dotnet <…>\LocalGameSync.Agent.dll <command> [options]` so console output
attaches (the `.exe` is a GUI-subsystem app and won't print to a terminal).

> The CLI is the power-user / automation surface. The [[UX Roadmap]] moves
> everyday use into the tray menu + dashboard.

## Global option
- `--config <path>` — use a specific config file instead of
  `C:\ProgramData\LocalGameSync\config.json`. Lets multiple identities run side
  by side (used heavily in tests).

## Commands

| Command | Options | What it does |
|---|---|---|
| *(none)* | | Launch the system-tray app. |
| `register` | `--name <machineName>` | Register this machine with the server. **Prints the API key** and saves it to config. Re-registering the same name **rotates** the key. |
| `whoami` | | Print the stored machine name, id, server URL, **API key**, and config path. Local-only (no server call). |
| `set-server` | `--url <url>` or positional `<url>` | Set the server URL in config (e.g. the CloudFlare Tunnel hostname). |
| `add-game` | `--name <name>` `[--manifest <key>]` `[--dir <path>]` `[--proc <a,b>]` | Enroll a game: creates the server `Game` (matched case-insensitively) and a local tracked entry. If `--dir` is omitted, the save dir is auto-detected from the Ludusavi manifest (`--manifest` or `--name`). `--proc` = process names (no `.exe`) that mean the game is running. |
| `list` | | List locally tracked games (name, save dir, process names). |
| `status` | | Per-game server state: head version + origin machine, lease holder, conflict flag. |
| `push` | `[gameName\|all]` `[--force]` | Archive + upload saves. Skipped if unchanged. Diverged uploads become a **conflict** (head untouched). `--force` overwrites the server head. |
| `pull` | `[gameName\|all]` `[--force]` | Download + restore the head. **Guarded:** refuses to overwrite local saves with un-pushed changes (prints a BLOCKED message). `--force` discards local and takes the server copy. |
| `scan` | `[--no-cloud]` | Discover enrollment candidates on this machine: non-Steam Steam shortcuts (`shortcuts.vdf`), installed Steam games (`libraryfolders.vdf` + `*.acf`), and folders under common save roots that match the Ludusavi manifest. Prints name, source, suggested save dir, and a `[Steam Cloud]` flag. `--no-cloud` hides games flagged as having Steam Cloud. Local-only. |
| `search` | positional `<term>` | List Ludusavi manifest game names containing the term. |
| `resolve` | `--manifest <name>` or positional `<name>` | Show the save dir(s) the manifest resolves to on this machine. Local-only. |
| `refresh-manifest` | | Re-download the Ludusavi manifest into the local cache. |
| `log` | `[--n <count>]` | Print the last *n* lines of the agent log file (default 50). Exits with a message if no log exists yet. |

## Examples
```sh
dotnet LocalGameSync.Agent.dll register --name "ThunderHorse"
dotnet LocalGameSync.Agent.dll whoami
dotnet LocalGameSync.Agent.dll set-server --url https://lgs.example.com
dotnet LocalGameSync.Agent.dll add-game --name "Octopath Traveler 0" --dir "C:\Users\me\AppData\Local\Octopath_Traveler0\Saved"
dotnet LocalGameSync.Agent.dll add-game --name "Celeste" --manifest "Celeste" --proc Celeste
dotnet LocalGameSync.Agent.dll scan --no-cloud     # list non-Steam-Cloud candidates to enroll
dotnet LocalGameSync.Agent.dll status
dotnet LocalGameSync.Agent.dll push all
dotnet LocalGameSync.Agent.dll pull all          # safe: won't clobber unsynced local saves
dotnet LocalGameSync.Agent.dll pull Celeste --force
```

## Safe initial-sync procedure
See [[Gotchas]] and [[UX Roadmap]]. On the machine holding real progress: `push`
first (it conflicts), then **resolve in the dashboard choosing that machine's
version**, then `pull` on the other machine.
