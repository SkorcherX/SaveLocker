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

Note: the **scheduled/automatic** GitHub installer auto-poll is a follow-up to the
shipped manual button and remains open in `Backlog.md`.
