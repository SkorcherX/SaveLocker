# Best practices for multiple machines

## Keep all agents on the same version

Different agent versions may hash save archives differently. If two machines are on different versions, the server may see every push as a new divergence and raise a conflict even when the save files haven't changed.

**Rule: before playing on any machine, check the agent version matches across all machines.** The dashboard's **Configuration → Machines** list shows the agent version for every machine (the Windows tray tooltip shows it locally too, and `savelocker doctor` prints it on a Deck) — the console is the one place you can compare them all at once.

## Always launch through your game launcher

The agent hooks into Steam (and other launchers) to run its **pre-launch pull** before the game opens. If you start the game executable directly — bypassing Steam — the pre-launch pull is skipped, and you'll play with whatever save was last written locally, which may be stale.

Always open games through Steam (or whatever launcher SaveLocker is configured to watch) so the pre-launch pull runs first.

## Resolve conflicts before switching machines

If a conflict is open on Machine A and you switch to Machine B without resolving it, Machine B will pull the head (which is fine), but Machine A will keep conflicting the next time it tries to push. Resolve conflicts promptly from the dashboard before leaving a machine.

## Don't hand-copy save files

Manually copying save files between machines breaks the agent's knowledge of the parent version. The agent won't know which version to use as its parent on the next push, and a conflict is likely.

Always let SaveLocker handle the transfer: resolve via the dashboard, Force Pull from the tray (Windows), or `savelocker pull --force` (Linux).

## Understand the conflict: it's a divergence, not an error

A conflict doesn't mean something is broken — it means two machines wrote independent saves and the server can't automatically merge them (game saves aren't mergeable). You decide which version wins. See [Understanding sync conflicts](#help/conflicts) for the full resolution workflow.
