# Task: Linux agent (Proton / Steam Deck)

> ## ▶ START HERE: **Phases 0–3 are DONE. Next is Phase 4** (enrollment token + policy import).
>
> **The runtime moved to .NET 10 after Phase 3** (`Decisions.md → Runtime: .NET 10 LTS`;
> `logs/2026-07-13_dotnet-10-upgrade.md`). The phase write-ups below still *say* `net9.0` — that is
> the historical record of when they were written, not the current target. **Everything is
> `net10.0` now** (`net10.0-windows` for the tray). The upgrade was deliberately sequenced *before*
> Phase 4 so Phases 4–6 get written on net10 rather than ported to it afterwards.
>
> Phase 4 adds server endpoints → per `CLAUDE.md`, **regenerate `web/src/api-types.ts` and commit the
> updated `src/Server/openapi.json`**.

Design decisions are **locked** in `Decisions.md → Linux agent (locked 2026-07-12)`.
Read that section first. Do not re-litigate Proton-only scope, the headless-daemon
choice, the launch-wrapper trigger, or the unsigned-policy call — they are settled.

**Execute ONE phase, verify it, then STOP and report.** Do not roll into the next
phase. Each phase is independently shippable and independently revertable.

---

## Phase 0 — Dev environment (do once, before Phase 1)

**Goal:** a Linux box that tells the truth.

1. Install WSL2 + **Ubuntu 24.04 LTS** (`wsl --install -d Ubuntu-24.04`). Not Arch — see
   `Decisions.md` §6: Ubuntu matches CI (`ubuntu-latest` *is* 24.04) and its glibc is
   *older* than SteamOS's, so self-contained builds stay forward-compatible with the Deck.
2. Enable systemd (`/etc/wsl.conf` → `[boot]\nsystemd=true`), then `wsl --shutdown` and reopen.
3. Install the .NET 9 SDK **inside WSL** — not the Windows SDK via `/mnt/c`.

   > **`apt install dotnet-sdk-9.0` DOES NOT WORK on Ubuntu 24.04.** .NET 9 shipped
   > *between* LTS releases, so it is absent from the 24.04 archive — apt offers only
   > `dotnet-sdk-8.0` and `dotnet-sdk-10.0`. Use Microsoft's install script (no root, and it
   > sidesteps the known packages.microsoft.com ↔ Ubuntu-archive conflict on 24.04):
   >
   > ```bash
   > curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
   > bash /tmp/dotnet-install.sh --channel 9.0 --install-dir "$HOME/.dotnet"
   > printf '\n# .NET SDK\nexport DOTNET_ROOT="$HOME/.dotnet"\nexport PATH="$PATH:$DOTNET_ROOT"\n' >> ~/.bashrc
   > ```
   >
   > Apt-managed alternative: `sudo add-apt-repository ppa:dotnet/backports`.
   > **Do not install `dotnet-sdk-10.0`** just because apt offers it — the solution targets
   > `net9.0` and EF Core is pinned to 9.0.x precisely to stay off net10.

4. Clone the repo into the **WSL ext4 home** (`~/SaveLocker`). Never `/mnt/c` or `/mnt/e`.

> **Critical:** never run Linux tests from `/mnt/c`. DrvFs breaks inotify, file
> permissions, case-sensitivity and locking semantics — you would be testing a
> fiction. The repo must live inside `~/` for any Linux verification to mean anything.

**Verify:** `dotnet build src/Shared/SaveLocker.Shared.csproj` succeeds inside WSL,
and `touch ~/x && ls ~/X` fails (proving you are on a case-sensitive filesystem).

### Status: ✅ DONE (2026-07-12)
Ubuntu 24.04.4 LTS on WSL2, systemd 255 up, .NET **9.0.315** in `~/.dotnet` (via the install
script — apt could not provide it), repo cloned to `~/SaveLocker` on **ext4** (`/dev/sde`,
case-sensitivity confirmed), `libicu` present, `SaveLocker.Shared` builds clean on Linux.
Windows host reachable from WSL at `172.26.32.1` (needed for the Phase 3 round-trip).
Not set: git identity inside the WSL clone — only needed if you intend to commit *from* Linux.

