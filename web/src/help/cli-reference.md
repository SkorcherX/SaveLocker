# Agent CLI reference

## When to use the CLI

The agent runs as a **system-tray app** when launched with no arguments. Given arguments, it instead runs a single command, prints the result, and exits.

The CLI is the power-user and automation surface — scripting, diagnostics, and recovery. Everyday use goes through the tray UI and this dashboard; you never need the CLI for normal syncing.

## Running it

**Linux / Steam Deck** — the agent is a normal command-line program. `install.sh` symlinks it onto your `PATH`, so just run:

```sh
savelocker <command> [options]
```

On a Steam Deck, the daemon runs headless (there is no tray); the CLI is how you enroll it, add games, and diagnose it. `savelocker doctor` is the one to remember — see the **Linux / Steam Deck** section below.

**Windows** — the installed `.exe` is a GUI-subsystem app, so **it does not print to a terminal**. Invoke the DLL through `dotnet` so output attaches to your console:

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
| `--config <path>` | Use a specific config file instead of the default (`%PROGRAMDATA%\SaveLocker\config.json` on Windows, `~/.local/share/SaveLocker/config.json` on Linux). Lets several machine identities run side by side on one machine. On Linux the `SAVELOCKER_CONFIG` environment variable does the same thing — handy inside a systemd unit. |

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
| `add-game` | `--name <name>`<br>`[--manifest <key>]`<br>`[--dir <path>]`<br>`[--proc <a,b>]`<br>`[--appid <id>]`<br>`[--prefix <compatdata>]` | Enroll a game. Creates the server-side game (matched case-insensitively, so it joins an existing one rather than duplicating it) and a local tracked entry. Without `--dir`, the save folder is auto-detected from the Ludusavi manifest. `--proc` lists process names (no `.exe`) that mean the game is running. **Linux:** `--appid` is the Steam AppID whose Proton prefix (`compatdata/<appid>`) the launch wrapper matches on — set it for a non-Steam shortcut; `--prefix` resolves the manifest's paths inside a specific Wine prefix. On Linux, mapping with `--dir` is the normal path, not a fallback (most standalone builds aren't in the manifest). |
| `remove-game` | `--name <name>` | Stop syncing a game **on this machine**. The game and its saves stay on the server for every other machine. Recorded as a per-machine opt-out, so a running daemon will not re-adopt it; `add-game` (or re-adding it in the agent UI) undoes it. |
| `add-path` | `<gameName>`<br>`--key <key>`<br>`--dir <path>`<br>`[--include <a,b>]`<br>`[--label <text>]`<br>`[--keep local\|cloud]`<br>`[--force-path]` | Give a game **another save folder** — saves in both Documents and AppData, or an emulator's save states beside its saves. The key (`states`, `appdata`…: lower-case letters, digits and `-`) is the folder's name on **every** machine and can never be renamed or reused. The folder is described to the other machines as a template (`<winAppData>/Game/States`) when one fits; each machine whose copy of that folder exists maps it on its next poll. A machine that has no such folder keeps a hidden copy of it (a *shadow*, in the agent's own state) so it is never dropped from the save. `--include` limits the folder to matching files (relative to it, e.g. `*.state`). Run it again with an existing key to map that folder on **this** machine. If the folder already holds different files from the synced copy, it asks: `--keep local` keeps this folder's files (the next push sends them), `--keep cloud` replaces them with the synced copy. It also asks when this machine has not received that folder from the server yet (no pull since another machine added it): `pull` the game first, or `--keep local`. Two folders of one game may not be nested, and may share a directory only when both have include patterns. |
| `remove-path` | `<gameName>`<br>`--key <key>` | Stop syncing one of a game's extra save folders, **on every machine**. The files stay where they are; versions already stored keep them; the key is retired. The game's primary folder cannot be removed. |
| `list` | | List locally tracked games: name, save directory, process names, and any extra save folders. |
| `scan` | `[--no-cloud]`<br>`[--yes]`<br>`[--no-prompt]` | Discover enrollment candidates — non-Steam shortcuts, installed Steam games, and folders under common save roots that match the manifest. `--no-cloud` hides games that already have Steam Cloud. **If a candidate matches a tracked game that has no save folder, `scan` offers to map it** — answer `y` and you never have to type the path. `--yes` applies every match without asking; `--no-prompt` only lists. Prompting is skipped automatically when the output is piped or the command runs under systemd. Local only. |
| `search` | `<term>` | List Ludusavi manifest game names containing the term. |
| `resolve` | `<name>` | Show the save folder the manifest resolves to **on this machine**. Reports nothing if the folder doesn't exist yet. Local only. |
| `refresh-manifest` | | Re-download the Ludusavi manifest into the local cache. |

### Syncing

| Command | Options | What it does |
|---------|---------|-------------|
| `status` | | Per-game server state: head version and the machine it came from, lease holder, conflict flag. A game with extra save folders also lists each one: its folder here, or *not mapped here* with the size of its shadow copy. |
| `push` | `[gameName\|all]`<br>`[--force]` | Archive and upload saves. Skipped when nothing changed. A diverged upload becomes a **conflict** and leaves the head untouched. `--force` overwrites the server head with this machine's copy. |
| `pull` | `[gameName\|all]`<br>`[--force]` | Download and restore the head. **Guarded:** refuses to overwrite local saves that hold un-pushed changes. `--force` discards the local copy and takes the server's. |
| `emptied-hold` | `[gameName]`<br>`[--delete-everywhere\|--restore-here]` | A game whose save folder lost **every** file at once (an uninstaller, a reset, the wrong folder cleared) is held: nothing is pushed or pulled for it on this machine, so the empty folder never reaches your other machines. With no game, lists the held games. `--restore-here` pulls the latest save back; `--delete-everywhere` pushes the empty folder so every machine deletes its copy. Either lifts the hold, and so does a file coming back. |
| `hash` | `[gameName\|all]`<br>`--dir <path>`<br>`[--exclude <a,b>]` | Print the content hash of a save folder — the value the server compares to decide *changed*, *unchanged*, or *conflict*. Use it to check whether two machines really hold the same save. Identical bytes give an identical hash on **any** OS, so a Windows PC and a Steam Deck agree. Local only. |
| `log` | `[--n <count>]` | Print the last *n* lines of the agent log (default 50). |
| `check-update` | `[--download]` | Ask the configured server what agent version it is publishing **for this platform**, and show the URL and SHA-256. `--download` fetches the package and **verifies** it, printing `VERIFIED` and the path — it never installs it. A download is refused if the checksum does not match, if the payload is not the right shape for this platform (a Windows executable, or a gzip archive on Linux), or if it comes from a host other than your server without a published checksum. Since SaveLocker runs over plain `http://` by default, that checksum is the only thing standing between the server's answer and code running on your machine. |
| `version` | | Print this agent's version and nothing else. Handy in a script — and it is what the Linux updater asks a downloaded package before letting it replace a working agent. |

Manual `push` and `pull` are **immediate** — they skip the settle gate that delays automatic backups. See **Save-in-use safety**.

### Linux / Steam Deck only

These commands exist only in the Linux agent, which has no tray or window to do their job.

| Command | Options | What it does |
|---------|---------|-------------|
| `run` | `-- %command%` | The **Steam launch wrapper**. Add `/home/deck/.local/bin/savelocker run -- %command%` to a game's **Launch Options** and Steam runs the game *through* the agent: it pulls the latest save before launch, waits for the game to exit, waits for the save to settle, and pushes. Everything after `--` is the game's own command line, untouched. This is how a Deck syncs — it has no process watcher. **Use the full path** — Game Mode does not put `~/.local/bin` on `PATH`, so the short form silently prevents the game from launching. |
| `launch-options` | `[--list]`<br>`[--check]`<br>`[--preview "<existing>"]` | Print the exact string to paste into a game's **Launch Options** — the answer to "what do I paste?" without opening the agent UI. `--list` adds every tracked game Steam launches and what is known about its options; `--check` does the same and exits non-zero if any is unconfirmed, for a script. `--preview` shows what a game that **already** has launch options would end up with: the wrapper is substituted into `%command%`, so `mangohud`, environment variables and per-game arguments all keep working, and a short `savelocker` is repaired to the full path. The agent cannot set launch options itself — Steam rewrites its own config on exit, so only something running inside Steam can — which is why "unknown" is the normal state here rather than a fault. |
| `doctor` | | **The first thing to run when something is wrong.** Checks the whole chain — server reachable, Steam roots found, shortcuts parsed, Proton prefixes located, save folders present and writable — and prints a mark next to anything broken. On a headless machine it is the only diagnostic UI; paste its output when asking for help. |
| `daemon` | `[--port <n>]` | Run the agent headless (the foreground process the systemd unit runs). It serves the same agent UI on **localhost:5178** — loopback only, always, because that UI can re-point this machine at another server. To see it from another device, forward the port over SSH: `ssh -L 5178:localhost:5178 deck@<deck-ip>`. `--port` moves the listener (for running a second agent alongside a real one); it does not change what it binds to. |
| `autostart` | `--enable`<br>`--disable` | Enable or disable the `systemd --user` unit that starts the daemon with your session. With no flag, prints whether it is currently enabled. (`install.sh` enables it for you.) |
| `update` | | Fetch, verify and install a newer agent **now**, then restart the service. You do not normally need it: the daemon prepares an update by itself and installs it the next time the agent starts, so this is for when you do not want to wait. It refuses a package whose checksum is wrong, that is not a gzip archive, or that will not run and report the version it claimed. |
| `plugin-update` | `[--check]` | Update the **Decky plugin** from the server now. The daemon does this on its own timer, so you rarely need it. `--check` reports and installs nothing, exiting non-zero when the installed plugin is behind. Does nothing on a machine without Decky Loader, and cannot perform the *first* install — creating the plugin directory needs root, so that stays a one-time **Install Plugin from URL** paste in Decky. |

> The daemon never swaps its own files while running — `systemctl --user stop` kills everything in the service's control group, so an updater it launched would be killed halfway through. Instead it downloads, verifies and unpacks, and the unit installs that on its next start. The previous version is kept until the new one has started successfully; one that does not start is put back automatically and reported to the console.
>
> To stop the agent preparing updates on its own, set `"AutoUpdate": false` in `~/.local/share/SaveLocker/config.json`. It keeps checking, so you still hear that you are behind. The same setting governs the Decky plugin.

> **The Decky plugin updates the same way.** If it is installed, the agent replaces its files from the server on the same schedule; Decky reloads it within a second, with no sudo and no Steam restart. Decky holds exactly one custom-store URL — which replaces the official store while it is set — so this is how you get plugin updates without giving that up. Two things it will not do: it never writes `plugin.json` (Decky owns that file), and it refuses a package that needs a new *top-level* file rather than half-installing one, telling you to reinstall the plugin once from Decky instead.

> On a Deck, the agent stops when you log out unless *lingering* is enabled: `sudo loginctl enable-linger $USER`. Usually unnecessary — you are logged in whenever you are playing.

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
dotnet SaveLocker.Agent.dll add-path "Octopath Traveler 0" --key config --dir "C:\Users\me\Documents\My Games\Octopath"
dotnet SaveLocker.Agent.dll remove-path "Octopath Traveler 0" --key config

dotnet SaveLocker.Agent.dll status
dotnet SaveLocker.Agent.dll push all
dotnet SaveLocker.Agent.dll pull Celeste --force
dotnet SaveLocker.Agent.dll log --n 100
```

On Linux / Steam Deck the binary prints normally, so drop the `dotnet` wrapper:

```sh
savelocker enroll --file ~/Downloads/savelocker-enroll-steamdeck.json
savelocker doctor
savelocker add-game --name "Hollow Knight" --dir ~/.local/share/Steam/steamapps/compatdata/367520/pfx/... --appid 367520
savelocker daemon                # agent UI at http://localhost:5178 (loopback only)
# from another device:  ssh -L 5178:localhost:5178 deck@<deck-ip>

# In the game's Steam launch options (use the full path — Game Mode has no ~/.local/bin on PATH):
#   /home/deck/.local/bin/savelocker run -- %command%
```

## Safe first sync between two machines

When both machines already have save data, one of them holds the progress you actually want to keep. Don't guess — make the server decide:

1. On the machine with the **real progress**, run `push`. It will report a conflict (both machines have history the server hasn't seen).
2. In the dashboard, **resolve the conflict**, choosing that machine's version.
3. On the other machine, run `pull`.

Pulling first on the wrong machine is what loses progress. The pull guard exists to catch exactly that, which is why an un-forced `pull` refuses to overwrite un-pushed local saves.
