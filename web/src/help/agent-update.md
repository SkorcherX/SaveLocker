# Agent auto-update & fetching from GitHub

Both agents keep themselves current, from packages this server hosts. They go about it differently, because a Windows PC has a tray icon to ask you with and a Steam Deck does not:

- **Windows** — package `SaveLocker-Agent-Setup-x.y.z.exe`. The tray offers the update; it installs when you accept.
- **Linux / Steam Deck** — package `savelocker-x.y.z-linux-x64.tar.gz`. It installs itself at the next agent start, without asking. Nothing ever changes mid-session.

The server hosts a package **per platform**, and each agent asks for its own. Uploading a Windows installer does nothing for your Deck, and vice versa — Configuration → Agent updates has a row for each.

## How the Windows agent updates

It checks the server for a newer installer on startup and periodically while running. When one is available the tray offers it; accepting downloads the installer and runs it, and the agent restarts. You can decline, and decline permanently for a particular version.

## How the Linux / Steam Deck agent updates

It checks the server every few hours. When a newer version is hosted it downloads it, checks it against the checksum the server published, unpacks it, and **runs it once to confirm it starts and reports the version it should**. Only then is it kept — as a *staged* update.

The staged update is installed the **next time the agent starts**. Nothing is replaced underneath a running session, and a game in progress is never interrupted. You do not have to do anything.

### What "the next time the agent starts" actually means

Not a reboot specifically, and not restarting Steam. It means the **`savelocker.service` systemd `--user` unit** starting — that is where the swap runs, before the new agent comes up.

| Action | Installs a waiting update? |
|---|---|
| Restarting your Deck | **Yes**, always |
| `systemctl --user restart savelocker.service` | Yes |
| `savelocker update` | Yes — checks, downloads if needed, installs, restarts |
| Switching to Desktop mode and back | **Only if lingering is off** (the default). It is a log out and log in, so your user services stop and start with it. With `loginctl enable-linger` set they keep running, and nothing is swapped. |
| **Restart Steam** (in the power menu) | **No.** That restarts Steam's own processes; SaveLocker is not one of them. It is the natural thing to reach for and it does nothing here. |
| Sleep and wake | No |
| Closing a game | No |

The Game Mode screen (`savelocker ui`) tells you which of these applies to *your* device, because it can read whether lingering is on. `savelocker doctor` says the same thing.

If a game is running when the unit restarts, the update is **deliberately left waiting** — it goes in at the start after that. This is the one case where restarting appears to do nothing.

### The Decky plugin

If you have the [SaveLocker Decky plugin](#help/decky-plugin), its panel shows an **Install update now** button whenever a verified update is waiting, and does the restart for you without leaving Game Mode. It only appears for an update that is already downloaded — never for one that has merely been announced, because that one needs a network round trip that can fail.

### From a terminal

To take one immediately instead of waiting:

```sh
savelocker update
```

The previous version is kept until the new one has started successfully. If an update is installed and then fails to start, the agent puts the old version back by itself and reports it to this console — a Deck does not strand itself on a broken build.

**To turn automatic installing off**, set `"AutoUpdate": false` in the agent's `config.json` (`~/.local/share/SaveLocker/config.json`). It keeps *checking*, so the console and `savelocker doctor` still tell you when the machine is behind; it just stops preparing the update on its own.

## What you see in this console

A Deck has no tray and cannot pop up a message, so it reports to the dashboard instead — that is the only notice anyone gets. Under a machine's health you may see:

- **`update.staged`** — a newer version is downloaded and verified, and goes in at the next start.
- **`update.failed`** — a package could not be used (bad checksum, or it would not run). Nothing was replaced and the machine still works. It has, however, stopped updating, which is the sort of thing that otherwise goes unnoticed for months.
- **`update.rolled_back`** — a version was installed here, did not start, and the previous one was restored. The machine is fine. **That build should not be rolled out further until you know why.**

## Fetching the latest packages from GitHub

The dashboard can pull straight from the SaveLocker GitHub Releases:

1. Go to **Configuration → Agent updates**.
2. Click **Fetch from GitHub** on the row you want.
3. The server downloads that platform's asset from the latest Release and stores it.
4. Agents pick it up on their next check.

A Release that predates the Linux tarball has no asset for that row, and the server says so rather than serving something else.

## Automatic fetching

In **Configuration → Agent updates**, set **Automatic GitHub fetch** to an interval in hours. The server checks immediately when you enable or change the schedule, then repeats. Set `0` to disable. It applies within a minute; no Docker or JSON edit needed. It refreshes **both** platforms.

## Checking the current hosted versions

The Configuration tab shows the hosted version for each platform. A blank row means nothing has been uploaded for it, and those agents will not be offered an update.

## Keeping versions in sync

After updating the server, host a matching agent package for every platform you run. Version skew between agents is the most common cause of unexpected conflicts — see [Best practices for multiple machines](#help/multi-machine).

## Manual placement

If you would rather not use the GitHub button:

1. Download the package from the GitHub Releases page.
2. Copy it into `/data/agent-installer/` for Windows, or `/data/agent-installer/linux-x64/` for Linux, on your Docker host.
3. Restart the server container so it picks up the new file.

Each Release also publishes `SHA256SUMS-windows.txt` / `SHA256SUMS-linux.txt`. To check a download by hand:

```sh
sha256sum -c SHA256SUMS-linux.txt
```

The Linux tarball additionally carries a build attestation tying it to the workflow run that produced it, which — unlike a checksum — does not depend on trusting the page you read the checksum from:

```sh
gh attestation verify savelocker-<version>-linux-x64.tar.gz --repo SkorcherX/SaveLocker
```

## Installing a Deck by hand

Still supported, and it supersedes anything the agent had staged for itself:

```sh
tar -xzf savelocker-<version>-linux-x64.tar.gz
./SaveLocker/install.sh
```

`install.sh` installs over the top and **keeps your configuration** — enrollment, tracked games and the server pin all survive. Your saves are on the server, not in the agent, so there is nothing to migrate.