**STOP.**

---

## Phase 1 — Split out `Agent.Core` (pure refactor, no behaviour change)

**Goal:** the sync brain stops being Windows-only. Lowest-risk step; unblocks everything.

1. New project `src/Agent.Core/SaveLocker.Agent.Core.csproj`, `net9.0` (no `UseWindowsForms`).
2. **Move** into it, unchanged: `SyncEngine`, `SaveSettler`, `ApiClient`, `CommandPoller`,
   `OfflineQueue`, `OfflineQueueDrainer`, `AgentConfig`, `AgentApiServer`, `Detection`,
   `AgentLogger`. These are already platform-neutral (`AgentApiServer` is `HttpListener`,
   which runs on Linux).
3. **Leave** in `SaveLocker.Agent` (Windows): `TrayApp`, `AgentWindow`, `AutoStart`,
   `GameScanner`, `AppResources`, `Program`.
4. `SaveLocker.Agent` takes a ProjectReference on `Agent.Core`. Namespaces stay
   `SaveLocker.Agent.*` so no call sites churn.
5. Anything left in Core that touches the registry or WinForms → extract behind an
   interface (`IAutoStart`, `IGameScanner`) with the Windows impl injected from `TrayApp`.

**Verify:** `dotnet build --no-incremental` clean; `.\tests\run-agent-tests.ps1` still
**10/10** (server on :5179, fresh DB — see `Gotchas.md`). The Windows agent must behave
identically. No new features in this phase.

### Status: ✅ DONE (2026-07-12)
`src/Agent.Core/SaveLocker.Agent.Core.csproj` (net9.0) now holds `SyncEngine`, `SaveSettler`,
`ApiClient`, `CommandPoller`, `OfflineQueue`, `OfflineQueueDrainer`, `AgentConfig`,
`AgentApiServer`, `Detection`, `AgentLogger` and `UpdateChecker` — moved with `git mv`, namespace
unchanged (`SaveLocker.Agent.*`), so no call site churned. Windows keeps `TrayApp`, `AgentWindow`,
`AutoStart`, `GameScanner`, `AppResources`, `Program`, `Watchers`, `SteamVdf`/`SteamTextVdf`.

Three Windows leaks in the moved files, extracted behind interfaces in `Agent.Core/Platform.cs`:
- `AgentApiServer` called `AutoStart` statics (registry) → `IAutoStart`, injected from `TrayApp`.
  `AutoStart` went from a static class to `sealed class AutoStart : IAutoStart`.
- `AgentApiServer` hosted a WinForms `FolderBrowserDialog` → extracted to `Agent/FolderPicker.cs`
  and injected as `Func<Task<string?>>? pickFolder`. **Null on a headless platform** — the
  folder-pick routes then return no path, which is the correct Linux behaviour.
- `CommandPoller` constructed `GameScanner` directly → `IGameScanner`, injected.
- `ScanCandidate`/`ScanSource` moved out of `GameScanner.cs` into `Agent.Core/ScanCandidate.cs`
  (Core consumes the candidates; only discovery is platform-specific).
- `AgentApiServer`, `UpdateChecker` and `UpdateResult` went `internal` → `public` (cross-assembly).

**Verified:** `dotnet build --no-incremental` succeeds, 0 errors; `.\tests\run-agent-tests.ps1`
**10/10** against a fresh DB. The one warning (MSB3277 WindowsBase/WebView2) is **pre-existing** —
confirmed by building HEAD in a throwaway worktree, not introduced by the split.

**STOP.**

---

## Phase 2 — Linux daemon, CLI, Proton path resolution, launch wrapper

