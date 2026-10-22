# Backups and restoring

The server keeps its **version history** in one SQLite database: which games exist, every version of every save, which machine uploaded it, open conflicts, machines, settings and the audit log. The save files themselves are separate archives on disk. The server backs up both on its own, and any backup can be restored from the **Backups** page.

## What a backup contains

Each backup is one zip file:

- **The database**, compressed. It is taken with SQLite's `VACUUM INTO`, which is safe while the server keeps working.
- **Every game's latest save**: the version marked Latest at that moment. Older versions are **not** included, which keeps backups small.
- A small `manifest.json` listing which saves are inside.

Cover art and hosted agent installers are not included. Backups from before this format (`.db` files) contain the database only; they are still listed and can still be restored.

## Credentials in a backup

- **Machine API keys, the admin password and enrollment tokens** are stored as one-way **hashes**. Neither a backup nor the database can give them back.
- **The SteamGridDB key** has to be usable by the server, so it is **encrypted** instead. The key that decrypts it sits in `/data/keys`, next to the database but outside it, and is in **no** backup. A backup restored onto a different server therefore shows the SteamGridDB key as not set. Enter it again under Configuration.

Treat a downloaded backup as private anyway: it is your whole save history.

## When backups are taken

| Reason | When |
|---|---|
| **Scheduled** | Daily or weekly (default: weekly, Sunday 03:00 **UTC**). Change it under **Schedule** on the Backups page. If the newest backup is older than one interval when the server starts, one is taken right away. |
| **Manual** | When you press **Back up now** on the Backups page. |
| **Before upgrade** | When the server starts on a **different build** from the one that last ran, *before* any database migration. This one holds the database only. |
| **Before restore** | Automatically, right before any restore. It captures the current state, so a restore can be undone. |

All times are UTC, the server's own clock. Only the newest backups are kept (7 by default); older ones are deleted. To delete one yourself, press **Delete** on its row and confirm. A deleted backup can't be restored, so download it first if you may need it.

## Where they are

In the container, backups are stored in `/data/backups/`, named `savelocker-YYYYMMDD-HHMMSS[-reason].zip` in UTC. On the host, that folder is inside the path you mapped to `/data`, for example `/mnt/user/appdata/savelocker/backups/` on unRAID.

## Restoring

On the **Backups** page, press **Restore** on a row and confirm. The server then:

1. checks that the backup's database is intact and really is a SaveLocker database, and refuses without changing anything if it isn't;
2. takes a **Before restore** backup of the current state;
3. replaces the database with the backup's, while the server keeps running, and brings it up to this build's schema;
4. puts back any latest save from the backup that is missing on disk. Existing save files are never overwritten, because a save archive never changes once written.

Everything returns to the moment of the backup: games, versions, machines, settings (including the admin password and this backup schedule) and the audit log. If the backup's admin password differs from today's, enter that one in the nav bar. **To undo a restore**, restore the **Before restore** backup it made.

After a restore, older versions whose files were pruned since the backup still appear in the list, but they can't be downloaded. Machines aren't told about the restore. After their next sync, check the Games page and resolve any conflict a machine that saved since the backup reports. A machine **enrolled after** the backup isn't in the restored database, so its key stops working: enroll it again.

If a restore reports that it **did not finish**, the database *was* replaced but couldn't be brought up to this build. Restart the server to finish it, or restore the **Before restore** backup named in the message to go back. The newest **Before restore** backup is always kept, however few backups you keep, so the last restore can always be undone.

**Download** saves the file straight to disk, however large, through a one-time link that works for about a minute.

### Restoring by hand (server won't start)

1. **Stop the container.**
2. In the folder mapped to `/data`, rename the current `savelocker.db` (or `localgamesync.db` on older installs) to keep it, and delete any `-wal` / `-shm` files next to it.
3. Open the backup zip. Copy `savelocker.db` into that folder, and copy the files under `archives/` into `/data/archives/`, keeping their folders.
4. **Start the container.**
