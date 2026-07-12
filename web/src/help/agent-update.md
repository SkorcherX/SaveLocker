# Agent auto-update & fetching from GitHub

## How agent updates work

The SaveLocker agent checks the server for a newer installer on startup and periodically while running. If a newer version is available, it downloads the installer silently and relaunches itself. No user interaction is required.

The server hosts the agent installer at `/api/agent/installer`. The version it serves is set by uploading a new installer — either manually or via the GitHub fetch button.

## Fetching the latest installer from GitHub

The dashboard can pull the latest installer directly from the SaveLocker GitHub Releases:

1. Go to **Configuration → Agent Updates**.
2. Click **Fetch latest from GitHub**.
3. The server downloads the installer from the latest GitHub Release and stores it locally.
4. Agents will pick up the new version on their next update check (within ~30 minutes, or on restart).

This is useful when you've deployed a new server version via Docker but haven't manually placed a new installer in `/data/agent-installer/`.

## Automatic fetching

In **Configuration → Agent Updates**, set **Automatic GitHub fetch** to an interval in hours. The server checks immediately when you enable or change the schedule, then repeats at that interval. Set it to `0` to disable automatic fetching. The change is stored by the server and applies within one minute; no Docker or JSON configuration edit is required.

## Checking the current hosted version

The Configuration tab shows the **currently hosted installer version**. If it's blank, no installer has been uploaded yet and auto-update is effectively disabled.

## Keeping versions in sync

After updating the server (e.g. pulling a new Docker image), always fetch a matching agent installer so all machines stay on the same version. Version skew between agents is the most common cause of unexpected conflicts — see [Best practices for multiple machines](#help/multi-machine).

## Manual installer placement

If you prefer not to use the GitHub fetch button, you can place the installer manually:

1. Download `SaveLocker-Agent-Setup-{version}.exe` from the GitHub Releases page.
2. Copy it into `/data/agent-installer/` on your Docker host.
3. Restart the server container so it picks up the new file.
