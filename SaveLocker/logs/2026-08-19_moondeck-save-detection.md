# 2026-08-19 — Four root causes of unreliable Linux save detection, found live on a real Deck

**Status: DONE** — four independent root causes, all fixed and verified on the real Deck. Branch
`claude/steam-deck-save-detection-68b6c5`, no task file (investigation requested live, in two
rounds — Cyberpunk first, then a named follow-up list once that fix was confirmed).

## The report

Round 1: some games "I'm sure have save paths" are detected unreliably, and some installed games —
Cyberpunk 2077 named specifically — are not picked up by `scan` at all.
<br>Round 2, once Cyberpunk was fixed: "there are also other games on steam and on heroic that i know
i launched and had saves for but they don't show up" — A Short Hike, Left 4 Dead 2, Borderlands 2,
Roadwarden named specifically, "and so much more."

## Investigation

Built and deployed a `0.5.10-test` tarball via `tests/testenv.ps1 build`/`up -Only deck`, beside the
real installed agent (state at `~/savelocker-test-state`, `~/.local/share/SaveLocker` never touched
— see [[Gotchas]] → *`tests/testenv.ps1`*).

`savelocker scan` against the real Deck: **194 candidates, ~16 resolved a save dir.** Every
`SteamShortcut` candidate for Cyberpunk 2077 came back `(save dir unknown)`. `savelocker doctor`
confirmed why: `compatdata/3833743458` (the shortcut's AppID) does not exist — the shortcut has
never created a Proton prefix.

Dumping the raw `shortcuts.vdf` bytes around the Cyberpunk entry showed its `Exe` is
`/home/deck/homebrew/plugins/moondeck/python/moondeckrun.sh` — not the game. This is a **MoonDeck**
(Decky game-streaming plugin) shortcut: it launches MoonDeck's own streaming client, which tells a
remote host to start the real game and streams the video back. `LaunchOptions` carries
`MOONDECK_STEAM_APP_ID=1091500` — Cyberpunk 2077's real Steam AppID.

Searching every `compatdata/*` prefix on the Deck for Cyberpunk-shaped folders found real save data
— autosaves as recent as 2026-08-10, nine days before this session — sitting **orphaned** under
`compatdata/1091500`: a Steam-Store install of Cyberpunk that has since been uninstalled (no
`appmanifest_*.acf` anywhere on the machine), left behind because Steam does not clean up compatdata
on uninstall.

**Root cause:** a MoonDeck shortcut's `Exe` launches MoonDeck's own script, never the game, so Steam
never creates a Proton prefix for the shortcut's own AppID. When the same title is (or was) ALSO
installed locally under Proton, its real save data lives under a completely different AppID that
nothing in discovery ever tried.

Scanning all 134 shortcuts on the Deck for `moondeckrun.sh` found **11 MoonDeck shortcuts**. Of the 9
naming a `MOONDECK_STEAM_APP_ID`, only Cyberpunk 2077's (`1091500`) is a real, small-integer Steam
AppID with an actual local prefix — the other 8 (Minit, Forza Horizon 2, Forza Horizon (MoonDeck),
HITMAN 3, Marvel's Guardians of the Galaxy, Animal Crossing: New Horizons, Metroid Prime 4, Moving
Out) carry 19-digit values that are not real AppIDs and correctly resolve nothing, before and after
the fix below.

> [!important] A second, independent bug found in passing
> Doctor's raw (undeduped) shortcut listing showed the SAME game name under TWO different AppIDs for
> six titles on this one Deck: Metal Gear, Minit, Moving Out, Animal Crossing: New Horizons, HITMAN 3,
> Waydroid — `shortcuts.vdf` genuinely holds duplicate entries, most plausibly from MoonDeck- or
> Steam-ROM-Manager-style tooling re-creating a shortcut instead of updating the one already there.
> `LinuxGameScanner`'s final dedupe (`GroupBy(NormalizeName).OrderByDescending(has-a-save-dir)
> .ThenBy(HasSteamCloud).First()`) already prefers whichever duplicate resolves a save dir when
> exactly one does — but when NEITHER resolves, which is the common case since most duplicates are
> stray leftovers, the pick falls to an unexplained insertion-order tie-break, and nothing told the
> user two shortcuts existed at all. This is the shape of "I'm sure it has a save path but detection
> isn't reliable": SaveLocker can be silently watching a dead prefix while a better duplicate sits
> unexamined, with no diagnostic surface saying so.

## Fix

- **`SteamShortcuts.cs`** — parse `MOONDECK_STEAM_APP_ID=<digits>` out of a shortcut's
  `LaunchOptions` (`SteamShortcut.MoonDeckAppId`). Deliberately not a full tokenizer: other MoonDeck
  values are single-quoted and may contain spaces
  (`MOONDECK_LINKED_DISPLAY='LG Electronics LG TV (1600x900 mm)'`); an AppID is always a bare digit
  run after the marker, so finding the marker and reading digits is sufficient and immune to how
  neighbouring values are quoted.
- **`LinuxGameScanner.SuggestSaveDirAsync`** — when a shortcut's own compatdata prefix does not exist
  (or does not resolve a save dir), and it names a MoonDeck AppID whose compatdata DOES exist, try
  resolving there too. `installDir` is deliberately `null`, NOT the shortcut's `StartDir` — that is
  MoonDeck's own script directory, not the game's, and passing it as `<base>` would resolve template
  paths into a directory that has nothing to do with the game.
- **`Doctor.cs`** — when a shortcut has no prefix, name the MoonDeck-derived one and say whether it
  resolves: a dead-end "launch it once through Steam" becomes "✓ but MoonDeck streams appid X, which
  DOES have a local prefix: Y". Also added a duplicate-AppID note for any shortcut name backed by more
  than one AppID — a **note**, not a `Problem`, since scan's dedupe often still lands on the right
  answer and failing doctor's exit code over a mere duplicate would report a working Deck as broken
  (same reasoning as the pre-existing Heroic split-prefix note).

## Verification

`tests/linux/run-linux-tests.sh`: two new fixture entries (`make-fixtures.py`, `manifest.yaml`) — a
MoonDeck shortcut whose own AppID has no prefix but whose `LaunchOptions` names one that does, plus a
stray dead duplicate of the same name with no `LaunchOptions` at all (the exact shape found on the
real Deck). Seven new checks, all green:

```
scan finds the MoonDeck shortcut
scan resolves save via MOONDECK_STEAM_APP_ID
the resolving MoonDeck row wins over its dead duplicate
only one MoonDeck Streamed Game row survives the dedupe
doctor points at the real local prefix MoonDeck streams
doctor names the actual prefix path
doctor notes the duplicate MoonDeck shortcut
```

**221/223** total. The two failures are pre-existing and unrelated (Decky "no plugin installed"
messaging in `plugin-update`/`doctor` — neither code path this change touches); flagged as a separate
follow-up rather than fixed here, to keep this change scoped to detection.

**On the real Deck** — rebuilt and redeployed the test build:
- `scan` now resolves Cyberpunk 2077 to
  `compatdata/1091500/pfx/drive_c/users/steamuser/Saved Games/CD Projekt Red/Cyberpunk 2077` — the
  real, previously-orphaned save folder, autosaves and all.
- `doctor` explains why, and confirms the other 8 MoonDeck shortcuts still correctly resolve nothing
  — their `MOONDECK_STEAM_APP_ID` is not a real local AppID, so nothing changed for them.
- Candidates resolving a save dir: 16 → 25.
- All six duplicate-shortcut-name notes appeared, matching what was found by hand: Metal Gear, Minit,
  Moving Out, Animal Crossing: New Horizons, HITMAN 3, Waydroid.

## Notes

> [!note] Streaming is not syncing
> Even with the save path found, SaveLocker's pre-launch/post-exit sync still cannot hook a
> MoonDeck-streamed session automatically — the actual gameplay runs on a different machine, and the
> Steam wrapper only ever sees the shortcut's OWN (dead) AppID when Steam launches it. What this fix
> buys is **discovery**: the user can now `add-game --dir` the real path for manual push/pull or the
> folder watcher, which matters most for a title also played locally sometimes, and for recovering
> what is sitting there right now before it is lost to retention or a wipe. Doctor's pre-existing
> "saves written now are NOT being backed up" mismatch warning is not a false positive for this
> shape — it is honestly true for as long as a session is actively streamed rather than run locally.

> [!warning] Deck IP had moved since CONTEXT.md was last written
> `tests/testenv.local.ps1` (gitignored, authoritative) has `deck@192.168.0.103` /
> `http://192.168.0.124:5080`; [[CONTEXT]]'s Deck-handoff section still said `192.168.68.67` from a
> different LAN. Corrected there — another instance of the vault-drift trap in [[Gotchas]] → *Vault
> hygiene*.

**Deliberately not done:** a smarter dedupe tie-break for the duplicate-shortcut-name case (e.g.
preferring whichever duplicate's compatdata folder merely exists, even with no save dir resolved
yet). Doctor's note is the safer, additive fix — it hands the user the fact rather than guessing,
matching the standing rule from `logs/2026-08-09_heroic-detection.md`: a wrong guess that looks more
convincing than the truth is worse than an admitted unknown. The note is also headless-only —
nothing in the agent-ui Add Games view or the Game Mode UI surfaces it yet, since neither reads
`doctor` output at all; if this becomes a common complaint, that is the next lever, not a cleverer
tie-break.

---

## Round 2: the rest of the installed-games list (A Short Hike, Left 4 Dead 2, Borderlands 2, Roadwarden)

With Cyberpunk fixed, the maintainer named four more games bought and played on Steam whose saves
`scan` still would not find, "and so much more" — and indeed the original scan had resolved only
**16 of 194** candidates. Two more, independent bugs in `ScanInstalledSteamGamesAsync` explain most
of that gap; the fourth named game turned out to have no bug behind it at all.

### Investigation

Pulled each game's real manifest entry off the Deck's own cached `~/.local/share/SaveLocker/manifest.yaml`
(the SAME file the real, already-installed agent has been using) rather than trusting memory of what
a save path "usually" is — this is exactly the class of guess `Decisions.md` already warns against.

**Left 4 Dead 2** — the manifest's Windows save path is not inside any Wine prefix at all:
`<root>/userdata/<storeUserId>/550/remote`, Steam's own Cloud sync folder. `compatdata/550` does not
exist anywhere on the Deck — the game has probably never been Proton-launched — but
`userdata/162214018/550/remote` does, with real synced save files. `ScanInstalledSteamGamesAsync`
gated its ENTIRE resolution attempt on `Directory.Exists(prefix)`, so a template needing no prefix at
all was never even tried.

**Borderlands 2** — its ACF is on the SD card (`/run/media/deck/SDCard/steamapps/appmanifest_49520.acf`),
but `compatdata/49520` in the MAIN root also exists, fully populated, real save history included:
`.../Documents/My Games/Borderlands 2/WillowGame/SaveData` — while the SD card's OWN
`compatdata/49520` (Steam re-created one there too) is real but useless: its equivalent folder is
named `My games`, lowercase g, not `My Games` — Linux is case-sensitive, so the manifest's exact-case
template matches nothing there. Confirmed with `savelocker resolve --prefix <main-root-prefix>
--install-dir <sdcard-installdir> "Borderlands 2"`, which succeeded immediately once pointed at the
right prefix by hand — proving the resolver itself was never the problem, only which prefix
`ScanInstalledSteamGamesAsync` was willing to look at.

**A Short Hike** — `compatdata/1055540` exists but `drive_c/users/steamuser/` is completely empty: no
Wine boot has ever populated it. The install directory
(`.../steamapps/common/A Short Hike/`) confirmed why —
`AShortHike.x86_64` and `UnityPlayer.so`, a **native Linux build**, no `.exe` anywhere. The game has
never run under Proton on this Deck at all, so there is genuinely nothing in any prefix to find.
Native-Linux builds are explicitly out of scope (`Decisions.md` §1) — this is not a bug, and no
orphaned save data turned up anywhere else on the machine either (searched every compatdata prefix
for `adamgryu`/`short hike` fragments, found nothing).

**Roadwarden** — `compatdata/1155970` does not exist anywhere on the Deck, and its manifest entry has
no Steam-Cloud-style fallback (only Wine-prefix-anchored paths, and its `cloud:` block names GOG, not
Steam). Genuinely never launched under Proton here either; nothing to recover.

> [!note] Two named games turned out fine, and that matters as much as the two that didn't
> Reporting only the bugs would overstate what was broken. A Short Hike and Roadwarden are both
> correctly, honestly unresolved — the first because it runs as a native build outside this feature's
> stated scope, the second because there is simply no local Proton save history for it right now.
> Neither is a case of SaveLocker silently getting the wrong answer; both are SaveLocker admitting it
> has nothing, which is the behaviour `Decisions.md` and `logs/2026-08-09_heroic-detection.md` both
> deliberately chose over a convincing guess.

### Fix

- **`<root>` is now built from the caller's verified Steam root directly**, never derived from
  whichever compatdata path happens to be tried
  (`Detection.ResolveProtonAsync`'s usual `SteamLayout.RootFromCompatData`, which reads it off the
  prefix path's own directory structure). `userdata` — Steam Cloud's sync folder — exists exactly
  once, at the true Steam client root, regardless of which library holds a game's files or its
  compatdata; deriving `<root>` from a secondary library's prefix path silently pointed it at a
  library with no `userdata` at all. `LinuxGameScanner` now calls
  `PathResolver.Proton(prefix, installPath, storeRoot: steamRoot)` directly through a small
  `ResolveInstalledAsync` helper instead of going through `ResolveProtonAsync`.
- **Resolution is attempted even when no prefix exists anywhere** — the `Directory.Exists(prefix)`
  gate is gone. `PathResolver.Proton` only builds strings; a prefix that does not exist just makes its
  own tokens fail their own `Directory.Exists` check further down in
  `ManifestLoader.ResolveSaveDirectories`, which is exactly what lets a `<root>`-anchored template
  (needing no prefix) still resolve.
- **A present-but-useless prefix now falls back to the main Steam root too, not only a missing one.**
  When the current library's own prefix resolves nothing, `ScanInstalledSteamGamesAsync` retries
  against `{steamRoot}/steamapps/compatdata/{appid}` if that is a genuinely different, existing
  location — covering both "no prefix in this library" and Borderlands 2's actual shape, "a prefix
  exists here but Steam re-created it fresh and it has nothing usable." Checking only the main root
  (not every library combinatorially) is deliberate: it is where Steam already treats compatdata as
  special-cased for a different reason (a non-Steam shortcut's prefix always lands there), so a
  relocated game's ORIGINAL location is disproportionately likely to be it, and this is the one shape
  actually observed on hardware.

### Verification

Two new fixture games mirroring the exact real shapes — a game whose current library has a
present-but-useless prefix (deliberately non-empty with an unrelated folder, so a fixture that only
checked `Directory.Exists` could not accidentally pass) while the main root has the real one, and a
game with no prefix anywhere whose only save path is a `<root>/userdata/<storeUserId>/<appid>/remote`
template. Four new checks, all green:

```
scan finds the relocated game
relocated game resolves via the main root's prefix
scan finds the cloud-only game
cloud-only game resolves with no prefix at all
```

Combined with round 1's seven: **`run-linux-tests.sh` 216 → 227 (225 pass; the same two
pre-existing, unrelated Decky-messaging failures from round 1, still untouched by this change).**

**On the real Deck** — rebuilt and redeployed the test build again:
- `scan` now resolves Left 4 Dead 2 to `userdata/162214018/550/remote` and Borderlands 2 to
  `compatdata/49520/pfx/drive_c/users/steamuser/Documents/My Games/Borderlands 2/WillowGame/SaveData`
  — both the real, previously-invisible save folders.
- A Short Hike and Roadwarden remain correctly unresolved, for the reasons above.
- Candidates resolving a save dir: **16 → 32**, roughly double, across the whole 193-candidate scan —
  most of the remaining ~34 unresolved `SteamInstalled` entries sampled (Hades, FTL, Little
  Nightmares) show the SAME empty-prefix shape as A Short Hike: likely native-Linux builds or
  genuinely never Proton-launched, not a fifth bug. Not individually confirmed for every remaining
  title — a reasonable stopping point once the pattern repeated three times running.

### Notes

The first attempt at this fix only fell back to the main root when the current library's prefix was
**missing** (`!Directory.Exists`), which is what the Borderlands 2 investigation exists to warn
against: that version passed a hand-built fixture that happened to also make the prefix missing
entirely, and would have shipped silently wrong for the real, present-but-empty-in-the-wrong-case
shape actually on the Deck. Caught only by building the fixture carefully enough to match hardware
exactly, then re-testing against real hardware anyway rather than trusting the green suite alone —
worth remembering next time a fallback's fixture is easier to write than the bug is to reproduce.

---

## Round 3: the tie-break bug round 1 introduced (fixed same day)

Asked directly, using Cyberpunk as the example again: MoonDeck creates a streaming shortcut, but the
same game can also be genuinely installed (Steam, or Epic/GOG through Heroic) — is that going to be
detected as duplicates, what should happen about it, and should the dashboard even know? Full design
answer (dashboard's role, same-platform vs cross-platform duplicates, why `add-game --dir` already
covers manual switching): `Backlog.md` → *One game, several real sources*. This section covers just
the one piece that shipped immediately, per the maintainer's own call to split it from the rest.

**The bug.** `LinuxGameScanner.ScanAsync`'s final dedupe ties on `HasSteamCloud`, and a
`SteamShortcut` candidate is *always* `HasSteamCloud: false` by construction — "prefer the copy Cloud
doesn't already cover" is the right call when the shortcut is a genuinely independent DRM-free build,
but a MoonDeck shortcut is a pointer to the SAME install, not an alternate one. Round 1's own fix is
what exposed this: before it, a MoonDeck shortcut never resolved a save dir at all, so the FIRST sort
key (has-a-save-dir) already picked a genuinely-installed duplicate correctly — by construction, not
by design. Once MoonDeck shortcuts resolve, a game that is BOTH genuinely installed with real Steam
Cloud AND has a MoonDeck shortcut pointing at it hits the Cloud tie, and the pointer's hardcoded
`false` wins — surfacing the pointer's own dead AppID as `SteamAppId` instead of the real install's.

**Fix.** `ScanAsync` now tracks, per shortcut candidate, whether it resolved through the MoonDeck
fallback (a DIFFERENT game's prefix) rather than its own — a local flag, not a new field on
`ScanCandidate`, since nothing downstream of the dedupe needs to know how a survivor was found, only
that it was. Sorted ahead of the `HasSteamCloud` tie-break, so a genuine local install always beats a
MoonDeck pointer to it.

**Verification.** New fixture (`Fake Dual Source Game`): genuinely installed with `cloud: steam:
true`, also pointed at by a MoonDeck shortcut naming its real AppID — the Cyberpunk shape directly.
`run-linux-tests.sh` 227 → **230 (228 pass; the same two pre-existing, unrelated Decky-messaging
failures, still untouched)**. Confirmed no regression on the real Deck: Cyberpunk, Left 4 Dead 2,
Borderlands 2, A Short Hike and Roadwarden all resolve identically to round 2 — this Deck has no live
dual-source case for any of them right now, so the check is that nothing else moved, not that the new
branch fired.

---

## Round 4: Cyberpunk again — an unmounted SD card, not a bug

Reported directly: Cyberpunk 2077 still shows under agent-ui's "Added to Steam" filter
(`ScanSource.SteamShortcut`) instead of "Steam" (`SteamInstalled`), and "cyberpunk is installed
through STEAM in my steam deck." Round 1 had already found no `appmanifest_*.acf` for it anywhere
reachable and concluded the Steam-Store copy had been uninstalled — worth re-checking rather than
standing on that conclusion, since real hardware changes between sessions.

**Investigation.** A fresh, exhaustive ACF sweep (every `appmanifest_*.acf` on the main root and every
currently-mounted card, by content, not just filename) still found no Cyberpunk anywhere reachable.
But `libraryfolders.vdf` names **four** configured libraries, not two: the main root, `SDCard`,
`WinSD`, `SD2` — and only `SDCard` is currently mounted (`/run/media/deck/` has exactly one entry).
Steam's own per-library `apps` block in that same file — which Steam persists in its own config, not
on the removable media, so it survives a card being absent — names `SD2`'s contents directly:

```
"3" { "path" "/run/media/deck/SD2" ... "apps" { ... "1091500" "91231172278" ... } }
```

AppID `1091500`, 91,231,172,278 bytes (≈91 GB) — Cyberpunk 2077's real Steam install, genuinely
present, on a card that simply isn't in the Deck right now. **The maintainer was completely right, and
so was the scanner**: no code, ours or Steam's own, can read a file off media that isn't physically
connected. `SteamRoots.LibraryPaths` already skips a missing library on purpose (its own doc comment
says so) — correct behavior, functioning exactly as designed. What was missing was ever SAYING so.

**Fix — diagnostics, not detection; there is nothing to detect from absent media.**
`SteamRoots.AllConfiguredLibraryPaths` returns every library `libraryfolders.vdf` names regardless of
whether it currently exists (mirroring `LibraryPaths`' own VDF-walking, minus the `Directory.Exists`
filter). `Doctor`'s Steam section now diffs that against what actually resolves and names each gap by
its real path — a plain note, not a `Problem`: an unmounted card is completely normal, and failing
doctor's exit code over it would report a working Deck as broken, same reasoning as every other note
this session.

**Verification.** New WSL fixture: a third `libraryfolders.vdf` entry pointing at a path deliberately
never created on disk. `run-linux-tests.sh` 230 → **232 (230 pass, same two pre-existing failures)**.
**On the real Deck**, `doctor` now reads exactly:
```
  ! library configured but not mounted right now: /run/media/deck/WinSD
      an SD card that isn't currently inserted — any game installed there is invisible to this scan until it is.
  ! library configured but not mounted right now: /run/media/deck/SD2
      an SD card that isn't currently inserted — any game installed there is invisible to this scan until it is.
```
— naming `SD2` specifically, the exact card the real fix requires: reinsert it, and every earlier fix
in this write-up (the MoonDeck fallback, the tie-break, the retry-on-empty-resolution logic) already
has what it needs to classify Cyberpunk correctly as `SteamInstalled` the moment its ACF is reachable
again. Not yet confirmed with the card actually inserted — the next real verification step, whenever
the maintainer has it at hand.

---

## Round 5: classifying Cyberpunk correctly without needing the card at all

Round 4 closed on "reinsert `SD2` to confirm." The maintainer's own follow-up question made that
unnecessary: **"doesn't Steam know there are other games installed but the SD of it is not
mounted?"** — pointing at exactly the fact Round 4 had already surfaced but not fully used: the save
data for Cyberpunk sits on internal storage, reachable right now, while only the *install* is on the
absent card. Round 4's own `apps`-block reading (`"1091500" "91231172278"` under `SD2`, read while
`SD2` was unmounted) is Steam's own persistent bookkeeping — it survives the card being absent
*because* it lives in `libraryfolders.vdf` on the always-reachable main root, not on the card itself.
Nothing about classifying Cyberpunk correctly actually needed `SD2` inserted; it needed that file read
one level deeper than Round 4 had gone.

**Why a resolved compatdata prefix alone was never going to be enough to justify this**, and had to
wait for a second signal: Steam does not clean up compatdata on uninstall (established fixing Round
1) — a real, resolvable prefix full of real save data is exactly as consistent with "genuinely
installed, unreachable right now" as it is with "was installed once, uninstalled since, orphaned data
left behind." The `SuggestSaveDirAsync` MoonDeck fallback from round 1 could find the save; it could
never have justified relabelling the source, because finding a save proves nothing about current
install state. The `apps` block is the one place that actually says which is true.

**Fix.** `SteamRoots.AllKnownInstalledAppIds` reads every library's `apps` block from
`libraryfolders.vdf` — `{ appid: size-on-disk }` — regardless of whether that library currently
exists, the same file `AllConfiguredLibraryPaths` already reads for the unmounted-library note above,
one level deeper. `LinuxGameScanner.ScanAsync` checks a MoonDeck shortcut's real AppID against it; when
present, the candidate is built as `SteamInstalled` rather than `SteamShortcut` throughout — real
`HasSteamCloud` from the manifest instead of the shortcut default of `false`, the real AppID instead
of the shortcut's synthetic one (what the launch wrapper needs to match a genuine LOCAL Steam+Proton
launch, the only kind it can ever hook), and `InstallDir: null` rather than MoonDeck's own script
directory, which would be actively misleading attached to a candidate now presented as genuinely
installed. `ViaMoonDeck` (round 3) still applies unchanged: if the real ACF ever becomes reachable too
(the library gets mounted), that direct read remains more authoritative and keeps winning the dedupe.

**Verification.** New fixture (`Fake Phantom Installed Game`): a library that does not exist on disk
at all, whose `libraryfolders.vdf` entry nonetheless carries an `apps` block naming it — no ACF
reachable anywhere, exactly Cyberpunk's shape. `run-linux-tests.sh` 232 → **234.**

> [!warning] The first run of the new fixture read 193 passed, 41 failed — not the code
> A leftover `dotnet … daemon --port 5187` from an earlier manual run this same session was still
> bound to the port a *different* test section's daemon needed, so every test depending on that
> section's daemon starting failed with an unrelated `AddressInUseException` crash, cascading across
> completely unconnected areas (update staging, the Decky plugin, launch options). Both of this
> round's own checks passed cleanly in that same "failing" run — the tell that pointed away from the
> new code before the crash log confirmed it. Killing the stray process and re-running read
> **232 pass, 2 fail** (this round's 2 new checks included, same two pre-existing unrelated Decky
> failures). Worth remembering: a mass of unrelated failures after a change is a reason to check the
> environment before the diff.

**On the real Deck** — no card swap needed. `scan` now reads:
```
Cyberpunk 2077  <SteamInstalled> [Steam Cloud] appid=1091500
    save: .../compatdata/1091500/pfx/drive_c/users/steamuser/Saved Games/CD Projekt Red/Cyberpunk 2077
```
`SteamInstalled`, real AppID, real Cloud flag, correct save path — with `SD2` still not in the Deck.

> [!note] A side effect worth knowing about, not a bug
> Cyberpunk now carries `[Steam Cloud]`, which means it is hidden from the default Add Games view —
> exactly like every other Cloud-covered installed game, and exactly the point of that filter
> (Decisions.md, Steam Cloud bullet). If it is not already tracked, it will look like it "disappeared"
> from Add Games rather than reclassified; the `--no-cloud` filter (or the agent-ui's own Cloud
> toggle) still shows it.
