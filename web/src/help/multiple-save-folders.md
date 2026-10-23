# Games with more than one save folder

Some games keep saves in two places — say `Documents\My Games\…` and `AppData\Local\…` — and an emulator keeps its save states beside its saves. A SaveLocker game can sync any number of folders.

The first folder is the game's **main** folder: the one you pick when you add the game. Every other folder has a short **key** (`states`, `config`, `appdata`…) that every machine knows it by. The key is also where that folder's files are stored inside each save, so it can never be renamed or reused, even after the folder is removed.

## Folders SaveLocker finds for you

When the save database lists more than one location for a game and they exist on your machine, the agent offers the others as **Also found**. The database can't tell a second save folder from a settings folder or another store's copy of the game, so SaveLocker always asks before adding one:

- **Add Games** lists them under the game, **ticked**. Untick any you don't want before you press **Enroll selected**.
- For a game you already track, the agent's **Overview** shows a **More save folders found** card. For each folder, press **Sync it**, **Don't sync** (it isn't offered again), or **Not now** (it's offered again later).
- `savelocker status` lists them as *also found*, each with the `add-path` command that adds it.

Folders an emulator reader *knows* belong to the game, such as RetroArch's save states, are added without asking. See [Setting up emulator saves](#help/emulator-saves).

## Adding a folder yourself

- **In the console:** open the game, expand **Extra save folders**, and fill in a **key**, a **template** that every machine expands for itself (for example `<winLocalAppData>/Game/States`), and optionally **only** the files that belong to the game (`*.sav`). Then press **Add folder**.
- **On one machine, from the command line:**

  ```sh
  savelocker add-path "Hollow Knight" --key config --dir "<folder on this machine>"
  ```

  Add `--include "*.ini"` to sync only some of its files. See the [CLI reference](#help/cli-reference).

A new folder is defined for **every** machine. Each machine syncs it from its own copy of the folder.

## A machine without that folder

A machine where the folder doesn't exist, or where its template doesn't resolve, still syncs the game. Its agent keeps a hidden copy of the folder in its own state, so the folder is never dropped from a save. When you later map the folder on that machine (`add-path` with `--dir`) and it already holds different files, SaveLocker asks which copy every machine should keep: answer with `--keep local` or `--keep cloud`.

In the console's **Extra save folders** section, a folder no machine has mapped yet says *Not mapped on any machine yet; each syncs it through a hidden copy until it is.*

## Removing a folder

Press **Remove** beside the folder in the console's **Extra save folders** section, or run `savelocker remove-path "<game>" --key <key>`. This stops syncing that folder on **every** machine. Its files stay where they are, versions already stored keep them, and the key is retired so it's never reused. The main folder can't be removed.

## Downloads and older agents

A version you **download** from the console has the extra folders inside a `.savelocker/paths/<key>/` folder of the zip. That's where SaveLocker stores them; it isn't a folder of the game.

An agent older than **1.0.0** still syncs such a game without losing anything, but it keeps the extra folders as that `.savelocker` folder *inside* the main one until it is updated. Update every machine that plays a game with more than one folder.

## Steam Deck Game Mode

Every folder of a game syncs in Game Mode as well, but adding or answering folders isn't available there yet. Use the agent UI in Desktop Mode, the console, or the command line.
