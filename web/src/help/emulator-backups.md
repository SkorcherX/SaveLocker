# Emulator backup best practices

SaveLocker can back up emulator saves just like other game saves. It preserves files byte-for-byte, and each machine can map the same game to a different local folder. A Windows PC and a Linux or Steam Deck agent do not need to use the same path.

The important distinction is **what kind of emulator save you are syncing**. In-game saves are generally portable. Save states are snapshots of an emulator's internal state and need more care.

## Emulators Add games finds by itself

Add games lists one game per save for RetroArch, melonDS, Supermodel, Model 2, ScummVM, PCSX2, DuckStation, Dolphin, PrimeHack, PPSSPP (PSP), RPCS3 (PS3), Vita3K (PS Vita), shadPS4 (PS4), Cemu (Wii U), Azahar (3DS, and Citra's old folders) and Xenia (Xbox 360). It looks in EmuDeck's folders on SteamOS and Windows first, then in standalone installs where SaveLocker knows the folder. Each game keeps only its own files inside a folder other games share, and its save states come with it. A PSP game you have only played from save states so far is listed too, marked *Save states only so far*: adding it syncs its states, and its in-game saves join it once the game makes one. Other emulators list a game only once it has an in-game save.

**A memory card every game shares is never added as one game.** PCSX2's card file (`Mcd001.ps2`, 8–64 MB), Dolphin's `MemoryCardA.USA.raw` and DuckStation's `shared_card_1.mcd` hold every game's saves, so restoring one game's save would restore them all. Add games shows such a card greyed out, with how to switch the emulator to a card per game:

- **PCSX2:** Settings > Memory Cards, right-click the card > Convert > Folder, then right-click the new card > Use for Slot 1. The saves are kept.
- **Dolphin:** Options > Configuration > GameCube, set Slot A to GCI Folder. Dolphin copies the old card's saves into the folder by itself.
- **DuckStation:** Settings > Memory Cards, set Card 1 to Separate Card Per Game (Title). The new cards start empty; copy the saves over with Tools > Memory Card Editor.

**PlayStation, GameCube and Wii games are named by their disc's serial** — *Prince of Persia - The Two Thrones*. Each region's disc has its own serial and reads only its own saves, so a USA save and a European save of the same game are two games with the same name. The region isn't part of the name: it's shown beside it — as text in game lists and Add games, and on the game's page as a chip. Hover over the chip, or click or tap it, to see the release's details: region, game ID, console, languages and the save's files. A save only joins a game of its name from the same region. Titles come from [Redump](http://redump.org/) via [libretro-database](https://github.com/libretro/libretro-database) (CC BY-SA 4.0); a game Redump doesn't list is named by the title inside its save. PSP and PS3 games work the same way. A PSN download, a PS Vita game or a PS4 game is named by the title in its save's `PARAM.SFO`. The region of a PSN game or a Vita game comes from its game ID (`NPUB…` is USA, `PCSB…` Europe). A PS4 game's ID names no region, so none is shown.

**Wii U, 3DS and Xbox 360 saves are filed under a title ID, not a name.** Nothing in the save says which game it is, so SaveLocker names it from what this machine has:

- **Xbox 360 (Xenia):** from the title ID — disc games from [Redump](http://redump.org/), Arcade games from [No-Intro](https://no-intro.org/), both via libretro-database. One title ID is every region's release, so no region is shown.
- **3DS (Azahar):** from the game's ROM — in `Emulation/roms/n3ds`, Azahar's game folders, or the installed game itself — by its product code (`CTR-P-AREE`), from No-Intro's 3DS list, with the region beside the name. With no ROM on the machine, it's named by its file name or `3DS <title ID>`.
- **Wii U (Cemu):** from Cemu's own game list, or the installed game's `meta.xml`. Otherwise it's named `Wii U <title ID>`.

The name only matters to the first machine that adds the game. Another machine joins it by its save folder, whatever it calls it. Only the save syncs: a 3DS game's `data` folder, never the installed game beside it; an Xbox 360 game's saved games, never its DLC or updates. Xenia keeps saves per profile, and each machine syncs into its own profile's folder. Saves a game keeps for the device rather than a player (Rock Band's calibration, some games' options and high scores) sync with the game too. Cemu keeps them per account inside the game's folder (`user/80000001` is the first account), so use the same Cemu account on every machine.

**A save from one emulator never joins another emulator's game**, even for the same title: Metroid Prime Trilogy saved in Dolphin and in PrimeHack are two games, and never sync with each other.

**RPCS3, Vita3K and shadPS4 keep saves per emulator user.** The save itself belongs to no user, so a save made by one machine's user loads for any user on another machine. Each machine syncs the user folder the game was found in when you added it — usually the emulator's first user (`00000001` in RPCS3, `00` in Vita3K, `1` or `1000` in shadPS4). If you switch the emulator to another user later, SaveLocker keeps syncing the first user's saves; on that machine, open the game, click **Change** beside its save folder and pick the new user's `savedata` folder. When two users on one machine have saves for the same game, Add games offers the one written last.

## Prefer in-game saves

Whenever possible, sync the files created by the game's own save system:

- Battery-backed saves, commonly `.sav` or `.srm`
- Virtual memory cards
- Per-game save folders created by the emulator

These formats are usually more stable across emulator updates and may work across compatible emulators or cores. Test compatibility before relying on that portability.

Use save states as a convenience, not as your only backup. Keep at least one recent in-game save even if you regularly use states.

## Treat save states as emulator-specific

Save states can depend on the exact emulator, core, emulator version, ROM revision, settings, and sometimes the operating system or CPU architecture. A state that loads on one machine may fail or behave incorrectly on another even when the file synced successfully.

For the safest results, keep these identical on every machine:

- Emulator and core
- Emulator/core version
- ROM or disc revision, including patches and region
- BIOS and firmware files where applicable
- Settings that affect emulation or memory layout

After updating an emulator or core, create a normal in-game save before the update, then confirm existing states still load. Do not overwrite your last known-good state until they do.

## Map the smallest safe folder

Create a separate SaveLocker game for each emulated title and map only that title's save folder or isolated state folder. SaveLocker maps directories, not individual files. If the emulator uses one memory-card file, place or keep it in a dedicated folder and map that folder.

Avoid mapping the emulator's entire data directory. It may contain ROMs, BIOS files, shader caches, screenshots, logs, configuration, and states for unrelated games. Those files waste storage, can exceed the 200 MB upload limit, and may cause unwanted changes on another machine.

If several games share one memory-card file, treat its containing folder as one sync unit. Do not configure separate SaveLocker games that manage overlapping folders.

> **Important:** A pull mirrors the selected backup into the mapped folder. Files in that folder that are not present in the downloaded version are removed. Never map a broad parent directory unless everything inside it belongs to that backup.

Use [exclude patterns](#help/glob-patterns) for content that does not belong in the backup, such as:

```text
*.log
cache/**
screenshots/**
shaders/**
```

## Map each machine separately

The server tracks the game once, while every agent stores its own local path. For example, the same emulator save can map to:

```text
C:\Emulators\Saves\Example Game
/home/deck/Emulation/saves/Example Game
```

Add the game on the server, then set the correct save path on each machine. See [Adding games & mapping save folders](#help/adding-games).

Path differences do not affect synchronization. File and folder names *inside* the mapped directory do matter. Avoid names that differ only by letter case because Linux is case-sensitive while Windows normally is not.

## Close the emulator before syncing

Do not push, pull, or load a state while the emulator is writing its save files. Close the emulator normally and let SaveLocker wait for the folder to settle before it creates the backup.

Automatic pushes use the settle gate to wait for file sizes and write times to stop changing. Configure the emulator's process or launch integration so SaveLocker pulls before launch and pushes after exit. See [Save-in-use safety](#help/save-in-use-safety).

Manual pushes skip the settle wait. Before using one, close the emulator and wait for any save or state operation to finish.

## Avoid using two machines at once

Do not run the same emulated game on two machines simultaneously. Let the first machine exit and finish pushing before launching on the second machine. The second machine should pull Latest before play.

If both machines create different saves, SaveLocker records a conflict instead of trying to merge binary files. Choose the version you want to keep in the dashboard. See [Understanding sync conflicts](#help/conflicts) and [Best practices for multiple machines](#help/multi-machine).

## First-time setup checklist

1. Make an independent copy of the current save or memory-card file.
2. Create one server game for the emulated title. Map a shared memory card as one game only if you accept that restoring it restores every game on it; SaveLocker warns when you do.
3. Map the smallest non-overlapping save folder on each machine; put a standalone memory-card file in a dedicated folder.
4. Add exclude patterns for caches, logs, screenshots, and other generated files.
5. Confirm both machines use compatible ROMs, emulator versions, cores, BIOS files, and settings.
6. Push from the machine containing the authoritative save.
7. Pull on the other machine and test both an in-game save and, if used, a save state.
8. Keep the independent copy until you have completed a successful round trip.