**Goal:** a Deck can sync a **non-Steam** Proton game, configured by hand.

> **Read `Decisions.md` §0 first.** The niche is **non-Steam shortcuts**, not Steam-store
> games (those already have Steam Cloud). That means discovery is **`shortcuts.vdf`**, and
> the `libraryfolders.vdf` / `*.acf` scan is **not** the path here.

1. `src/Agent.Linux/SaveLocker.Agent.Linux.csproj` — `net9.0`, `OutputType=Exe`,
   publishes self-contained (`-r linux-x64 --self-contained`; SteamOS has no .NET runtime).
   Binary name `savelocker`.
2. **`PathResolver.Proton(prefixRoot)`** — a second factory beside `PathResolver.Windows()`
   (the token-dictionary design already anticipates this). Tokens resolve *inside the
   prefix*: `<winAppData>` → `{prefix}/pfx/drive_c/users/steamuser/AppData/Roaming`, etc.
   On Proton the token map is **per-game**, not per-machine — this is the crux of the port.

   **Handle both save shapes.** A non-Steam Windows game under Proton either writes
   *in-prefix* (above) **or** writes **portably, next to its .exe** — very common for the
   standalone builds our users actually have. The portable case needs *no* prefix
   resolution: it is a plain Linux path on the native filesystem. Do not force everything
   through the prefix resolver.
3. **Launch wrapper:** `savelocker run -- %command%`. Reads `STEAM_COMPAT_DATA_PATH`
   (the exact prefix — no compatdata scanning) and `SteamAppId`; runs pre-launch pull,
   execs the child, waits for exit, runs the settle gate + push. Works for non-Steam
   shortcuts: they have a Launch Options field, and with "Force compatibility tool"
   enabled Proton exports the same env vars. Process polling stays a fallback for games
   launched **outside** Steam (in Game Mode everything goes through Steam, so this is rare).
4. **Discovery = `shortcuts.vdf`** (NOT `libraryfolders.vdf`). `GameScanner.ScanSteamShortcutsAsync`
   already parses it via `SteamVdf` (pure parsing — ports for free), but currently reads only
   `AppName` / `StartDir`. **Extend it to capture the shortcut's generated AppID**, which *is*
   the `compatdata/<appid>/` directory name.
   - **Trap:** Steam derives that AppID as a **signed** 32-bit value but names the `compatdata`
     folder with the **unsigned** form. Get this wrong and every prefix lookup silently misses.
   - Probe **multiple Steam roots** — native (`~/.steam/steam`) *and* Flatpak
     (`~/.var/app/com.valvesoftware.Steam/…`).
   - SD-card library roots are **not** a concern: non-Steam `compatdata` lands in the main
     Steam root.
   - The **Ludusavi manifest is much less useful here** — standalone builds are largely absent
     from it. Manual `--dir` mapping is the *primary* path on Linux, not the fallback.
5. **`savelocker doctor`** — the single highest-value command on a headless device.
   Reports: server reachable? Steam roots found (native + Flatpak)? shortcuts parsed, with
   AppIDs? prefixes resolved? games mapped and save dirs present? permissions OK? It answers
   "why isn't this working" with no UI to look at.
6. **`systemd --user` unit** installed to `~/.local/share/SaveLocker`. **Never `/usr`** —
   SteamOS's rootfs is immutable and wiped on update.

### Known Linux behaviour change — do not let this pass silently

`SaveSettler.FirstLockedFile` relies on `FileShare.Read` throwing when another process
holds a write handle. **That is a Windows kernel semantic. On Linux, `FileShare` is not
enforced** — the open simply succeeds, so the probe always returns `null` and the settle
gate degrades to **fingerprint-only**.

That is tolerable (the fingerprint is the load-bearing half, and the launch wrapper means
we *know* the process exited), but it must be deliberate:
- either implement the Linux probe by scanning `/proc/*/fd` for write handles into the
  save dir (this is what `lsof` does), **or**
