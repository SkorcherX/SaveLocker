# Task: Linux agent (Proton / Steam Deck)

Design decisions are **locked** in `Decisions.md → Linux agent (locked 2026-07-12)`.
Read that section first. Do not re-litigate Proton-only scope, the headless-daemon
choice, the launch-wrapper trigger, or the unsigned-policy call — they are settled.

**Execute ONE phase, verify it, then STOP and report.** Do not roll into the next
phase. Each phase is independently shippable and independently revertable.

---

## Phase 0 — Dev environment (do once, before Phase 1)

**Goal:** a Linux box that tells the truth.

1. Install WSL2 + Ubuntu. Enable systemd (`/etc/wsl.conf` → `[boot]\nsystemd=true`), then `wsl --shutdown` and reopen.
2. Install the .NET 9 SDK **inside WSL** (not the Windows SDK via `/mnt/c`).
3. Clone the repo into the **WSL ext4 home** (`~/SaveLocker`).

> **Critical:** never run Linux tests from `/mnt/c`. DrvFs breaks inotify, file
> permissions, case-sensitivity and locking semantics — you would be testing a
> fiction. The repo must live inside `~/` for any Linux verification to mean anything.

**Verify:** `dotnet build src/Shared/SaveLocker.Shared.csproj` succeeds inside WSL,
and `touch ~/x && ls ~/X` fails (proving you are on a case-sensitive filesystem).

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

**Goal:** a Deck can sync a Proton game, configured by hand.

1. `src/Agent.Linux/SaveLocker.Agent.Linux.csproj` — `net9.0`, `OutputType=Exe`,
   publishes self-contained (`-r linux-x64 --self-contained`; SteamOS has no .NET runtime).
   Binary name `savelocker`.
2. **`PathResolver.Proton(prefixRoot)`** — a second factory beside `PathResolver.Windows()`
   (the token-dictionary design already anticipates this). Tokens resolve *inside the
   prefix*: `<winAppData>` → `{prefix}/pfx/drive_c/users/steamuser/AppData/Roaming`, etc.
   On Proton the token map is **per-game**, not per-machine — this is the crux of the port.
3. **Launch wrapper:** `savelocker run -- %command%`. Reads `STEAM_COMPAT_DATA_PATH`
   (the exact prefix — no compatdata scanning) and `SteamAppId`; runs pre-launch pull,
   execs the child, waits for exit, runs the settle gate + push. Process polling stays a
   fallback for non-Steam launchers only.
4. **Steam library discovery:** reuse `SteamVdf` (pure parsing, ports for free) to read
   `libraryfolders.vdf`. Probe **multiple Steam roots** — native (`~/.steam/steam`) *and*
   Flatpak (`~/.var/app/com.valvesoftware.Steam/...`). Games on the **SD card** live under
   a different library root; missing this means Deck games silently do not resolve.
5. **`savelocker doctor`** — the single highest-value command on a headless device.
   Reports: server reachable? Steam roots found? libraries (incl. SD card)? prefixes
   discovered? games mapped and save dirs present? permissions OK? It answers "why isn't
   this working" with no UI to look at.
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
a fixture `compatdata/<appid>/pfx/drive_c/...` tree plus a script that writes save files
slowly and then exits, invoked with `STEAM_COMPAT_DATA_PATH`/`SteamAppId` set. The agent
never talks to Steam (it reads two env vars and supervises a child), so this exercises the
entire real code path. Assert: prefix resolved, pull-on-launch, settle gate waits out the
slow writer, push lands.

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
4. **Steam Cloud conflict.** Steam Cloud is on by default and will fight SaveLocker over the
   same game. `scan --no-cloud` exists; on the Deck this needs to be a loud warning.
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
