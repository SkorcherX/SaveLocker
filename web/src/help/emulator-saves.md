# Setting up emulator saves

SaveLocker finds emulator saves by itself and lists one game per save in **Add games**, the same way it lists PC games. You don't type a path or pick a folder: each emulator game already knows which files are its own, even inside a folder that every game of that emulator shares.

For what to sync and how to keep saves safe between machines, see [Emulator backup best practices](#help/emulator-backups).

## Supported emulators

| Emulator | System | What syncs | Save states | Named by |
|---|---|---|---|---|
| RetroArch | Every core | `<rom>.srm` | `<rom>.state*` | The save file's name |
| melonDS | Nintendo DS | `<rom>.sav` | `.ml1`–`.ml8` | The save file's name |
| Supermodel | Sega Model 3 | `NVRAM/<set>.nv` | `Saves/<set>.st*` | The arcade set |
| Model 2 Emulator | Sega Model 2 | `NVDATA/<set>.DAT` | `STATES/<set><slot>.sta` | The arcade set |
| ScummVM | Adventure games | The target's save files | — | The target's description |
| PCSX2 | PS2 | A folder card's saves for one game | `<serial>*.p2s` | Disc serial (Redump), region beside the name |
| DuckStation | PS1 | One per-game memory card | `<serial>_*.sav` | Disc serial (Redump), region beside the name |
| Dolphin | GameCube, Wii | A game's `.gci` files; a Wii title's `data` | `<game ID>.s*` | Game code (Redump), region beside the name |
| PrimeHack | GameCube, Wii | As Dolphin | As Dolphin | As Dolphin |
| PPSSPP | PSP | Every `SAVEDATA` folder of the game ID | `PPSSPP_STATE/<ID>_*` | Game ID (Redump), else the save's `PARAM.SFO` |
| RPCS3 | PS3 | Every `savedata` folder of the game ID | — | Game ID (Redump), else the save's `PARAM.SFO` |
| Vita3K | PS Vita | The title ID's `savedata` folder | — | The save's `param.sfo` |
| shadPS4 | PS4 | The title ID's save folder | — | The save's `param.sfo` |
| Cemu | Wii U | `usr/save/00050000/<ID>` | — | Cemu's game list, else `Wii U <ID>` |
| Azahar (and Citra) | 3DS | The title's `data` folder | `states/<program ID>.*` | The ROM's product code (No-Intro), else `3DS <ID>` |
| Xenia | Xbox 360 | The title's saved games and their headers | — | Title ID (Redump and No-Intro), else `Xbox 360 <ID>` |

**Nintendo Switch emulators** (Eden, Citron, Yuzu, Ryujinx and the rest) are **not supported**. Their saves can still be added by hand like any folder, but SaveLocker doesn't find them or name them.

## Where SaveLocker looks

On a **Steam Deck or Linux machine with EmuDeck**, the agent reads EmuDeck's `Emulation/saves/<emulator>` folders, following EmuDeck's links to wherever each emulator really keeps its saves. On **Windows with EmuDeck for Windows**, it reads EmuDeck's own emulator folders. With no EmuDeck, it reads each emulator's own settings to find its save folder: RetroArch's `retroarch.cfg`, PCSX2's and Dolphin's config, RPCS3's `vfs.yml`, ScummVM's `scummvm.ini`, Flatpak installs and the default folders of standalone builds.

A game shows up once it has an in-game save. The one exception is a PSP game you have only played from save states so far: it's listed too, marked *Save states only so far*. Adding it syncs its states, and its in-game saves join the game once the game makes one.

## Adding an emulator game

1. Open **Add Games** in the agent (or **Add game** in Game Mode on a Steam Deck).
2. Pick the **Emulators** filter. In the agent, an **Emulator** row under it narrows the list to one emulator, and a **Console** row (shown once games from more than one console are found) narrows it to one console.
3. Tick the games and press **Enroll selected**.

Each emulator game's row shows which emulator found it and, when a console release has one, its region. When two ROMs clean up to the same title, the save file's own name is shown beside it so you can tell them apart.

On every other machine, add the same game the same way. **A machine joins the existing server game by the save's files, not by its name**, so it doesn't matter if this machine would call the game something else (for example because it has no `gamelist.xml`, ROM or game list to read the name from).

### Linking a save to a server game by hand

In the agent's **Add Games** page, each emulator save says what will happen when you add it: **Joins "Chrono Trigger"** or **New game "Chrono Trigger" on the server**. Press **Change** to choose differently:

- **Automatic** — the choice SaveLocker made.
- **Another server game** with the same title or the same files, to join that one instead.
- **Keep it as its own game**, to keep this save apart from the fleet's game of the same files. This machine then also stops tracking that other game, so the same save is never synced into two games.

A choice marked *chosen by you* has **Back to automatic** beside it. If the server can't be reached, the row says so, and the link is decided when you add the game. Game Mode always links automatically.

## Things Add games won't add

- **A memory card every game shares.** PCSX2's card file, Dolphin's raw card and DuckStation's shared card hold every game's saves, so restoring one game would restore them all. They are listed greyed out, with how to switch the emulator to a card per game. See [Emulator backup best practices](#help/emulator-backups).
- **EmuDeck's preinstalled saves that were never played here.** EmuDeck ships a few saves with some emulators. One is listed only when the server already keeps that game, so this machine can take the fleet's save in its place. Adding it replaces the preinstalled file at the first pull, without asking you to force it.
- **A save from another emulator's game.** A save from one emulator never joins a game another emulator made, even for the same title. Metroid Prime Trilogy saved in Dolphin and in PrimeHack are two games.
- **Another region's release.** A USA and a European save of the same game are two games with the same name, because each disc reads only its own region's saves.

When a game can't be added, the status line under the list says why, after **Not added:**.

## Regions and names

PS1, PS2, GameCube, Wii, PSP and PS3 games are named from [Redump](http://redump.org/) disc lists, 3DS games from [No-Intro](https://no-intro.org/), and Xbox 360 games from both, through [libretro-database](https://github.com/libretro/libretro-database) (CC BY-SA 4.0). The region isn't part of the name: it shows beside it, as text in Add games and in the console's games list.

A PSN download, a PS Vita game or a PS4 game is named by the title stored in its save. The region of a PSN or Vita game comes from its game ID (`NPUB…` is USA, `PCSB…` Europe). A PS4 game ID names no region, so none is shown, and an Xbox 360 title ID is every region's release, so none is shown there either.

## Emulators with several users or profiles

- **RPCS3, Vita3K and shadPS4** keep saves per emulator user. Each machine syncs the user folder the game was found in when you added it, usually the first user (`00000001` in RPCS3, `00` in Vita3K, `1` or `1000` in shadPS4). If you switch the emulator to another user later, open **Settings → Currently Tracked Games** in the agent and use **Change save path** to point the game at the new user's folder.
- **Cemu** keeps saves per account inside the game's folder (`user/80000001` is the first account), so use the same Cemu account on every machine.
- **Xenia** keeps saves per profile. Each machine syncs into its own profile's folder, and saves a game keeps for the device rather than a player (Rock Band's calibration, some games' options) sync too, as a second folder. Xenia's profile itself (gamertag, achievements) and DLC are not synced.

## Seeing where a game came from

Every machine records how it found each game, for example *Emulator · RetroArch · SNES* or *Steam · Installed game*. The console shows it under each machine in a game's **Save paths per machine** table, and the agent shows it beside each game under **Settings → Currently Tracked Games**.
