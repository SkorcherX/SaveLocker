# Understanding sync conflicts

## What is a conflict?

A conflict occurs when the server rejects an upload because the save you're pushing is not a direct child of the current server head — meaning another machine already advanced the save history while your machine wasn't looking.

## The three upload outcomes

When an agent pushes a save, the server compares your upload's **parent version ID** to the current head:

| Outcome | What it means |
|---------|---------------|
| **NoChange** | Your save hash matches the current head — nothing to store. |
| **FastForward** | Your parent matches the current head — save accepted, head advances. |
| **Conflict** | Your parent does not match the current head — save rejected, conflict flag raised. |

## Why does a "behind" machine keep conflicting?

When a conflict is raised, the server sets a conflict flag for that game. The pushing machine's **known parent** is now stale — it still points to its last successful push, not the new head that the other machine wrote. Until the conflict is resolved, every subsequent push from the behind machine will also conflict because its parent is still wrong.

The agent does **not** automatically advance its known parent when a conflict occurs — it waits for you to resolve it explicitly so you can decide which save to keep.

## How to resolve a conflict

**Option A — Dashboard resolve (recommended):**
1. Open the dashboard and find the game showing a **conflict** badge.
2. Click the game, then click **Resolve conflict** in the Conflicts section.
3. Choose which version to keep (your local save or the server's current head).
4. After resolving, the conflicting machine will pull the winning version on its next sync.

**Option B — Force Pull from the agent:**
1. **Windows:** right-click the SaveLocker tray icon → open the agent window, find the game, and click **Force Pull**.
2. **Linux / Steam Deck** (no tray): run `savelocker pull <game> --force`.
3. Either way this overwrites your local save with the current server head and resets the known parent, breaking the conflict loop.

## Conflicts on a Steam Deck

A Deck is headless — it can't pop a conflict banner. It is **not** silent, though: the agent reports the stuck game to the server, so the console shows a **problem badge** in the nav bar and marks that machine in **Configuration → Machines** (per-machine health: online / offline, agent version, last sync). Resolve it the same way — dashboard **Resolve conflict**, or `savelocker pull --force` on the Deck. The event auto-clears once that machine syncs the game cleanly again.

## Common causes

- **Two machines editing the same game offline.** Both accumulate local saves; whichever pushes second will conflict.
- **Version skew between agents.** Different versions may hash archives differently, making the server see every push as a new divergence. Keep all agents on the same version.
- **Manually copying save files** without going through the agent. The agent loses track of the parent version.

## Prevention

- Always **launch the game through Steam** (or your launcher) so the agent's pre-launch pull runs first.
- Keep all agents on the **same version** — install updates before playing on a new machine.
- Resolve conflicts promptly; a stale conflict blocks clean syncs for the affected game.
