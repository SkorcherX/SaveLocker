# How syncing works

## Overview

SaveLocker uses a **hub-and-spoke** model: every machine (spoke) syncs its saves through a central server (hub). The server holds the canonical save history; agents push and pull against it.

## The save head

Each game has a **head** — the ID of the most recent accepted save version. When an agent pushes, the server advances the head. When an agent pulls, it downloads the current head. The head acts like the `HEAD` commit in git.

## Leases

Before an agent starts a game, it acquires a **lease** on that game from the server. A lease signals "this machine is about to play." Other machines can see the lease badge in the dashboard. Leases expire automatically (1 hour default) in case the agent crashes or the machine goes offline.

## The sync lifecycle

```
Pre-launch (game starts)
  └─ Acquire lease
  └─ Pull latest save from server → overwrites local save

Post-exit (game closes)
  └─ Settle gate — wait until the save folder stops changing and
     nothing is still open for writing (10 s default, 120 s cap)
  └─ Push local save → server checks parent, accepts or conflicts
  └─ Release lease
```

The settle gate is what stops a game that keeps flushing after exit from being archived half-written. See **Save-in-use safety** for how to tune it.

On **Windows** this lifecycle is driven by a process watcher that notices the game start and exit. On **Linux / Steam Deck** there is no watcher — you wrap the launch instead, with `savelocker run -- %command%` in the game's Steam launch options. The wrapper *is* the pre-launch/post-exit boundary: it pulls, runs the game to completion, settles, and pushes.

## Push flow

1. Agent hashes every file in the game's save folders (respecting exclude patterns), and the save as a whole.
2. It tells the server the save's file list and hashes, with the **parent version ID** (the last version this machine successfully pulled or pushed).
3. Server compares parent to current head → FastForward, NoChange, or Conflict.
4. For a new version of a larger save, the server says which files it doesn't already have, and the agent uploads **only those**: a 150 MB save where one 40 KB file changed uploads about 40 KB. The server builds the new version from those files and the ones it already holds. A small save is simply uploaded whole.
5. Large uploads are sent in chunks and are **all-or-nothing**: an upload that is interrupted publishes no version and leaves nothing half-written on the server.

## Pull flow

1. Agent requests the current head version from the server.
2. Server streams the zip.
3. Agent replaces each of the game's save folders with the version's files. Files this machine **excludes** (see [exclude patterns](#help/glob-patterns)) are left where they are, since they were never part of the save.

A game can sync several folders; each is pushed and pulled together as one version. See [Games with more than one save folder](#help/multiple-save-folders).

## When every save file disappears

If every file in a game's save folder vanishes at once, the agent holds the game instead of syncing the empty folder to your other machines, and asks you what to do. See [When every save file disappears](#help/save-folder-emptied).

## Offline queue

If the server is unreachable, the agent stores pending pushes in a local **offline queue** (`%PROGRAMDATA%\SaveLocker\offline-queue.json` on Windows, `~/.local/share/SaveLocker/offline-queue.json` on Linux). A background timer retries the queue every 30 seconds once the server becomes reachable again.
