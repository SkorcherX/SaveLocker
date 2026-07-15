# Troubleshooting

> **On a Steam Deck or any Linux machine, start with `savelocker doctor`.** It checks the whole chain in one shot — see the **Steam Deck / Linux** section below. The paths in this article are shown for Windows (`%PROGRAMDATA%\SaveLocker\`); on Linux the same files live under `~/.local/share/SaveLocker/`.

## Agent shows "offline" or can't reach server

1. Verify the server is running: open `http://<server-ip>:5080/api/overview` in a browser. You should get a JSON response.
2. Check the agent config (`%PROGRAMDATA%\SaveLocker\config.json`, or `~/.local/share/SaveLocker/config.json` on Linux) — confirm `ServerUrl` matches your server address and port.
3. Check the agent log (`…\SaveLocker\agent.log`, or `~/.local/share/SaveLocker/agent.log`) for connection errors. On Linux, `savelocker log` prints it.
4. Ensure the server port (default 5080) is accessible from the agent machine (firewall, VPN, etc.).

## Sync not triggering after game close

- After a game exits, the agent waits for its save folder to go quiet before backing it up — 10 seconds by default. A backup that seems slow is usually just this working as intended. See [Save-in-use safety](#help/save-in-use-safety).
- Make sure the game is launched through Steam so the agent's process watcher can detect the exit event.
- Check the agent log for "push" entries to confirm a push was attempted. If you see `still writing after 120s`, something in the save folder is being written continuously — add an [exclude pattern](#help/glob-patterns) for it.

## Upload rejected: "archive exceeds 200 MB"

The save archive is larger than the 200 MB limit. Common causes:
- Save path is pointing at the game install directory instead of the save folder.
- Large files (screenshots, caches, logs) are being included. Add [exclude patterns](#help/glob-patterns) to filter them out.

## Conflict won't clear after resolving

After resolving a conflict in the dashboard, the pushing machine needs to **pull** the new head before it can push cleanly again. Trigger a Force Pull from the agent tray (Windows) or run `savelocker pull <game> --force` (Linux), or launch the game (which auto-pulls on launch).

## Agent update loop / stuck on old version

If an agent keeps downloading the installer but stays on the old version:
- Check that the hosted installer version in the dashboard (**Configuration → Agent Updates**) is actually newer than the agent's current version.
- Check the agent log for installer download/launch errors.
- Verify the installer is not blocked by antivirus (unsigned executables may be quarantined).

## Steam Deck / Linux

**Run `savelocker doctor` first.** It walks the entire chain — server reachable, Steam roots found, `shortcuts.vdf` parsed, AppIDs matched, Proton prefixes located, save folders present and writable, nothing still holding a file open — and marks anything broken. On a headless machine it is your only diagnostic surface; paste its output when asking for help. Then the specific failures:

- **"No prefix found"** — the game has never been launched through Proton, so Steam hasn't created its Wine prefix yet. Launch the game once, then try again.
- **"No tracked game matches this launch"** — the game has no `--appid`, so the `savelocker run` wrapper can't match it to a tracked game. Re-add it with `savelocker add-game --name … --dir … --appid <id>`.
- **A non-Steam game isn't syncing at all** — its Steam entry must have **"Force the use of a specific Steam Play compatibility tool"** ticked (Properties → Compatibility). Without it Steam runs the game without Proton, no prefix is created, and there is nothing to sync. `doctor` reports the missing prefix.
- **Game not in the Ludusavi manifest** — common for standalone builds, and not a failure. Map the save folder yourself: `savelocker add-game --name … --dir <path>`. On Linux this is the normal path, not a fallback.
- **The daemon isn't running** — check it with `systemctl --user status savelocker`. If it stops when you log out, enable lingering: `sudo loginctl enable-linger $USER`.

## Dashboard shows wrong data after a change

Click **↻ Refresh** in the nav bar to force an immediate reload. The dashboard auto-refreshes every 15 seconds, so stale data usually resolves on its own.

## Can't add a machine / API key rejected

- API keys are per-machine and are generated when a machine first registers with the server.
- If a machine's key is lost, the clean fix is to **re-enroll**: mint a fresh enrollment file in the console and run `enroll --file …` again (redeeming an existing machine name rotates its key and brings it back as itself). Alternatively, delete the agent config (`%PROGRAMDATA%\SaveLocker\config.json`, or `~/.local/share/SaveLocker/config.json` on Linux) and restart the agent to register anew.
- In the dashboard, you can revoke old machines under **Configuration → Machines**.

## Logs and diagnostics

Windows state lives under `%PROGRAMDATA%\SaveLocker\`; Linux state lives under `~/.local/share/SaveLocker/` (XDG, not `%PROGRAMDATA%`).

| File | Contents |
|------|----------|
| `…\SaveLocker\agent.log` | Agent activity: syncs, errors, update checks. On Linux, `savelocker log` prints it |
| `…\SaveLocker\offline-queue.json` | Pending pushes queued while the server was offline |
| Server logs | `docker logs savelocker` (or check your Docker host) |
