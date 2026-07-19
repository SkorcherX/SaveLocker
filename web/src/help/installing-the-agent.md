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

## Install and enroll — one flow

1. Download and run `SaveLocker-Agent-Setup-<version>.exe`.
2. **Windows will warn you that the publisher is unknown.** Click **More info → Run anyway**. The
   installer is not code-signed yet, so SmartScreen has no publisher to check against. This is
   expected, and worth knowing rather than being surprised by.
3. The installer needs **administrator rights** (it installs to `C:\Program Files\SaveLocker Agent`
   and registers the auto-start entry, so the uninstaller can cleanly remove both).
4. On the **Enroll this machine** page, leave **"Enroll this machine now"** selected and browse to
   the enrollment file you downloaded. The installer shows **which server and machine name** the file
   will join — check the server address is one you trust before continuing. The machine is enrolled as
   the install finishes, so it is **online in this console before the installer window closes**.

The agent then starts in the **system tray**. Double-click the icon to open its window, and confirm
the machine appears under **Configuration → Machines** here, showing as **online**.

> **Enrolling later instead.** Choose **"Skip — I'll enroll later"** on that page to install without
> enrolling. When you have the file, enroll from the agent's **Settings** tab, or from a terminal:
>
> ```
> "C:\Program Files\SaveLocker Agent\SaveLocker.Agent.exe" enroll --file C:\Users\you\Downloads\savelocker-enroll-desktop.json
> ```
>
> Either way, if the file has **expired** (they last ~15 minutes) the installer says so on the page —
> just create a new one from the console.

> **Deploying to many machines?** Pass the file on the command line for an unattended install:
> `SaveLocker-Agent-Setup-<version>.exe /SILENT /ENROLL="C:\path\savelocker-enroll.json"`.

## Notes

- **Start with Windows** is a toggle in the agent's Settings. It uses a registry Run-key entry, which
  the uninstaller removes.
- **Auto-update works on Windows.** When the console hosts a newer installer, the agent offers it and
  can install it silently.
- Uninstall from **Add or remove programs**. It asks before deleting `%PROGRAMDATA%\SaveLocker`
  (your API key and tracked games) — answer **No** if you plan to reinstall.

---

# Linux and Steam Deck

The Linux agent is **headless by design** — meaning no tray icon and no pop-ups, since in Game Mode a
Deck has no desktop to put them on. **This console is where the agent reports problems**, because a
Deck cannot raise a toast to tell you itself.

It is *not* true that the agent has no UI of its own. The daemon serves the same web UI the Windows
tray shows at `http://localhost:5178` — reachable in Desktop Mode, and the easiest way to map a save
folder. See [Reaching the agent's own UI](#help/installing-the-agent) below.

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

Enrollment over HTTPS also records the server's TLS public key. If that identity later changes, the
agent warns but does not stop syncing — a legitimate certificate renewal may rotate the key, and a
hard failure would strand a headless Deck. If you expected the certificate or reverse-proxy change,
inspect the current pin and accept the new one:

```bash
savelocker trust
savelocker trust --accept
```

If you did not expect the change, stop and verify the server URL, DNS, tunnel, and certificate before
accepting it. A pull restores files into your save folders, so an unexpected server identity matters.
Plain `http://` connections have no TLS identity to pin.

**`savelocker doctor` is the command to remember.** On a machine with no UI, it is how you find out
what is wrong: it checks the whole chain — server reachable, Steam found, shortcuts parsed, Proton
prefixes located, save folders present and writable — and prints a `✗` next to anything broken. If
you ever ask for help, paste its output.

## Sync a game: the launch wrapper

Tell Steam to run the game *through* the agent. In the game's **Properties → Launch Options**:

```
/home/deck/.local/bin/savelocker run -- %command%
```

Replace `deck` with your username if it differs. **Use the full path.** Game Mode does not put
`~/.local/bin` on `PATH`, so the short form `savelocker run -- %command%` silently prevents the
game from launching — you get a black screen and the game never opens.

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
`http://localhost:5178`.

**"Headless" here means no tray icon and no pop-ups — not that there is no UI.** There is a full one,
and this is the easiest way to **map a save folder without typing a path**: under
**Settings → Currently Tracked Games**, a game with no folder shows **Set save path**, which opens a
folder browser. Drive it in Desktop Mode the usual way — right stick or trackpad moves the cursor,
left stick scrolls, click to open a folder. It starts on the path the scan guessed and is limited to
your home directory, Steam libraries and mounted SD cards. See [Adding games](#help/adding-games).

The UI listens on **localhost only, and that is deliberate**. It is a management interface — it can
re-point this machine at another server, re-register it, and change what it syncs — so putting it on
your network would let anything on that network do the same. There is no `--lan` flag any more.

To reach it from your PC or phone, forward the port over SSH, which authenticates you and encrypts
the traffic:

```sh
# from the other device
ssh -L 5178:localhost:5178 deck@<deck-ip>
# then browse to http://localhost:5178 on that device
```

On the Deck, SSH is off by default: `sudo systemctl enable --now sshd` (and set a password with
`passwd` first, since SteamOS ships without one).

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
