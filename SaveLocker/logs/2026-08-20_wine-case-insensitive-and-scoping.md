# 2026-08-20 — Case-insensitive Wine/Proton paths shipped; multi-path and registry saves scoped

**Status: DONE** for case-insensitive matching (implemented, tested, committed). Multi-path saves and
registry-based saves are **scoped only, not built** — both turned out to be genuine schema/architecture
changes, not resolver tweaks, so building either is a maintainer scope decision, not something to just
do. Branch `claude/wine-proton-case-insensitive-paths-815ba3`, no task file (three related backlog
items requested directly: "implement case-insensitive path matching... as well as look into multiple
game paths... and also registry keys in saves").

## The report

All three items were already named in `Backlog.md` from the 2026-08-19 MoonDeck/Borderlands 2 session,
at very different levels of readiness: case-insensitive matching had a maintainer-authored fix shape
and named tie-break policy; multi-directory saves and registry-based saves were one-line acknowledged
gaps with no scoping at all. Asked to implement the first and "look into" the other two.

## Part 1 — Case-insensitive path matching (implemented)

### Investigation

`PathResolver.ResolveConcrete` expands a manifest template, trims at the first wildcard, joins the
kept segments into ONE string, and relies on the OS's own filesystem semantics for case handling —
NTFS is case-insensitive so this silently works on Windows, ext4 is case-sensitive so it silently
fails on Linux/Wine. `PathResolver`'s own string-comparison helpers (`PathComparison`, `Tokenize`,
`StartsWithKnownRoot`) turned out to be a red herring: they only ever compare a candidate path against
a token ROOT value (e.g. `<winDocuments>`'s resolved value), and the mismatch always happens in
manifest-supplied text *after* a token (`My Games` in `<winDocuments>/My Games/...`) — the root itself
is unaffected, so none of those needed to change.

Real-world case: Steam re-created a fresh, useless prefix on an SD card whose Wine session wrote
`My games` (lowercase g); the manifest and the original, working prefix in the main root both say
`My Games` (`logs/2026-08-19_moondeck-save-detection.md`). `savelocker resolve` against the correct
prefix worked immediately by hand — the resolver was never the problem, only that nothing tried the
wrong-cased segment.

### Fix

`src/Shared/PathResolver.cs` only. `PathResolver` gained a `_caseInsensitiveFilesystem` constructor
flag — `Wine()` (and therefore `Proton()`, which delegates to it) passes `true`, `Windows()` passes
`false` explicitly (NTFS never needs the fallback, but the flag documents intent for a reader rather
than relying on that being incidentally true). Verified there are exactly two callers of the
constructor in the whole repo — `Windows()` and `Wine()` themselves — so the added parameter is
invisible to every other caller.

`ResolveConcrete`, when the naive exact-case path doesn't exist and the flag is set, now calls a new
`ResolveCaseInsensitive` walk. The walk anchors at `LongestMatchingRoot` — the longest token value
that's an exact-case (`Ordinal`) prefix of the naive path — rather than the filesystem root: token
substitution is verbatim (`ResolveToDirectory`'s `expanded.Replace(token, value)` splices a token's
stored value byte-for-byte), so the naive path is *guaranteed* to contain some token's exact-case value
as a prefix whenever it's rooted in anything known at all. Anchoring there means correction never
touches the token-root portion of the path, which is what lets `StartsWithKnownRoot` run once, on the
naive path, before any correction is attempted, and never need re-checking on the corrected result — a
walk-from-filesystem-root design would have risked that check rejecting a real, correctly-resolved
path on the way back out.

From the anchor, the walk proceeds segment-by-segment through the manifest-authored remainder: try the
exact-case join first via `Directory.Exists` (fast path, zero behavior change for the common
already-correct case), and only on a miss fall back to a case-insensitive scan of the parent's real
directory listing (`MatchChild`, reusing the existing `SafeChildDirectories` helper). Zero matches
fails the whole walk (caller keeps the untouched naive path — same "no such directory" outcome as
today); multiple matches — a parent genuinely holding both `My games` and `My Games` as siblings, a
plausible outcome of the exact relocation bug that motivated this — picks the newest by
`Directory.GetLastWriteTimeUtc`.

**A design correction found while tracing the algorithm by hand, before running anything:** the first
draft had `MatchChild` prefer an exact-case candidate among its own matches before falling back to
newest-mtime. Tracing it through showed this branch is structurally unreachable — `MatchChild` is only
ever invoked *after* `Directory.Exists` on that exact `(parent, segment)` pair already returned false,
and `SafeChildDirectories` enumerates the same real directory `Directory.Exists` just queried, so no
exact-case sibling can ever appear among its candidates: if one existed, the exact-case check would
already have taken it. Removed the dead branch and documented why exact-case preference falls out of
the caller for free rather than needing its own check inside `MatchChild`. This also meant a
"both exact and wrong case as siblings" test fixture would have silently passed for the wrong reason
(the pre-existing, un-changed fast path would have resolved it, never touching any new code) — dropped
that fixture design in favor of one where NEITHER sibling matches exactly, which is the only shape that
actually exercises the tie-break.

The trailing segment of a file-shaped template (`<base>/Save.dat`-style — `ResolveConcrete` already
special-cases this: last segment has an extension and isn't a real directory → use its parent instead)
needed its own accommodation: its existence is never checked either way, only its corrected parent
matters, so it resolves as `MatchChild(current, segment) ?? current` rather than requiring a match.
Without this, a save file that has never been written yet (first run, nothing on disk) sitting under an
earlier mis-cased folder would wrongly fail the whole walk on "zero matches" for the filename, even
though the parent directory correction was real and findable.

`WinePrefix.cs`'s own exact-case checks (`pfx`/`drive_c`, the `steamuser` fallback in `UserIn`) were
checked and confirmed to be a non-issue: every segment they touch is a fixed Steam/Proton bootstrap
convention Valve's own tooling creates with deterministic casing, categorically different from a
*game's* own save-folder creation choosing arbitrary casing.

### Verification

Two new `tests/linux/` fixtures (`make-fixtures.py`, `manifest.yaml`, `run-linux-tests.sh`), placed
right after the existing "manifest tokens resolve inside the prefix" check since that's the same code
path:

```
mis-cased Wine folder resolves via the case-insensitive fallback
case tie-break: the newer sibling wins
case tie-break: the older sibling loses
```

Fixture paths need a literal space (`My games`/`MY GAMES`/`my games` — the exact real-world shape
under test), which broke the harness's `eval "$(python3 make-fixtures.py ...)"` KEY=VALUE reporting on
the first run (`No such file or directory` on the space-split remainder) — every other fixture path in
the file is deliberately built with no spaces for exactly this reason, per its own comment. Fixed with
`shlex.quote()` on the five new printed values rather than avoiding spaces, since the casing under test
cannot be spelled without one. The tie-break fixture backdates one sibling's mtime by an hour with
`os.utime()` (new to this file — nothing there used it before) rather than relying on two `os.makedirs`
calls landing in different filesystem-clock ticks, which is not guaranteed; `utime()` runs *after*
creating the nested game-subfolder, not before, since POSIX bumps a directory's own mtime again when an
entry is added to it — backwards, and the backdating would be silently overwritten.

`run-linux-tests.sh` **234 → 237, 235 passed.** The 2 failures (`no Decky: it says why` / `no Decky:
doctor says nothing applies`) are pre-existing and unrelated — confirmed by the arithmetic (237 total −
3 new checks = 234, matching the last recorded baseline exactly) and by the failing section being
Decky-plugin update detection, a subsystem this change never touches. Also confirmed by hand:
`agent resolve --config <cfg> --prefix <prefix> "Fake Miscased Game"` /
`"Fake Case Tiebreak Game"` against both new fixtures resolve to exactly the expected on-disk paths,
the same manual-verification method originally used to root-cause the Borderlands 2 bug.

Build/test environment note: the shared WSL box (`~/SaveLocker`, used across worktrees/sessions per
`Build and Run.md`) had a stale, uncommitted diff sitting in its working tree against
`LinuxGameScanner.cs`/`SteamRoots.cs`/`tests/linux/*` from a prior session's `AllKnownInstalledAppIds`
work — confirmed via `git log -S` that the same code is already committed and merged (`ea9c8ed`) into
what this branch already carries, so the stale copy was redundant, not at-risk work. Stashed (not
discarded) before syncing the clone to this branch, in case that reading is ever wrong.

## Parts 2 & 3 — Multi-path saves and registry-based saves (scoped, written into `Backlog.md`)

Full-codebase research (data model, DTOs, every layer from server schema to the React UI for Part 2;
existing registry-read patterns and Wine's own registry format for Part 3) found both are considerably
bigger than their one-line `Backlog.md` entries suggested — full write-ups are now in `Backlog.md`
itself rather than duplicated here. Headline findings:

- **Multi-path saves** would be the most structurally invasive schema migration in the project's
  12-migration history — `MachineSavePath`'s single-string-per-composite-key shape has "one path per
  machine per game" baked into the primary key itself, not just the column type, and ripples through
  wire DTOs, agent config, `SyncEngine`'s push/pull loop, ~7 scanner call sites that currently reduce a
  resolved list to `.FirstOrDefault()`, and `SaveArchive` (the hardest piece — no namespacing scheme
  exists for zipping multiple roots into one archive without their relative paths colliding, and the
  restore-side safety logic is all written in terms of one root). The design risk that matters most:
  `ManifestLoader.ResolveSaveDirectories`'s own doc comment already carries a cautionary tale
  (DRAGON QUEST III's two templates, one resolving to the real save folder and one to a sibling
  `Config` folder) showing the manifest format does not disambiguate "alternate locations, pick one"
  from "complementary locations, sync all of them" — a real answer to that ambiguity is needed before
  any of the rest matters.
- **Registry-based saves** splits into two asymmetric problems by host. Native Windows is a small,
  low-risk addition — `GameScanner.cs`'s existing `ReadRegistryString` pattern (used for finding
  Steam's install path) is directly reusable. Linux/Proton is hard: the agent is a native linux-x64
  process, never running under Wine itself, and needs to read a game's prefix registry without
  launching it — Wine transparently backs `Microsoft.Win32.Registry` calls with a prefix's
  `user.reg`/`system.reg`, but only from *inside* that specific prefix's own Wine process, which is not
  the position this agent is in. The realistic path is hand-parsing Wine's plain-text registry format
  directly, which does not exist anywhere in this codebase today (closest existing pattern:
  `SteamTextVdf.cs`'s hand-rolled tokenizer, structurally similar in spirit, wholly different grammar).
  `ManifestLoader`'s YAML DTO also has no `Registry` property at all — not even an unused one — so a
  manifest's `registry:` block is silently dropped during parse today, and `SaveArchive.cs` has no
  extensibility hook for a non-file archive member.

Both are now phased entries in `Backlog.md` (High priority section, matching the depth of the
"Native Linux save support" entry already there) rather than one-liners in Planned/future, since they
now carry enough scoping for a future session to act on without re-deriving this session's research —
not because either is scheduled to be built next.

## Notes

**Deliberately not attempted:** any code for Parts 2/3 — both need a maintainer decision (accept the
current single-path/files-only limitation, or commit to the schema work) before implementation makes
sense, matching how `Decisions.md`'s native-Linux scope call was left standing until the maintainer
asked directly to revisit it.
