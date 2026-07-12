# Shipped — migrated out of Backlog (July 2026)

Backlog items that are now complete, moved here so `Backlog.md` holds only
not-yet-done work. Full technical detail lives in `logs/sessions.md`; this is the
quick "what shipped, where" index.

| Item | Shipped in | Verified | Detail |
|------|-----------|----------|--------|
| Agent version display fix (MinVer overrode version fields in-target → `MinVerVersionOverride` + read `FileVersion`) | v0.1.2 | ✅ device | sessions 2026-07-12 |
| Silent auto-relaunch after update (`skipifsilent` removed) | v0.1.2 | ✅ device | sessions 2026-07-12 |
| Uploaded installer persistence across Docker update (`Storage:AgentInstallerRoot`) | v0.1.2 | ✅ device | sessions 2026-07-12 |
| Fetch agent installer from GitHub Releases — manual dashboard button (`POST /api/admin/agent-installer/fetch-github`) | main, 2026-07-11 | API-verified | sessions 2026-07-11 (session 2) |
| Sync toaster reduction — one summary toast with save date instead of 4 | v0.1.3 | ✅ device | sessions 2026-07-12 |
| Per-game exclude globs + configurable upload cap (hygiene 5e) | v0.1.4 (agent); server on main | API-verified live; agent device check pending | sessions 2026-07-12 (session 2); `logs/002_glob_filters.md` |
| Console Help KB — dashboard Help tab, 8 static Markdown articles, full-text search, `#help/<slug>` deep-links, conflict card "Why did this happen?" link | main (`be54374`) | build-verified | 2026-07-11 |
| Scheduled GitHub installer auto-poll | main | server + dashboard builds | `AgentInstallerPollerService`, dashboard-configurable in Agent Updates; sessions 2026-07-12 |

## Dropped from backlog — won't do (2026-07-12)

- **SteamGridDB key in agent UI** — the agent tray UI displays no game cover art (only the app logo); art is fetched server-side and shown only in the web dashboard. No plan to add art to the agent UI, so there's nothing for the key to power there. Console/dashboard config is sufficient.
- **CloudFlare Access / remote-access hardening** — SaveLocker is a LAN-only self-hosted tool by design. Exposing it over the internet is the operator's responsibility (Tailscale or another secure tunnel); we won't build in remote-access hardening or work around Cloudflare Tunnel's file-size limit.