- have the gate log explicitly that lock detection is unavailable on this platform.

Silently returning "nothing is locked" on every check is the one outcome that is not acceptable.

**Verify (no Steam, no GPU, no Deck required):** build the **fake-game harness** —
a fixture `compatdata/<appid>/pfx/drive_c/...` tree plus a fixture `shortcuts.vdf`, plus a
script that writes save files slowly and then exits, invoked with
`STEAM_COMPAT_DATA_PATH`/`SteamAppId` set. The agent never talks to Steam (it reads two env
vars and supervises a child), so this exercises the entire real code path. Assert: shortcut
+ AppID parsed, prefix resolved, pull-on-launch, settle gate waits out the slow writer, push
lands. Cover **both** save shapes — in-prefix *and* portable-next-to-exe.

### Status: ✅ DONE (2026-07-12)

`src/Agent.Linux/` → binary **`savelocker`**. All six items done, plus the settle-gate item.

- **`PathResolver.Proton(compatDataPath)`** (Shared) — Windows tokens resolve *inside* the prefix.
  Per-game, not per-machine, as anticipated.
- **`SteamShortcuts`** (Core) — one shortcuts.vdf reader for both hosts. `CompatDataId()` is the
  single place the **signed → unsigned AppID** trap is handled. The Windows `GameScanner` now uses
  it too, so the two cannot drift.
