# Decisions

Settled choices. Don't re-litigate without a reason.

## Build philosophy — Hybrid
Reuse the open-source **Ludusavi manifest** (community DB mapping thousands of games → save locations, from PCGamingWiki) for detection. Build our own agent + server + dashboard for orchestration, leasing, and conflict handling. Do **not** re-map save locations ourselves.

## Conflict prevention — Proactive lock/lease
Server tracks an active "checkout" per game (like Steam Cloud's "in use"). Agent pulls latest before launch; the other machine is warned if saves are leased elsewhere. Content-hash + parent-version lineage is the fallback detector.

## Tech stack — Single-language .NET  ⚠️ *version superseded by "Runtime: .NET 10 LTS" below*
- Agent in C#/WinForms: best Windows integration (FileSystemWatcher, process watch, tray, single-file exe).
- Server in ASP.NET Core, runs in Docker on unRAID. One language end-to-end.
- The single-language, WinForms and ASP.NET Core choices all stand. Only the **framework version**
  moved: .NET 9 → .NET 10.

## Runtime: .NET 10 LTS (locked 2026-07-13)
**Supersedes the version half of "Single-language .NET 9".** Execution plan: `tasks/dotnet-10-upgrade.md`.

- **.NET 9 is STS and goes out of support 10 Nov 2026** — it is already in its maintenance phase
  (security fixes only). This is a deadline, not a preference.
- **.NET 10 is LTS** (supported to 14 Nov 2028) — three years, instead of another 18-month STS
  treadmill. Prefer LTS-to-LTS from here.
- **It dissolves the EF Core pin.** The rule "pin EF Core to 9.0.x; 10.x requires net10" existed
  *because* we were on net9. The upgrade removes the reason, so the rule goes with it — do not leave
  it behind to tell a future session not to do the thing that was just done.
- **Timing: before Linux agent Phase 4, not after.** The safety net peaked the moment Phase 3 landed
  — Windows 10/10, Linux 10/10, harness 27/27, and a cross-OS byte-compare in CI. That is exactly the
  apparatus needed to catch a framework swap going wrong. Upgrading later means doing it across a
  larger surface *and* porting fresh Phase 4/5 code onto net10 afterwards.
- **Its own branch, its own PR.** Never mixed with feature work: if something breaks, the whole value
  of the timing is that the cause is unambiguous.
- **A `global.json` pins the SDK.** CI was silently building the net9 targets with **SDK 10.0.301**
  (windows-latest preinstalls it; `dotnet build` takes the newest SDK unless pinned) while the dev box
  used 9.0.315. Dev, CI and Docker must agree on the toolchain — and the pin must be satisfiable by
  all three, including the `mcr.microsoft.com/dotnet/sdk` image.

## unRAID as hub (vs peer-to-peer)
- Asynchronous decoupling: PC pushes; laptop pulls later even if PC is off.
- Single source of truth → trivial "who synced last" + conflict resolution.
- Versioned history/rollback in one place.
- Already always-on, has storage, Docker, internet-reachable via **CloudFlare Tunnel**.
- Rejected raw Syncthing: continuous sync risks copying mid-write; conflict files messy for binary saves.

## UX phase decisions (locked 2026-06-22)
1. **Dashboard auth:** real admin auth shipped (2026-06-25) — `AdminPasswordFilter` + PBKDF2-SHA256, set from ConfigView. CloudFlare Access + Google deferred; blocked by Cloudflare Tunnel's 100 MB file limit (conflicts with large save archives).
2. **Enrollment model:** a game is defined **once on the server** (via the dashboard); each agent **maps its own local save dir**. Scanners suggest candidates; the server game is the single definition.
3. **"Latest" nomenclature:** the authoritative version agents pull is called **"Latest"** in the UI — this is the server **head** pointer. The dashboard labels it "Latest"; the admin action is **"Set as Latest"**.
4. **Artwork:** **download/cache** SteamGridDB images on the server (offline-safe, survives upstream art changes) rather than storing only URLs.

## Product name: SaveLocker (locked 2026-06-22)
The official product/brand name is **SaveLocker**. Rename is complete (2026-07-10):
- **User-visible:** config dir `%PROGRAMDATA%\SaveLocker`, single-instance mutex `"SaveLocker.Agent"`, registry Run-key `"SaveLocker"`, installer AppName/publisher, wizard images, health check, tray/window/balloon text, log paths, DB path `savelocker.db` (with rename shim for existing installs on `localgamesync.db`).
- **Code identifiers:** namespaces (`SaveLocker.*`), solution (`SaveLocker.sln`), project files (`SaveLocker.Agent/Server/Shared.csproj`) — all renamed 2026-07-10.
- **Note for existing Docker deployments:** the server DB at `/data/localgamesync.db` needs to be renamed to `/data/savelocker.db` (or override `Storage__DbPath`). The rename shim handles this automatically on the agent side.

## Agent installer (locked 2026-06-22)
- **Tooling: Inno Setup 6** (over WiX/MSI and MSIX). Free, full control over registry cleanup + uninstaller. MSIX rejected — its virtualisation would interfere with the agent reading the Steam registry + arbitrary save folders.
- **Script:** `installer/SaveLocker.iss`. Build via `.\installer\build-installer.ps1`. Output: `installer/dist/SaveLocker-Agent-Setup-{version}.exe`.
- **Machine-wide install** to `C:\Program Files\SaveLocker Agent`, `PrivilegesRequired=admin` (UAC up front).
- **Why an installer:** auto-start writes a registry entry; a manually-deleted exe would orphan it. The uninstaller must own and revert every system change.
- **Uninstall:** prompts before deleting `%PROGRAMDATA%\SaveLocker` (API key + tracked games config); *No* preserves it for a reinstall.

## Linux agent (locked 2026-07-12)

Decisions taken before writing any Linux code. All six phases shipped 2026-07-12 → 2026-07-14; the
execution plan and its outcomes are archived at `logs/2026-07-14_linux-agent.md`.

### 0. The niche is NON-Steam games run under Proton
Games **bought on Steam already have Steam Cloud** — SaveLocker adds nothing there and should not compete with it. The problem we solve on Linux is the one Steam does not: **non-Steam games added to Steam as shortcuts** (standalone / itch / GOG / DRM-free builds — exactly the "Environment facts" user profile below), launched through Proton.

This is the load-bearing scoping fact, and it shapes everything downstream:

- **Discovery is `shortcuts.vdf`, not `libraryfolders.vdf`.** The `*.acf` / library-root scan (`GameScanner` Source 2) finds *installed Steam games* and is irrelevant here. `GameScanner` already parses `shortcuts.vdf` (Source 1, binary VDF) — but it currently reads only `AppName` / `StartDir` and **must also capture the shortcut's generated AppID**, because that AppID *is* the `compatdata/<appid>/` directory name.
  - **Trap:** Steam derives that AppID as a **signed** 32-bit value but names the `compatdata` folder with the **unsigned** form. Get this wrong and every prefix lookup silently misses.
- **Two save shapes, and the simpler one is probably the common one.** A non-Steam Windows game under Proton either writes *into* the prefix (`drive_c/users/steamuser/AppData/…`), **or** writes **portably, next to its .exe** — which is very common for standalone builds. The portable case never touches the prefix: it is a plain Linux path on the native filesystem, needing no prefix resolution at all.
- **The Ludusavi manifest is much less useful here.** Standalone builds are largely absent from it. On Linux, **manual `--dir` mapping is the primary path**, not the fallback.
- **Steam Cloud contention is a non-issue.** Non-Steam shortcuts have no Cloud. Likewise SD-card library roots — non-Steam `compatdata` lands in the main Steam root.

The launch wrapper (decision 3) still applies: non-Steam shortcuts have a Launch Options field, `%command%` works, and with "Force compatibility tool" enabled Proton still exports `STEAM_COMPAT_DATA_PATH`.

### 1. Proton-only for v1 — native Linux builds are out of scope
A Proton game **is a Windows game**: it writes Windows-format saves to Windows paths inside a Wine prefix. A Deck and a Windows PC therefore produce **byte-identical saves**, and the existing content-hash lineage works across them with no conversion, no format translation, no line-ending handling.

All the genuinely hard cross-OS problems (different save formats, different paths, text-mode line endings, case collisions) appear **only** with native Linux builds of a game. Excluding them means v1 needs **zero server schema change** — and Proton *is* the Steam Deck / Steam Machine use case.

Native Linux builds need a save-*variant* model on the server (a version's lineage would only be valid within a platform family). Deferred until there is a reason to build it. **Do not sync a native-Linux save into a Windows install** — that is the corruption case this scoping avoids.

### 2. No native UI on Linux — the daemon serves the existing React UI
In **Game Mode** (gamescope) there is no system tray and no desktop; a tray icon is invisible and a toast is impossible. In **Desktop Mode** it is just KDE with a browser. So the Linux agent is a **headless daemon** that serves the existing `agent-ui` on `localhost:5178` — the same UI, for free, reachable from a browser or another device on the LAN. No WinForms equivalent, no GTK/Qt, no second frontend.

Consequence, and it is a design obligation rather than a nice-to-have: **a headless spoke cannot tell the user anything.** A conflict that raises a toast on Windows is *silent* on a Deck. The agent must therefore report health and errors to the server so the console can surface them ("Steam Deck: conflict on Hades, 2 days ago"). **The console is the Deck's UI.** This ships *with* the Linux agent, not after it.

### 3. The Steam launch wrapper is the primary trigger — not process polling
Users add `savelocker run %command%` to a game's Steam launch options. Steam then supplies `STEAM_COMPAT_DATA_PATH` and `SteamAppId` in the environment, which gives:
- the **exact Wine prefix**, with no compatdata scanning or guessing, and
- **precise** pre-launch / post-exit hooks, with no polling.

Process-name polling is the fallback for non-Steam launchers (Heroic, Lutris, Bottles), and it is genuinely unpleasant on Linux — `/proc/<pid>/comm` truncates at 15 chars and Proton games hide behind `reaper` / `pv-bwrap` / `wine` wrappers. Prefer the wrapper wherever it is available.

### 4. Enrollment carries a short-lived token, not an API key — and is not signed
The console generates an enrollment file (server URL + preselected games/globs/settle delay) carrying a **single-use, ~15-minute enrollment token**, which the agent redeems for its real machine API key on first contact. A leaked file then expires on its own and is revocable. A long-lived API key sitting in `~/Downloads` is not.

**The policy file is deliberately not signed.** The threat a forged file poses is not a bogus token — it is being pointed at a **malicious server**, whose *pull* writes files into save directories. Signing cannot fix that, because a fresh agent has no trust anchor and therefore no way to know the right public key; the *user* is the trust anchor (they downloaded the file from their own console). A PKI here would be security theatre. What actually mitigates it, in order: **HTTPS** (already have, via the Tunnel), **hardening the restore path** (see below), and **TOFU-pinning** the server after enrollment.

Detached signing only earns its keep for *offline* policy distribution (bundling a policy into an installer for machines that never contact the console first). Build it then, not now.

### 5. Install to the user's home, never to /usr
SteamOS's root filesystem is **immutable and wiped on update**. Install to `~/.local/share/SaveLocker` with a `systemd --user` unit, which survives SteamOS updates. This rules out a plain `.deb`/`.rpm` system install. Self-contained publish is mandatory — SteamOS ships no .NET runtime.

### 6. Dev on WSL2 (Ubuntu 24.04 LTS), not a VM, not Arch
WSL2 (inside the **ext4 home** — never `/mnt/c`, where DrvFs breaks inotify, permissions, case-sensitivity and locking) faithfully reproduces everything that matters: Linux `FileShare` semantics, inotify, `/proc`, case-sensitivity, `systemd --user`, and self-contained publish.

**Distro: Ubuntu 24.04 LTS.** The tempting reasoning — "SteamOS is Arch, so develop on Arch" — is wrong. Everything WSL actually validates (the list above) is **kernel and .NET behaviour, identical on every distro**, while the things that make SteamOS *SteamOS* (gamescope, immutable rootfs) cannot run under WSL on any base. So Arch buys zero extra fidelity and costs the thing that does pay: **CI parity** — GitHub Actions `ubuntu-latest` *is* Ubuntu 24.04, so dev and CI share a glibc, a .NET packaging story and a toolchain.

**glibc.** The rule of thumb — *build on the oldest glibc you intend to support*, because an older-glibc build runs on newer systems but **never the reverse** — still stands, and it is why the release job pins **`ubuntu-latest`** rather than drifting onto whatever runner is convenient.

> **Measured 2026-07-14, because the mechanism is not what it looks like.** A self-contained .NET app does **not** natively compile against the build host's glibc: `libcoreclr.so` and the other native libs are **prebuilt by Microsoft against an old baseline** and simply copied in, and our C# becomes IL. So the artifact's real floor is set by **.NET, not by Ubuntu**:
>
> | | glibc |
> |---|---|
> | What the package **requires** (`objdump -T`, all `.so` + apphost) | **2.27** |
> | What SteamOS 3 **provides** | **≥ 2.33** |
>
> Building on Ubuntu 24.04 (host glibc 2.39) is therefore safe with a wide margin — the host's version is not inherited. **The real risk is a change that raises that floor silently**: enabling **NativeAOT**, or adding a natively-compiled dependency, would bind to the build host after all. That failure appears as `GLIBC_2.3x not found` **on a user's Deck** and cannot be reproduced on any machine we own — so CI's `package-linux` job now **asserts** the floor stays ≤ 2.31 rather than trusting it.

The agent never talks to Steam — it reads two env vars and supervises a child process — so a **fake-game harness** (fixture compatdata tree + a script that writes saves slowly and exits, with the env vars set) exercises the entire code path with no Steam, no Proton and no GPU. That harness is also the CI test.

Not testable without hardware: gamescope/Game Mode, the immutable rootfs, SD-card library paths, suspend/resume. A VM buys only the immutable-rootfs check and makes gamescope worse. **No Deck is owned** — hardware validation is an explicit deferred-risk item, exactly like the existing Windows device-verify pattern.

## Environment facts (user-provided)
- Games are **standalone builds**, not bought on Steam/Epic → save locations unpredictable, hence manifest-based detection + manual `--dir` fallback.
- User has a domain on CloudFlare and uses **CloudFlare Tunnel** for remote access.
- Sync trigger: **hybrid** (automatic background + manual override).
