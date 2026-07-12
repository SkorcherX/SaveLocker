# Save retention

## How versions are stored

Every accepted push creates a new **save version** — a zip archive stored on the server. Versions are immutable; the server never overwrites an existing archive.

## Retention policy

By default, SaveLocker keeps the **last 10 versions** per game. When a new version is accepted and the count exceeds the limit, the oldest versions are pruned automatically.

The retention limit is configurable in **Configuration → Save retention** (versions per game). Setting it higher uses more disk space; lower means less history available for rollback.

## Viewing version history

In the dashboard:
1. Select a game.
2. Scroll to the **Versions** card in the game detail panel.
3. Each version shows its timestamp, size, and uploader machine.

## Restoring an older version

From the Versions card, click **Restore** on any historical version. This sets that version as the new head. The next pull on any machine will download the restored version.

> **Note:** Restoring does not delete the versions between the restore point and the previous head. They remain in history until pruned by the retention policy.

## Disk usage

Total save storage per game is shown in the sidebar badge and in the game detail panel. If a game is using unexpectedly large storage, check for a misconfigured save path or missing [exclude patterns](#help/glob-patterns).