- **`LinuxGameScanner`** — discovery is `shortcuts.vdf` across native *and* Flatpak Steam roots
  (symlinks resolved so a root isn't counted twice). No `libraryfolders`/`*.acf` scan. Handles
  **both save shapes**: in-prefix (manifest + Proton resolver) and portable-next-to-the-exe
  (a plain Linux path; no prefix resolution). Only claims a portable dir when unambiguous.
- **`ProtonRun`** — `savelocker run -- %command%`. Reads the two env vars, pulls, supervises the
  child, then settles + pushes. **Returns the game's own exit code**, and a sync failure never
  blocks the game launching.
- **`Doctor`** — server, Steam roots, shortcuts, AppIDs, prefixes, save dirs, writability, lock
  probe. The only diagnostic a headless box has.
- **`Daemon` + `systemd --user`** — serves the same React agent UI on :5178 (`--lan` to reach it
  from another device, per §2). Installs to `~/.local/share/SaveLocker`; **never `/usr`**.

**Settle gate — handled, not silently degraded.** `FileLockProbe` now walks `/proc/*/fd` for write
descriptors into the save dir (what `lsof` does), reading the **octal** `flags` from `fdinfo`.
Where it cannot answer it returns `Unavailable`, which the gate **logs**; it never claims a false
all-clear. The harness pins this with a writer that writes once then holds the descriptor open in
silence — the fingerprint goes quiet immediately, so a broken probe settles in ~3 s where a working
one waits the full 8 s (observed: **12 s**).

**Two cross-cutting bugs surfaced and fixed** (neither was in this plan):
1. `AgentConfig.DefaultDir` used `CommonApplicationData` = **`/usr/share`** on Linux — unwritable,
   and wiped by SteamOS updates. Now XDG.
2. `Detection` hardcoded `PathResolver.Windows()`. The resolver is injected now, and on Linux
   resolves **nothing** rather than inventing host paths for a Windows game.

**Verified:** `tests/linux/run-linux-tests.sh` — **27/27** in WSL on ext4 (fake HOME, fresh server,
fixture `shortcuts.vdf` with a deliberately negative AppID). Windows agent still **10/10**.

**Deferred to Phase 5, as planned:** the daemon's "notify" is a log line. A headless spoke cannot
tell the user anything — that is health reporting, and faking it here would hide the gap.

**STOP.**

---

## Phase 3 — Cross-OS round-trip in CI

**Goal:** prove a Windows save and a Proton save are actually interchangeable.

1. Port `tests/run-agent-tests.ps1` to run on `ubuntu-latest` (PowerShell Core runs on
   Linux, or rewrite in bash). Note it was **unrunnable from the rename until 2026-07-12** —
   do not assume green means covered.
2. New CI job: one server, a **Windows agent and a Linux agent**, push from one → pull on
   the other → **byte-compare the tree**. This is the test that actually matters; everything
   else is a proxy for it.
3. Assert hashes match cross-OS. They should: relative paths are normalised to `/`, sorted
   Ordinal, and only content bytes are hashed. If they do not, stop and fix the hash —
   divergence here silently manufactures conflicts (see the two-agent gotcha in `CONTEXT.md`).

**Verify:** CI green on both runners; round-trip byte-identical.

### Status: ✅ DONE (2026-07-13)

**It found a real bug on its first honest run — see below. That is the whole point of the phase.**

1. **`tests/run-agent-tests.ps1` now runs on both OSes** (PowerShell Core runs on Linux). It picks the
   host binary per OS and is driven against `Agent.Linux` on `ubuntu-latest` in the new `agent-tests-linux`
   job. **10/10 on Windows, 10/10 on Linux.** The one check that *must* differ is explicit rather than
   skipped: on Windows the manifest's `<winAppData>` resolves against the host; on Linux it must resolve to
   **nothing** (Phase 2's deliberate refusal to invent a host path for a Windows game), so Linux asserts the
   refusal. A resolver that guessed there would silently sync the wrong directory.
2. **Cross-OS round-trip in CI** — `tests/cross-os/crossos.ps1`, three chained jobs. GitHub runners
   cannot share a network, so "one server, two agents" is realized by carrying **the server's own state**
   (SQLite DB + archive store — the entirety of what it knows) between jobs as an artifact and restarting
   the same server binary on top of it on the other OS:
   `crossos-author` (win, push) → `crossos-roundtrip` (linux, pull + compare + push) → `crossos-confirm`
   (win, pull + compare). Fixture is a torture tree: nested depth, spaces in file *and* directory names, a
   non-ASCII filename, CRLF vs LF, no-trailing-newline, and a 256-byte binary file with NULs.
3. **Hashes match cross-OS** — asserted two ways. Explicitly, via a new `hash` CLI command (`hash --dir`),
   recomputed on the *other* OS and compared to the head hash. And on the real code path: a pull stores the
   head hash (computed on the *other* OS) as `LastSyncedHash`, so a subsequent push reporting **"no local
   changes since last sync"** is the agent itself confirming the two OSes hashed the same bytes identically.
   Divergence would surface as a spurious upload or a phantom CONFLICT. Neither happened.

**Verified:** author 3/3, roundtrip 6/6, confirm 4/4 — **byte-identical in both directions**, Windows→Linux
(8 files) and Linux→Windows (9 files). Run for real across Windows and WSL-on-ext4, handing the artifact
across by hand exactly as CI does. Windows suite still 10/10; Phase 2 harness still 27/27.

#### 🐛 The bug this phase existed to catch: `ArchiveStore` persisted an OS-specific separator

`ArchiveStore.RelativePath` built the archive's store path with **`Path.Combine`** — and that string is
persisted in the DB as `SaveVersion.ArchivePath`. A **Windows-hosted** server therefore wrote
`gameid\versionid.zip`. On Linux a backslash is a legal *filename character*, not a separator, so
`_store.Exists()` missed, `DownloadVersionAsync` returned null, the endpoint 404'd — and the agent
reported the very convincing lie **"server has no saves yet; nothing to pull"** while `status` happily
showed a head. Silent, and indistinguishable from data loss.

Production was accidentally safe (the server only ever runs in Docker/Linux, so it is self-consistent),
but **the documented dev workflow runs the server on Windows** — so any DB or backup moved from a
Windows-hosted server to the Docker one has unreachable archives. Fixed: the stored path is now always
`/`-separated (never `Path.Combine`), and `FullPath` accepts either separator so rows written by an older
Windows-hosted server still resolve. The hash was never wrong; the *path* was.

**Two harness bugs also fixed, both of the "green means nothing" kind:**
- The server-readiness probe polled `/api/games`, which is an **agent** route: it answers 401 without an
  API key, so the loop never saw success and silently burned its full 60 s timeout on every run. It is
  `/api/admin/status` (the only unauthenticated route) now — in `tests/linux/run-linux-tests.sh` too, where
  this had been costing a minute per run.
- `crossos.ps1` leaked its server process when the readiness probe timed out (the handle had not reached
  the caller's `finally`), so the next run silently talked to a **stale server holding stale state** and the
  resulting conflict looked like a cross-OS hash bug. It now kills what it spawned, refuses to start if the
  port is already in use, and refuses to proceed if it cannot clear the state dir.

**STOP.**

---

## Phase 4 — Enrollment token + policy import

Per `Decisions.md`: console mints a **single-use, ~15-min enrollment token** (never a raw
API key in a file), agent redeems it for its machine key. `savelocker enroll --file <policy>`.
Policy carries server URL + preselected games / exclude globs / settle delay.
**No signing** — see the decision for why it would be theatre. TOFU-pin the server after
enrollment and warn if its identity changes.

**STOP.**

---

## Phase 5 — Agent health reporting (ships WITH Linux, not after)

A headless spoke **cannot tell the user anything** — a conflict that toasts on Windows is
silent on a Deck. Add an agent→server health/error report so the console can surface
"Steam Deck: conflict on Hades, 2 days ago". Without this, failures on the Deck are invisible
and the whole feature feels broken.

**STOP.**

---

## Phase 6 — Hardening

1. **Symlink escape (real bug, affects Windows too).** `Directory.EnumerateFiles(...,
   AllDirectories)` **follows symlinks**, and a Wine prefix is full of them. A save folder
   containing a symlink to `/etc` or `$HOME` gets sucked into the archive. Do not follow
   symlinks when archiving; do not restore them. (Junctions are the Windows equivalent.)
2. **Prefix-root sanity check.** A mis-set save path inside a prefix archives the *entire*
   Wine prefix — gigabytes. The 200 MB cap catches it, but the error is baffling; `doctor`
   should detect and name it.
3. **Zip-slip:** `ZipFile.ExtractToDirectory` already rejects entries outside the target
   (verified) — keep it that way; do not hand-roll extraction.
4. **Steam Cloud conflict — mostly moot on Linux.** Non-Steam shortcuts have no Cloud, and
   Steam-store games (which do) are explicitly not our niche. Keep `scan --no-cloud`, but
   this is not a Deck hardening item; it only matters if a user enrols a Steam-store game,
   where the right answer is to tell them Steam Cloud already handles it.
5. **Suspend/resume.** The Deck suspends constantly, mid-sync. The offline queue covers the
   network drop; ensure the settle gate's max-wait does not count suspended time as elapsed.

**STOP.**

---

## Out of scope (deliberately)

- **Native Linux game builds.** Needs a save-variant model on the server. Proton games are
  Windows games — that is the whole reason v1 needs no schema change.
- **Flatpak packaging of the agent.** The sandbox fights `~/.steam` + `compatdata` access.
  Tarball + `install.sh` + `systemd --user` first.
- **gamescope / Game Mode UI.** There is none, by design. The console is the Deck's UI.

## Deferred risk: no hardware

**No Steam Deck is owned.** WSL2 + the fake-game harness cover everything except gamescope,
the immutable rootfs, SD-card paths and suspend/resume. Before shipping to real users,
either validate on a borrowed/used Deck or on Bazzite (the practical SteamOS stand-in), or
recruit a Deck-owning beta tester. Track this the same way as the Windows device-verify
items — **built ≠ verified**.
