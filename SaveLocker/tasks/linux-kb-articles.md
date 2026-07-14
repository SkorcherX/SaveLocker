# Task: Help KB articles for the Linux agent (Steam Deck / Proton)

**Status:** ✅ **UNBLOCKED — this is the next action.** Seeded 2026-07-12 after Linux agent Phase 2.
It was gated on Phases 4–6, and **all six phases shipped 2026-07-14**
(`logs/2026-07-14_linux-agent.md`).

**Two answers below changed, exactly as this file warned they might** — read these before writing:

1. **§1 setup is now the ENROLLMENT flow**, not `set-server` + `register`. The console mints a
   single-use, 15-minute policy file; the user copies it over and runs
   `savelocker enroll --file <policy>`. **No API key is ever copied by hand.** It sets the server URL,
   trades the token for the machine's key, TOFU-pins the server, and pre-seeds the games. Keep
   `register` in the KB as the manual fallback, not the headline. (`trust` / `trust --accept` is worth
   a line: the agent *warns* — never blocks — if the server's TLS key changes, which happens on a
   routine certificate renewal.)
2. **§4's `conflicts.md` edit is now the opposite of what this file predicted.** Phase 5 landed agent
   health reporting, so a Deck conflict is **no longer silent**: the console shows a problem badge and
   per-machine health (online / offline / never-reported, agent version, last sync). Say that a Deck
   reports its failures to the console — not that they are invisible.

Also worth folding into Troubleshooting (§3): **`savelocker doctor` now names a save path that is
really a Wine prefix** ("that is a Wine PREFIX, not a save folder") instead of letting it fail later
as a baffling *"your save is too big"*.

## Why

The Deck agent is **headless by design** (`Decisions.md` §2): no tray, no toast, no settings
window. In Game Mode there is nothing to look at. **The console IS the Deck's UI** — so the Help
KB is not documentation-as-nicety here, it is the actual support surface. A Windows user who hits
a problem sees a balloon; a Deck user sees nothing at all and goes to the console.

## How the KB works (so this is mechanical, not archaeology)

- Articles are plain markdown in `web/src/help/*.md`, imported `?raw`.
- Register each one in `web/src/help/index.ts` — add an `import`, then an entry in `articles[]`
  with `slug`, `title`, `category`, `content`. Categories today: Syncing, Configuration,
  Maintenance, Reference, Troubleshooting. Search and deep-links come for free.
- `SaveLocker/CLI Reference.md` is a stub that points at `web/src/help/cli-reference.md` — the KB
  article is the source of truth. Follow that pattern; don't fork docs into the vault.
- Match the existing voice: short, second person, concrete commands, no marketing.

## Articles to write

### 1. `steam-deck-setup` (category: Configuration) — the big one
End-to-end, assuming Desktop Mode and zero prior knowledge.
- Download the tarball → `./SaveLocker/install.sh`. Installs to `~/.local/share/SaveLocker`,
  symlinks `~/.local/bin/savelocker`, enables a `systemd --user` unit. **Never `/usr`** — say why
  (SteamOS's rootfs is immutable and wiped on every system update), because users WILL ask.
- `savelocker set-server --url …` → `savelocker register --name "Steam Deck"` → `savelocker doctor`.
- Adding the launch wrapper to a game: Properties → Launch Options → `savelocker run -- %command%`.
- **The step everyone will miss:** a non-Steam shortcut needs *"Force the use of a specific Steam
  Play compatibility tool"* ticked, or Proton never creates a prefix and there is nothing to sync.
- Reaching the agent UI: it serves the same React UI on `:5178`. On a Deck you browse to it from
  another device on the LAN (`savelocker daemon --lan`) — there is no local browser in Game Mode.

### 2. `deck-supported-games` (category: Syncing) — set expectations before they're violated
The single most likely support question is *"why doesn't my Steam game sync?"* Answer it head-on:
- SaveLocker syncs **non-Steam games added to Steam as shortcuts**, run under Proton.
- Games **bought on Steam already have Steam Cloud.** We do not compete with it and deliberately
  do not sync them. This is a scoping decision, not a missing feature (`Decisions.md` §0).
- **Native Linux builds are out of scope** and must not be synced into a Windows install — the save
  formats differ. Say this plainly; it is the corruption case the scope avoids.
- Two save shapes both work: *in-prefix* (inside the Wine prefix) and *portable* (next to the .exe).
- **A save made on a Windows PC and a save made on the Deck under Proton are the same save.** This is
  now *proven*, not asserted: Phase 3's CI round-trip pushes a save from a Windows agent, pulls it
  with the Linux agent, byte-compares the tree, sends it back, and compares again — identical both
  ways, identical content hash. Worth stating plainly in this article, because "will my PC save work
  on my Deck?" is the second question every user asks. (Keep it one sentence; do not turn it into a
  changelog.)

