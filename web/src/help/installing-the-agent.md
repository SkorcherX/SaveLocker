# Installing the agent

The agent is the piece that runs on each gaming machine: it watches your save folders, uploads
changes, and pulls the latest save before you play. The server (this console) is the hub; every PC,
laptop and Steam Deck runs an agent.

Downloads are on the [GitHub Releases page](https://github.com/SkorcherX/SaveLocker/releases):

| Machine | Download |
|---|---|
| Windows | `SaveLocker-Agent-Setup-<version>.exe` |
| Linux / Steam Deck | `savelocker-<version>-linux-x64.tar.gz` |

## First: create an enrollment file

Do this **before** touching the other machine. It works the same for Windows and Linux, and it means
**no API key is ever copied by hand**.

1. In this console: **Configuration → Enroll a machine**.
2. Optionally name the machine (e.g. `Steam Deck`). Naming it *binds* the file to that name, so it
   cannot be used to claim a different machine's identity.
3. Click **Create enrollment file**. A `.json` file downloads.
4. Copy that file to the new machine (USB stick, network share, `scp` — however you like).

The file carries a **single-use token that expires in about 15 minutes**, not a permanent key. If you
lose it, or it expires, just create another. It is downloaded **once and cannot be shown again** — the
server only keeps a hash of it.

---

# Windows

## Install

1. Download and run `SaveLocker-Agent-Setup-<version>.exe`.
2. **Windows will warn you that the publisher is unknown.** Click **More info → Run anyway**. The
   installer is not code-signed yet, so SmartScreen has no publisher to check against. This is
   expected, and worth knowing rather than being surprised by.
3. The installer needs **administrator rights** (it installs to `C:\Program Files\SaveLocker Agent`
   and registers the auto-start entry, so the uninstaller can cleanly remove both).

The agent starts in the **system tray**. Double-click the icon to open its window.

## Enroll it

Open a terminal and point the agent at the enrollment file you downloaded:

```
"C:\Program Files\SaveLocker Agent\SaveLocker.Agent.exe" enroll --file C:\Users\you\Downloads\savelocker-enroll-desktop.json
```

That sets the server URL, registers this machine, and picks up the games already defined on the
server. Then confirm it appears under **Configuration → Machines** in this console, showing as
**online**.

> **No console access?** You can still set it up by hand from the agent's **Settings** tab: enter the
> server URL and register. Enrollment is the easier path, not the only one.

## Notes

- **Start with Windows** is a toggle in the agent's Settings. It uses a registry Run-key entry, which
  the uninstaller removes.
- **Auto-update works on Windows.** When the console hosts a newer installer, the agent offers it and
  can install it silently.
- Uninstall from **Add or remove programs**. It asks before deleting `%PROGRAMDATA%\SaveLocker`
  (your API key and tracked games) — answer **No** if you plan to reinstall.

---

# Linux and Steam Deck

The Linux agent is **headless by design**. There is no tray icon and no pop-ups: in Game Mode a Deck
has no desktop to put them on. **This console is the Deck's UI** — it is where the agent reports
problems, and where you will see them.

The download is **self-contained**: it bundles its own .NET runtime, so there is nothing to install
first. (It needs glibc 2.27 or newer; every SteamOS 3 release is well past that.)

## Install

On a Steam Deck, switch to **Desktop Mode** first (**Steam button → Power → Switch to Desktop**).

```bash
tar -xzf savelocker-<version>-linux-x64.tar.gz
./SaveLocker/install.sh
```

This installs to **`~/.local/share/SaveLocker`**, links `savelocker` into `~/.local/bin`, and enables
a `systemd --user` service so the agent starts with your session.

> **Nothing is installed to `/usr`, and that is deliberate.** SteamOS's root filesystem is
> **immutable and wiped on every system update** — anything installed there would silently vanish.
> Everything lives in your home directory, which survives updates.

If `install.sh` warns that `~/.local/bin` is not on your `PATH`, add it (or use the full path
`~/.local/share/SaveLocker/savelocker`).

## Enroll it

```bash
savelocker enroll --file ~/Downloads/savelocker-enroll-steamdeck.json
savelocker doctor
```

**`savelocker doctor` is the command to remember.** On a machine with no UI, it is how you find out
what is wrong: it checks the whole chain — server reachable, Steam found, shortcuts parsed, Proton
prefixes located, save folders present and writable — and prints a `✗` next to anything broken. If
you ever ask for help, paste its output.

## Sync a game: the launch wrapper

Tell Steam to run the game *through* the agent. In the game's **Properties → Launch Options**:

```
savelocker run -- %command%
```

That is the whole integration. The agent pulls the latest save before the game starts, waits for it
to exit, waits for the save to finish being written, and pushes it.

> ### ⚠️ The step everyone misses
> For a **non-Steam game**, you must also tick **"Force the use of a specific Steam Play
> compatibility tool"** (Properties → Compatibility) and pick a Proton version.
>
> Without it, Steam launches the game without Proton, **no Wine prefix is ever created**, and there is
> nothing for the agent to sync. `savelocker doctor` will tell you the prefix is missing.

## What SaveLocker syncs on Linux — read this before you file a bug

- ✅ **Non-Steam games added to Steam as shortcuts**, run through Proton. This is the case SaveLocker
  exists for.
- ❌ **Games you bought on Steam.** They already have **Steam Cloud**, which does this job. SaveLocker
  deliberately does not compete with it. This is a scoping decision, not a missing feature.
- ❌ **Native Linux builds** of a game. A Proton game *is* a Windows game — it writes Windows-format
  saves — so a Deck save and a Windows PC save are **the same save**, and can be synced between them
  freely. A *native Linux* build writes a different format, and syncing that into a Windows install
  would corrupt it. So we don't.

## Reaching the agent's own UI

The daemon serves the same web UI the Windows tray shows, on port **5178**. In Desktop Mode, browse to
`http://localhost:5178`. To reach it from your PC or phone instead, run the daemon with `--lan` and
browse to the Deck's IP on port 5178.

## Notes

- **There is no auto-update on Linux yet.** To update, download the newer tarball and run
  `install.sh` again — it installs over the top and keeps your configuration.
- The agent stops when you log out unless *lingering* is enabled. On a Deck that is usually fine (you
  are logged in whenever you are playing). To keep it running regardless:
  `sudo loginctl enable-linger $USER`.
- Log file: `~/.local/share/SaveLocker/agent.log` (not `%PROGRAMDATA%` — Linux state lives under
  XDG paths). Tail it with `savelocker log`.

---

## Confirming it worked

On either platform, the machine should appear in this console under **Configuration → Machines**,
showing **online**, with its agent version and last sync time. If a machine is failing to sync,
it says so there — and a ⚠ badge appears in the top bar.

Next: **[Adding games & mapping save folders](#help/adding-games)**.
