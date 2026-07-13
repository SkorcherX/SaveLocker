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

## Push flow

1. Agent zips the save directory (respecting exclude patterns).
2. Hashes the archive.
3. Sends the archive with the **parent version ID** (the last version this machine successfully pulled or pushed).
4. Server compares parent to current head → FastForward, NoChange, or Conflict.

## Pull flow

1. Agent requests the current head version from the server.
2. Server streams the zip.
3. Agent atomically replaces the save directory with the extracted archive.

## Offline queue

If the server is unreachable, the agent stores pending pushes in a local **offline queue** (`%PROGRAMDATA%\SaveLocker\offline-queue.json`). A background timer retries the queue every 30 seconds once the server becomes reachable again.
