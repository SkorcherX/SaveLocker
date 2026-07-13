# Task: Linux agent (Proton / Steam Deck)

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
