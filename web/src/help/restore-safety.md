# When SaveLocker refuses to restore a save

Sometimes a pull stops and the agent says it **refused** the save the server sent:

```
[Hollow Knight] REFUSED the server's save: Refusing to restore:
'/home/deck/.../saves/backup' is a symlink/junction, so writing the archive
there would modify files outside the save folder.
```

Nothing was written when you see this. A refused restore is never a half-restore — the agent checks the archive first and either applies all of it or none of it, so your existing save is exactly as it was.

These checks exist because a save archive arrives **over the network**. The agent is designed so you never have to hand-copy an API key, which means it will happily talk to whatever server its enrollment file pointed it at. If that were ever the wrong server, the archive it sends is untrusted data being unpacked onto your disk. So the agent treats every archive as untrusted, no matter which server it came from.

## "… is a symlink/junction"

**What it means.** Somewhere inside your save folder there is a shortcut — a symlink on Linux, a junction on Windows — pointing at a directory *elsewhere* on your disk. The archive contains a file whose path goes through that shortcut. Writing it would land the file outside your save folder entirely, overwriting whatever is already there.

**Why it's blocked.** The agent can't tell a shortcut you created on purpose from one that happens to line up with a path an archive chose. Since the wrong guess means overwriting files that have nothing to do with the game, it stops and asks you.

**What to do.** The message names the exact path. Either:

- **Remove the link** from inside the save folder, if it isn't doing anything useful, or
- **Point the game's save folder at the real directory** instead of going through the link — `savelocker add-game --name "<game>" --dir <real path>` on Linux, or **Set save folder…** in the agent window on Windows.

**Note the exception:** if the save folder *itself* is a symlink — you keep saves on an SD card and link them into the prefix, say — that is fine and keeps working. Only links *inside* the folder are refused.

## "… over the limit" (too large, or too many files)

**What it means.** The archive would expand to more than **2 GB**, or contain more than **100,000 files**. The agent checks this before extracting anything.

**Why it's blocked.** A small compressed file can be crafted to expand into something enormous — enough to fill the disk. On a Deck that happens with nobody watching, and a full disk breaks far more than SaveLocker.

**What to do.** First ask whether the size is plausible for that game. A save folder that big usually means the **wrong folder is mapped** — most often a whole Wine prefix rather than the save directory inside it. Run `savelocker doctor`, which names that specific mistake.

If the save really is that large and you want it synced anyway, raise the limits with environment variables:

```sh
SAVELOCKER_MAX_RESTORE_MB=4096 savelocker pull "<game>" --force
```

| Variable | Default | Controls |
|---|---|---|
| `SAVELOCKER_MAX_RESTORE_MB` | `2048` | Total uncompressed size |
| `SAVELOCKER_MAX_RESTORE_ENTRIES` | `100000` | Number of files |

To make it permanent for the daemon, set the variable in the `systemd --user` unit rather than typing it each time.

## "… resolves outside the target directory"

**What it means.** The archive contains a file path that tries to escape the save folder — for example `../../something`.

**Why it's blocked.** SaveLocker never *creates* archives like this. An archive built by any SaveLocker agent contains only ordinary paths inside the save folder.

**What to do.** Treat this one as a real signal rather than a configuration problem. Check that your agent is pointed at the server you expect (`savelocker whoami` shows the server URL), and that the archive on the server came from a machine you recognise — **Configuration → Machines** in the console shows which machine pushed which version.

## Where to see this on a Steam Deck

A Deck has no tray and no pop-ups, so a refusal appears in two places:

- **The console** — the agent reports it, so a problem badge appears in the nav bar and the machine is flagged in **Configuration → Machines**.
- **The agent log** — `~/.local/share/SaveLocker/agent.log`, or run `savelocker log`.

A refused restore is reported like any other failure, so you don't have to be watching the Deck to find out about it.

## Related

- [Troubleshooting](#help/troubleshooting) — `savelocker doctor` and the full failure list
- [Adding games & mapping save folders](#help/adding-games) — fixing a wrongly mapped folder
- [Save-in-use safety](#help/save-in-use-safety) — the other safety gate, on the push side
