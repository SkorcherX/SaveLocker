# Troubleshooting

## Agent shows "offline" or can't reach server

1. Verify the server is running: open `http://<server-ip>:5080/api/overview` in a browser. You should get a JSON response.
2. Check the agent config at `%PROGRAMDATA%\SaveLocker\config.json` — confirm `ServerUrl` matches your server address and port.
3. Check the agent log at `%PROGRAMDATA%\SaveLocker\agent.log` for connection errors.
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

After resolving a conflict in the dashboard, the pushing machine needs to **pull** the new head before it can push cleanly again. Trigger a Force Pull from the agent tray, or launch the game (which auto-pulls on launch).

## Agent update loop / stuck on old version

If an agent keeps downloading the installer but stays on the old version:
- Check that the hosted installer version in the dashboard (**Configuration → Agent Updates**) is actually newer than the agent's current version.
- Check the agent log for installer download/launch errors.
- Verify the installer is not blocked by antivirus (unsigned executables may be quarantined).

## Dashboard shows wrong data after a change

Click **↻ Refresh** in the nav bar to force an immediate reload. The dashboard auto-refreshes every 15 seconds, so stale data usually resolves on its own.

## Can't add a machine / API key rejected

- API keys are per-machine and are generated when a machine first registers with the server.
- If a machine's key is lost, re-register: delete the agent config at `%PROGRAMDATA%\SaveLocker\config.json` and restart the agent. It will register as a new machine.
- In the dashboard, you can revoke old machines under **Configuration → Machines**.

## Logs and diagnostics

| File | Contents |
|------|----------|
| `%PROGRAMDATA%\SaveLocker\agent.log` | Agent activity: syncs, errors, update checks |
| `%PROGRAMDATA%\SaveLocker\offline-queue.json` | Pending pushes queued while server was offline |
| Server logs | `docker logs savelocker` (or check your Docker host) |
