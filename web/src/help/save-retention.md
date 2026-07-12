# Save retention

## How versions are stored

Every accepted push creates a new **save version** — a zip archive stored on the server. Versions are immutable; the server never overwrites an existing archive.

## When are old versions pruned?

Pruning runs **immediately after every successful upload** — not on a schedule. The moment a new version is accepted, the server checks the total count for that game and deletes the oldest versions beyond the limit.

This means:
- If a game is sitting at the limit and no one pushes a new save, nothing is pruned.
- If you lower the retention limit, old versions are not removed until the next push.

## Why might I see more versions than the limit?

**Open conflict versions are protected from pruning.** When a conflict exists, the two versions involved in that conflict are never deleted — even if they would otherwise be pruned — because you may still need to pick one as the winner. Once the conflict is resolved, those versions become eligible for pruning on the next upload.

So if your limit is 5 and you have an open conflict, you may see 6 or more versions until the conflict is resolved and another push runs.

## Is the retention limit per-agent or per-server?

**Per-server (per-game).** The retention limit is enforced server-side, and all agents share it. There is no per-agent retention setting. If you have two machines both pushing saves to the same game and the limit is 5, the server still enforces a total of 5 versions — not 5 per machine. Whichever agent pushes a new save will trigger the prune that removes the oldest version across all machines.

## Configuring the limit

Retention is set **per-game** in the dashboard, with a server-wide global default as the fallback:

- **Per-game:** select a game → scroll to the **Versions** card → set the retention limit.
- **Global default:** Configuration → Save retention. Applies to any game that doesn't have a per-game override.

Setting the limit to `0` disables pruning entirely for that game (versions accumulate until you manually delete them).

## Viewing version history

In the dashboard, select a game and scroll to the **Versions** card. Each version shows its timestamp, size, and which machine uploaded it. You can restore any listed version as the new head.

## Disk usage

Total save storage per game is shown in the sidebar badge and in the game detail panel. If a game is using unexpectedly large storage, check for a misconfigured save path or missing [exclude patterns](#help/glob-patterns), or lower the retention limit.
