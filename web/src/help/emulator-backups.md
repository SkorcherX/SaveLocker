# Emulator backup best practices

SaveLocker can back up emulator saves just like other game saves. It preserves files byte-for-byte, and each machine can map the same game to a different local folder. A Windows PC and a Linux or Steam Deck agent do not need to use the same path.

The important distinction is **what kind of emulator save you are syncing**. In-game saves are generally portable. Save states are snapshots of an emulator's internal state and need more care.

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
2. Create one server game for the emulated title or shared memory card.
3. Map the smallest non-overlapping save folder on each machine; put a standalone memory-card file in a dedicated folder.
4. Add exclude patterns for caches, logs, screenshots, and other generated files.
5. Confirm both machines use compatible ROMs, emulator versions, cores, BIOS files, and settings.
6. Push from the machine containing the authoritative save.
7. Pull on the other machine and test both an in-game save and, if used, a save state.
8. Keep the independent copy until you have completed a successful round trip.
