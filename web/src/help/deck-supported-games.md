# What syncs on Steam Deck (and what doesn't)

The most common Steam Deck question is *"why doesn't my Steam game sync?"* — usually because SaveLocker is doing exactly what it should. Here is the scope, up front, so nothing surprises you.

## What SaveLocker syncs

✅ **Non-Steam games added to Steam as shortcuts, run under Proton.** This is the case SaveLocker exists for — the games Steam itself doesn't back up. Add the game to Steam, launch it through Proton, and add `savelocker run -- %command%` to its launch options.

Two save layouts both work, and you don't have to tell SaveLocker which you have:

- **In-prefix** — the save lives inside the game's Wine prefix (`compatdata/<appid>/pfx/…`), where a Windows game would put it under `AppData` or `Documents`.
- **Portable** — the save sits next to the game's `.exe`.

## What SaveLocker deliberately does *not* sync

❌ **Games you bought on Steam.** They already have **Steam Cloud**, which does this job well. SaveLocker does not compete with it and deliberately leaves those saves alone. This is a scoping decision, not a missing feature — turn on Steam Cloud for those and let it handle them.

❌ **Native Linux builds of a game.** This one matters, because syncing it would *corrupt* your save. A Proton game is a Windows game: it writes Windows-format saves, so a save from your Windows PC and a save from the Deck under Proton are interchangeable. A *native Linux* build of the same game writes a different save format. Pushing a native-Linux save into a Windows install (or vice-versa) would overwrite a good save with one the other side can't read. So SaveLocker won't. If a game offers both, **run the Windows build under Proton** on the Deck to keep it in sync with your PC.

## Your PC save and your Deck save are the same save

Because a Proton game writes Windows-format saves, **a save made on a Windows PC and a save made on the Deck under Proton are the same save** — you can move progress between them freely. This isn't a hopeful claim: it's checked on every build, where a save is pushed from a Windows agent, pulled by the Linux agent, and byte-compared in both directions with an identical content hash.

## Still not syncing?

If a game *should* sync by the rules above but doesn't, it's almost always one of a few Deck-specific setup issues — a game never launched through Proton (no prefix yet), a non-Steam shortcut missing the forced compatibility tool, or a save folder that needs mapping by hand. Run `savelocker doctor`; it names the problem. See [Troubleshooting](#help/troubleshooting) and [Installing the agent](#help/installing-the-agent).
