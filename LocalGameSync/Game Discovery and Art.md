# Game Discovery & Art (design)

Back to [[Home]] · part of [[UX Roadmap]]. Goal: let users **pick games from a
scanned list** instead of typing `add-game --dir …`, and show **cover art** in
the dashboard. Inspired by SteamGrid (boppreh) + SteamGridDB.

## Where scanning must run
Discovery reads the **local filesystem / registry**, so it runs in the **agent**
(the server can't see a client's disk). Two delivery options:
- **A (recommended first):** agent shows discovered candidates in its own
  settings window; user ticks games to enroll.
- **B (later):** agent reports candidates to the server; the **dashboard** lists
  them and the user enrolls from there. Requires an **agent command channel**
  (agent polls the server for actions). Bigger lift — see [[UX Roadmap]] stretch.

## Scan sources (agent side)
1. **Steam non-Steam shortcuts** — the user's idea. Add non-cloud games to Steam,
   then read the binary `…\Steam\userdata\<userId>\config\shortcuts.vdf`:
   - Fields per entry: `AppName`, `Exe`, `StartDir`, `LaunchOptions`, `appid`.
   - Non-Steam appid = `CRC32('"{Exe}"{AppName}') | 0x80000000` (legacy art id).
   - Find Steam via registry `HKCU\Software\Valve\Steam\SteamPath`
     (or `HKLM\...\WOW6432Node\Valve\Steam\InstallPath`).
   - `StartDir`/`Exe` give the install location → seed for finding the save dir.
   - Need a small **binary VDF reader** in C# (no good maintained NuGet; write one
     — the format is simple, see refs).
2. **Installed Steam games** (optional, mostly already have cloud sync — show but
   flag as "has Steam Cloud"): `steamapps\libraryfolders.vdf` → library paths →
   `appmanifest_*.acf` (`name`, `installdir`, `appid`).
3. **Common save roots heuristic:** enumerate folders under `%APPDATA%`,
   `%LOCALAPPDATA%`, `LocalLow`, `Documents\My Games`, `%USERPROFILE%\Saved Games`
   and match names against the Ludusavi manifest.
4. **Ludusavi manifest** ([[Architecture]] `ManifestLoader`): for any candidate
   name, resolve concrete save dir(s). This is how a scanned name → save folder.

Output of a scan: candidates `{ name, suggestedSaveDir, source, hasSteamCloud }`
that the user selects → enroll (server `Game` + local `TrackedGame`).

## Artwork via SteamGridDB
- **Base URL:** `https://www.steamgriddb.com/api/v2`
- **Auth:** header `Authorization: Bearer <API_KEY>` (free key from the user's
  SteamGridDB account → preferences). Store server-side in config
  (`SteamGridDb:ApiKey` / env `SteamGridDb__ApiKey`).
- **Flow per game:**
  1. `GET /search/autocomplete/{name}` → first `id` (the SteamGridDB game id).
  2. `GET /grids/game/{id}` (cover/grid), `/heroes/game/{id}`, `/logos/game/{id}`,
     `/icons/game/{id}` → responses contain **direct asset URLs**.
  3. Pick the top/preferred asset; store the URL on the game (or download + cache
     under `wwwroot/art/` or the archive store).
- Can also look up by Steam appid: `/grids/steam/{appid}` — useful when we have the
  non-Steam shortcut appid, but name autocomplete is simpler and platform-agnostic.
- **Be polite:** cache results; only fetch on enroll or a manual "refresh art".
- **Server changes:** add art fields to `Game` (e.g. `GridUrl`, `HeroUrl`,
  `LogoUrl`, `IconUrl`) + an endpoint to (re)fetch art for a game. Dashboard
  renders them on each game card.

## References
- SteamGrid (boppreh): https://github.com/boppreh/steamgrid
- SteamGridDB API: https://www.steamgriddb.com/api/v2 ·
  node wrapper https://github.com/SteamGridDB/node-steamgriddb ·
  rust wrapper https://github.com/PhilipK/steamgriddb_api
- shortcuts.vdf format: https://developer.valvesoftware.com/wiki/Steam_Library_Shortcuts ·
  parser reference https://github.com/Hafas/node-steam-shortcuts
