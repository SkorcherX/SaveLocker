# Save-in-use safety (the settle gate)

## The problem it solves

When a game's window closes, its save is not necessarily on disk yet. Many games keep writing for several seconds after exit — flushing buffers, compressing a slot, writing a backup copy alongside the main file.

If SaveLocker archived the save folder the instant the process disappeared, it could capture that half-written state and publish it as a save version. You would not notice until you pulled it on another machine and found a corrupt or stale save.

The **settle gate** prevents this. Before any automatic backup, the agent waits until the game has demonstrably finished writing.

## How it works

After a game exits, the agent watches the save folder and waits for two things to be true at the same time:

1. **Nothing is changing** — the set of files, their sizes, and their modification times all stop moving.
2. **Nothing is being written** — no file in the folder is still held open for writing by another process.

Once both hold continuously for the **settle delay** (10 seconds by default), the folder is considered quiet and the backup proceeds. If the game touches a file during that window, the clock restarts.

> **How "still open for writing" is detected differs by OS.** On Windows the agent asks the filesystem directly (share-mode). The Linux kernel doesn't enforce that, so on the Steam Deck the agent instead scans `/proc/*/fd` to see whether any process still holds the save file open. Where it genuinely can't tell, it says so in the log and settles on the "nothing is changing" signal alone — the fingerprint of files, sizes, and modification times. The behaviour you see is the same; only the detection underneath changes.

```
Game closes
  └─ Settle gate: wait for the save folder to go quiet
       ├─ files still changing?      → keep waiting
       ├─ file still open for write? → keep waiting
       └─ quiet for the full delay   → proceed
  └─ Archive + push the save
  └─ Release the lease
```

The lease is held for the whole wait, so other machines still see the game as checked out while the agent is settling.

## The safety cap

A game that never goes quiet must not block syncing forever. After **120 seconds** the agent gives up waiting and backs up anyway, recording a line in the agent log:

```
[Game Name] still writing after 120s — pushing anyway.
```

The reasoning: a late backup is recoverable, no backup is not. If you see this line regularly for a game, something in its save folder is being written continuously — a log file or a cache is the usual culprit, and the fix is an exclude pattern rather than a longer delay.

## What is and isn't delayed

| Action | Waits for the settle gate? |
|--------|---------------------------|
| Automatic backup after a game exits | Yes |
| Automatic backup when save files change on their own | Yes |
| **Sync now** / manual push from the tray | No |
| Push triggered from the dashboard | No |
| Pull / restore | No |

Manual actions are never delayed — you chose the moment, so the agent trusts you and acts immediately.

## Changing the delay

The delay is per-machine, in the agent (not the dashboard):

1. Open the agent window from the tray icon (**Windows**). On a **Steam Deck** there is no tray — browse to `http://localhost:5178` in Desktop Mode. The UI is localhost-only; to reach it from another device, forward the port over SSH (`ssh -L 5178:localhost:5178 deck@<deck-ip>`).
2. Go to **Settings → Sync Safety**.
3. Set **Wait for saves to settle (seconds)** and click **Save**.

Accepted range is 0–300 seconds. The enrollment file can also seed this value, so a fleet of machines starts with the same settle delay without touching each one.

- **Raise it** if a game is slow to flush and you have seen an incomplete save reach the server. Going from 10 to 30 seconds costs you nothing except a slightly later backup.
- **Lower it** if you want backups to land faster and your games write their saves promptly.
- **Set it to 0** to disable the gate and archive immediately. This restores the old behaviour and is only sensible if a game writes its save synchronously on exit.

## How to tell it's working

Check the agent log (`%PROGRAMDATA%\SaveLocker\agent.log` on Windows, `~/.local/share/SaveLocker/agent.log` on Linux — or the **log** command in the agent CLI). After exiting a game you should see the backup happen *after* the wait, and if the agent had to wait at all it says so:

```
[Game Name] save files settled.
[Game Name] pushed new version.
```

## A note on games that hold files open forever

Some games keep a handle on their save file for the entire session and only release it at exit — that is normal and the gate handles it. A few keep a handle open even while idle in the background. If a game is still running, SaveLocker does not attempt an automatic backup at all, so this only matters once the process is gone.

If a *different* program (a cloud-storage client, a backup tool, an antivirus scanner) holds a save file open for writing indefinitely, the gate will wait out the full 120-second cap on every backup. The agent log will name the file it is waiting on.
