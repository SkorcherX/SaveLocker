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
2. Go to **Add Games** or **Overview** → find the game.
3. Click **Set save path** and browse to the local save folder.

Alternatively, use the **game scanning** feature: the agent can auto-detect save paths from Steam and Ludusavi's game manifests. Check the **Add Games** tab in the agent for detected candidates.

## Ludusavi auto-detection

SaveLocker downloads the [Ludusavi community manifest](https://github.com/mtkennerly/ludusavi) to look up known save paths for thousands of games. If your game is in the manifest, the agent will suggest its save path automatically. You can accept or override the suggestion.

## Tips

- Point the save path at the **save folder**, not the game's install directory. Pointing at the install directory will exceed the 200 MB upload cap and archive unnecessary files.
- If the game writes saves to multiple folders, map the primary one and exclude irrelevant subdirectories with [glob patterns](#help/glob-patterns).
- Disabling a game in the dashboard (Configuration → game toggle) pauses sync without deleting history.
