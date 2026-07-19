# Adding games & mapping save folders

## How games are tracked

SaveLocker separates **game definition** (server-side) from **save path mapping** (per-machine, agent-side):

- The **server** knows about a game by name and ID.
- Each **agent** maps that game ID to a local save folder on its machine.

This lets machines with different install paths (e.g. different drive letters) all sync the same game correctly.

## Adding a game from the dashboard

1. Click **+ Add game** in the nav bar.
2. Enter the game name. This name is displayed in the dashboard and agent UI.
3. Optionally enter a suggested save folder path. This is a hint — agents can override it.
4. Click OK. The game is now tracked on the server.

## Mapping a save folder on an agent

After a game is added to the server, each machine needs to map its local save directory:

1. Open the agent window (tray icon → open).
2. Go to **Settings** → **Currently Tracked Games** and find the game. One with no folder yet is marked **No save folder set**.
3. Click **Set save path** and browse to the local save folder.

Alternatively, use the **game scanning** feature: the agent can auto-detect save paths from Steam and Ludusavi's game manifests. Check the **Add Games** tab in the agent for detected candidates.

### On Linux / Steam Deck

"Headless" means the Deck agent has no tray icon and no pop-ups — **not** that it has no UI. The daemon serves the same web UI the Windows tray shows on port **5178**. In Desktop Mode, browse to `http://localhost:5178` and use **Settings → Currently Tracked Games**, exactly as above. A game with no folder yet shows **No save folder set** and a **Set save path** button.

Because there is no folder dialog on a Deck, that button opens a built-in folder browser instead. It is navigable with the D-pad or the trackpad — arrows or D-pad move, **Enter** or right enters a folder, **left**/**Backspace** goes up — and it opens on the path the scan already guessed, so usually you only confirm. It browses your home directory, your Steam libraries, and mounted SD cards; anything outside those is deliberately out of reach.

You never have to type a path. If you would rather not open the UI at all, there are two other routes:

- **From this console.** When an agent finds a likely folder for a game it can't map, the game's **Save paths per machine** table shows `<machine>'s scan found: <path>` with an **Apply** button. One click, no typing on the Deck.
- **From the command line**, if you have a terminal or SSH:

  ```sh
  savelocker scan          # offers to map anything it recognises — answer y
  savelocker add-game --name "Hollow Knight" --dir <save folder> --appid 367520
  ```

  `savelocker scan` now offers to map any tracked game it finds a folder for, which avoids typing the path by hand. Reach for `add-game --dir` when the scan doesn't recognise the game — common for standalone (non-Steam) builds, which usually aren't in the Ludusavi manifest. `--appid` is the Steam AppID of the non-Steam shortcut, which lets the `savelocker run` launch wrapper match the game to the Proton prefix Steam hands it.

Run `savelocker doctor` afterwards to confirm the path resolved. See the [CLI reference](#help/cli-reference) and [Installing the agent](#help/installing-the-agent) for reaching the UI over SSH.

## Ludusavi auto-detection

SaveLocker downloads the [Ludusavi community manifest](https://github.com/mtkennerly/ludusavi) to look up known save paths for thousands of games. If your game is in the manifest, the agent will suggest its save path automatically. You can accept or override the suggestion.

## Tips

- Point the save path at the **save folder**, not the game's install directory. Pointing at the install directory will exceed the 200 MB upload cap and archive unnecessary files.
- If the game writes saves to multiple folders, map the primary one and exclude irrelevant subdirectories with [glob patterns](#help/glob-patterns).
- Disabling a game in the dashboard (Configuration → game toggle) pauses sync without deleting history.
