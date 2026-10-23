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

Each game in that list has a small icon before its name: a **magnifier with a tick** means its save folder was found, a **yellow warning** means you need to set one, and a **green tick** means it is already added on this machine. Games already added are hidden; turn off **Hide enrolled** (top right, beside the filters) to show them, faded green, with an **Enrolled** filter of their own. The Steam Deck's Game Mode app works the same way.

## Add the game to Steam first (this is what makes detection work)

**If a game is not in Steam, the agent cannot find it.** This is the single most common reason a game
you have clearly installed never appears in **Add Games**.

The agent discovers games from four places:

1. **Non-Steam shortcuts** — anything you added to Steam yourself, read from `shortcuts.vdf`.
2. **Installed Steam games** — titles Steam itself installed.
3. **Existing save folders** — a folder under `Documents\My Games`, `AppData` and similar whose name
   matches a known game.
4. **Heroic Games Launcher** (Linux and Steam Deck) — games installed through Heroic from the Epic,
   GOG or Amazon stores, or sideloaded into it. These are found from Heroic itself, so they do
   **not** need adding to Steam first.

Apart from Heroic, all of these depend on Steam or on save data that already exists.

A game bought elsewhere and installed to, say, `E:\Games\...` matches none of these until you either
add it to Steam or launch it once so it writes a save. So:

**Add it to Steam as a non-Steam game** (in Steam: *Games → Add a Non-Steam Game to My Library*),
then **Rescan** in the agent. This is worth doing anyway if you use Big Picture or a Steam Deck,
since it is also how you launch the game there.

Two things that help, but are not required:

- **Launch the game once.** Detection confirms a folder actually exists, so a game that has never
  written a save has nothing to point at. It will still be listed — just with **No save folder set**,
  which you can fill in yourself.
- **Name the shortcut close to the real title.** Matching ignores case and punctuation, so
  `DRAGON QUEST III HD 2D Remake` still matches `Dragon Quest III HD-2D Remake`. It cannot recover
  from an abbreviation or nickname — a shortcut called `DQ3` will not match anything.

If a game still shows no folder after all that, it is most likely absent from the Ludusavi manifest
(common for very recent releases). Set the folder yourself with **Set save path**; nothing else about
syncing is affected.

### Removing a game does not uninstall it

Removing a game from SaveLocker only stops *this machine* syncing it. The game stays installed and
stays on the server for your other machines. Re-adding it is just **Rescan** → tick → enrol — but it
only reappears if it is still discoverable by one of the three routes above.

### On Linux / Steam Deck

**The easiest route on a Deck is the Game Mode app.** Add `~/.local/share/SaveLocker/savelocker` to
Steam as a non-Steam game with the launch option `ui`, launch **SaveLocker** from your library, and
add games with the D-pad — scan, tick a game, set its save folder from inside the game's own Proton
prefix, and enrol, all without leaving Game Mode. Setup steps are under
[Installing the agent](#help/installing-the-agent). The rest of this section covers the Desktop Mode
web UI, which is still there if you prefer it.

"Headless" means the Deck agent has no tray icon and no pop-ups — **not** that it has no UI. The daemon serves the same web UI the Windows tray shows on port **5178**. In Desktop Mode, browse to `http://localhost:5178` and use **Settings → Currently Tracked Games**, exactly as above. A game with no folder yet shows **No save folder set** and a **Set save path** button.

Because there is no folder dialog on a Deck, that button opens a built-in folder browser instead. In Desktop Mode you drive it the way you drive anything else there: **the right stick (or a trackpad) moves the mouse cursor and the left stick scrolls** — point at a folder and click to open it. Rows are deliberately large enough to hit comfortably. **The D-pad does not work here** — SteamOS maps it to neither the cursor nor the arrow keys. If you have a keyboard attached, arrow keys move the selection, **Enter** or **right** enters a folder and **left**/**Backspace** goes up.

The browser opens **inside the game's own Proton prefix** when the scan couldn't guess a save folder — so you land where the save actually lives instead of at your home directory. It browses your home directory, your Steam libraries, and mounted SD cards (`/run/media`); anything outside those is deliberately out of reach.

#### Games installed through Heroic Games Launcher

Heroic games are found without being added to Steam, and their save folders are detected inside the
Wine prefix Heroic runs each game in. Adding them to Steam as well is still worth doing if you want
to launch them from Game Mode — the agent lists such a game once, not twice.

Two things about Heroic that look like faults and are not. It often names a sideloaded game's prefix
`Prefixes/Title`, because its own "add a game" form starts with the word *Title* in the name field —
harmless, and nothing to do with SaveLocker. **Do not rename a prefix folder to tidy it up:** Heroic
keeps the old path in its settings, so it simply creates a new empty prefix there and runs the game
in that, leaving your existing saves behind in the folder you renamed. Change the prefix in Heroic's
per-game settings instead.

Games that Heroic installed as **native Linux builds** are not detected. Only Windows titles running
under Wine or Proton are supported.

Save folders inside a Wine prefix (`My Documents`, `Application Data` and the like under `steamuser`) are symlinks. The browser now shows and follows those, because they point back inside the prefix; a link that would escape the browsable roots is still hidden.

Once you enrol a game, the agent shows a **Steam launch setup** card with the exact launch-options command and a **Copy** button — see [the launch wrapper](#help/installing-the-agent) for what to do with it. The command is the same for every game, so you only need the card for the first one.

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

## Games with more than one save folder

A game can sync several folders — saves in both Documents and AppData, or an emulator's states beside its saves. When the save database knows another folder for a game, Add Games lists it under the game as **Also found**, ticked. See [Games with more than one save folder](#help/multiple-save-folders).

## Emulator games

Emulator saves are found by their save files, not by a launcher's library: pick the **Emulators** filter in Add Games. See [Setting up emulator saves](#help/emulator-saves).

## A Steam shortcut with more than one AppID

When a launcher re-creates a non-Steam shortcut instead of updating it, Steam ends up with the same game under two or more AppIDs, and only ever launches one of them. Add Games marks such a game with **N duplicate AppIDs** and says which AppID SaveLocker will track. If that is the stale one, its saves are never backed up: remove the stale shortcut in Steam (or set the save folder by hand), then rescan. Game Mode shows the same warning on the game's row.

## Tips

- Point the save path at the **save folder**, not the game's install directory. Pointing at the install directory will exceed the 200 MB upload cap and archive unnecessary files.
- If the game writes saves to multiple folders, add each one — see [Games with more than one save folder](#help/multiple-save-folders) — and exclude irrelevant subdirectories with [glob patterns](#help/glob-patterns).
- Disabling a game in the dashboard (Configuration → game toggle) pauses sync without deleting history.
