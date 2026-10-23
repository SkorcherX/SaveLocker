# When every save file disappears

If **every** file in a game's save folder disappears at once, SaveLocker doesn't sync the empty folder. An uninstaller, a game's "reset", a cleaning tool or clearing the wrong folder can all do this, and syncing it would delete that game's saves on every other machine at their next pull.

Instead, the game is **held** on that machine:

- Nothing is pushed or pulled for it there, automatically or by **Sync all**, until you answer.
- The agent's **Overview** shows a **Save files are gone** banner for the game, naming the folder.
- The console's machine health shows a warning for that machine, naming the game.
- Sync all's result names the game under *Held (every save file is gone here)*.

Your other machines keep syncing the game as usual, and the server still has every version with the files.

## Answering a hold

On the banner, choose one:

- **Restore here** — pulls the latest save back into the empty folder. Nothing is overwritten, because the folder is empty. Use this when the files were lost by accident.
- **Delete everywhere** — you meant to clear the save. The empty folder is pushed as the new latest version, and every other machine removes its copy at its next pull. The last version that had files stays on the server, so **Set as Latest** in the console brings it back if you change your mind.

From the command line, `savelocker emptied-hold` lists held games, and `savelocker emptied-hold "<game>" --restore-here` or `--delete-everywhere` answers one. See the [CLI reference](#help/cli-reference).

If the files come back on their own (a reinstalled game restoring them, for example), the hold lifts by itself and the game syncs as usual.

## The server's guard

The server refuses an empty upload over a latest version that has files, unless the agent forces it. So an agent that predates the hold, or a script, can't empty a game for the whole fleet by accident either. **Delete everywhere** is that forced upload.

## A game that starts empty

The hold is only for a folder that *had* files and lost all of them. A game whose folder is empty from the start, such as one you've added but not played yet, syncs normally.