### 3. `deck-troubleshooting` (category: Troubleshooting) — or fold into `troubleshooting.md`
Lead with **`savelocker doctor`**: it checks the entire chain (server, Steam roots, shortcuts,
AppIDs, prefixes, save dirs, permissions, lock probe) and is the first thing to ask any user to run.
Paste its output. Then the specific failures:
- *"No prefix found"* → the game has never been launched through Proton; launch it once.
- *"No tracked game matches this launch"* → the game has no `--appid`; the wrapper cannot match it.
- Game not in the Ludusavi manifest (**common** — most standalone builds are absent). The fix is
  `savelocker add-game --name … --dir <path>`, and on Linux this is the **primary** path, not a
  fallback. Do not present manual mapping as a failure state.
- Where the log lives: `~/.local/share/SaveLocker/agent.log` (**not** `%PROGRAMDATA%` — Linux state
  is XDG).
- `systemctl --user status savelocker` and the `loginctl enable-linger` caveat.

### 4. Updates to EXISTING articles — don't leave these Windows-only
- **`cli-reference.md`** — add the Linux commands: `run`, `daemon [--lan]`, `doctor`, `autostart`,
  and the new `add-game --appid / --prefix` options. This article is currently Windows-shaped.
  - ✅ Already added 2026-07-13: **`hash`** (`hash [game] | --dir <path> [--exclude a,b]`) — prints the
    content hash the server compares to decide changed / unchanged / conflict. Useful in the
    Troubleshooting article too: it is the direct way to answer "do these two machines actually hold
    the same save?", and it gives the same answer on Windows and on the Deck.
- **`save-in-use-safety.md`** — the settle gate behaves differently on Linux and users deserve to
  know: `FileShare` is not enforced by the Linux kernel, so open-file detection is done by walking
  `/proc/*/fd` instead. Where it cannot answer, the gate says so in the log and settles on the file
  fingerprint alone. Keep it one honest paragraph; do not oversell.
- **`conflicts.md`** — a Deck conflict is **silent**. Until agent health reporting lands
  (linux-agent Phase 5), the console is the only place a Deck conflict is visible. Say so.
- **`adding-games.md`** — mention `--dir` mapping is normal on Linux.

## Check before writing (nothing blocks this any more)

- ~~**Phase 4 (enrollment token)** changes the setup flow~~ — **it did.** See the status note at the
  top: §1 is the `enroll --file` flow now. Write it that way from the start.
- ~~**Phase 5** is what makes conflicts visible on a Deck~~ — **it landed.** The `conflicts.md` edit
  now says the console *does* surface a Deck's conflicts.
- **Phase 3 is no longer a blocker** (landed 2026-07-13). It unblocks the cross-OS claim in §2 above
  and adds `hash` to §4. It also fixed a bug worth knowing about while writing Troubleshooting: a
  server whose DB was written by a **Windows-hosted** server stored archive paths with backslashes,
  which the Docker (Linux) server could not resolve — the agent then said *"server has no saves yet"*
  while the console still showed a head. Fixed, and old rows still resolve. If a user reports that
  exact contradiction, it is this, and they need a server new enough to have the fix.
- **No Steam Deck is owned.** Every screenshot-level claim about Game Mode, the Launch Options UI
  and Desktop Mode is currently *from documentation, not observation*. Flag anything unverified
  rather than writing it confidently — or get it checked by a Deck owner first. **This is the one
  risk that did not go away**, and a support article that confidently describes a UI nobody has seen
  is worse than one that says "we have not verified this on hardware".
