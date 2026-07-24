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
In **Game Mode** (gamescope) there is no system tray and no desktop; a tray icon is invisible and a toast is impossible. In **Desktop Mode** it is just KDE with a browser. So the Linux agent is a **headless daemon** that serves the existing `agent-ui` on `localhost:5178` — the same UI, for free, reachable from a browser in Desktop Mode. No WinForms equivalent, no GTK/Qt, no second frontend.

**Loopback only — see §7.** An earlier `--lan` flag bound this to every interface; it has been withdrawn.

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

### 7. The agent's local API is loopback-only, token-authenticated, and never serves the machine key
`AgentApiServer` is shared by the Windows tray and the Linux daemon, and it is a **management** API: it rewrites `config.json`, re-registers this machine, and changes what syncs. Reaching it is equivalent to owning the box. It originally shipped unauthenticated with `AllowAnyOrigin`, and returned the machine's server API key in `/api/state` and `/api/config`.

Four things, all of which are load-bearing together — none is sufficient alone:

1. **Loopback only, always.** Kestrel binds `localhost`. `daemon --lan` is **withdrawn** and now exits non-zero with an SSH-tunnel instruction, rather than being silently ignored — someone's autostart unit or notes may still carry it, and they need to learn the exposure is gone. Remote access is an **authenticated SSH tunnel**, which supplies the authentication and transport security this API does not have.
2. **A high-entropy local token** (32 random bytes, `{configDir}/api-token`, `0600`) on every `/api/*` request, compared in fixed time. This is what stops *another process running as this user*, and any web page the user has open, from driving the agent. The bundled UI gets it by having it injected into `index.html` at serve time; the same-origin policy is what stops another page reading it back.
3. **Host and Origin validation.** A DNS-rebinding page resolves *its own* name to `127.0.0.1`, so the socket is loopback but the `Host` header still carries the attacker's domain — rejected, token or not. A foreign `Origin` is rejected the same way. **No CORS policy exists**: the UI is same-origin, so nothing legitimate needs one.
4. **The machine API key is never serialized into a response.** Not in `/api/state`, not in `/api/config`, and `/api/register` returns the machine name rather than echoing the new key. The agent UI shows *whether* the machine is registered, not its secret. `whoami` still prints it — that is a local CLI the user runs in their own terminal, not something served over a socket.

`/openapi` is deliberately **not** token-gated: it is a static description of the API with no machine state in it, and the UI's type generator has no way to send a header. Proven by `tests/run-local-api-tests.ps1`, which asserts each attack is refused rather than that the UI still works.

### 8. Two processes own the agent's state, so every shared file is locked, atomic, and merged
The agent is **not one process**. Autorun keeps the daemon alive while Steam starts `savelocker run -- %command%` as a second one (on Windows, the tray plus any CLI command). They share `config.json`, `offline-queue.json`, `health-events.json` and the temp archive directory. The locks that were here — a `SemaphoreSlim` and a `lock` statement — are in-process, and do nothing across that boundary.

- **A per-game cross-process lock** (`AgentStateLock`, a lock file opened `FileShare.None` → `flock` on Unix, share-deny on Windows) wraps push and pull. It is held **in addition to** the in-process semaphore, not instead: a `flock` is owned by the *process*, so two threads in the same process both acquire it and neither blocks. Each layer covers what the other cannot.
- **A lock timeout does not throw.** It logs and proceeds. A stale lock file from a crashed process must never be able to stop a game syncing forever — this tool exists to protect saves, and failing closed would lose them.
- **Temp archives carry PID + GUID.** They were `{gameId}-push.zip`, shared by every process: two pushes of one game wrote the same file, and the first to finish deleted the other's archive mid-upload. A 6-hour sweep reclaims what a killed process leaves behind.
- **Every write is atomic** (`AtomicFile`: temp file + rename). `File.WriteAllText` truncates before writing, so a second process could read an empty file and — this is the damaging part — `AgentConfig.Load` would fall back to defaults, discarding the machine's API key and game list.
- **Read-modify-write is merged under the lock, not overwritten.** This is the bug that mattered: the daemon holds a config loaded at startup, and a whole-object `Save()` erased the `LastKnownVersionId` another process had just recorded. The next push then presented a stale parent and the server rejected it — **one machine conflicting with itself**, indistinguishable in the dashboard from the two-machine divergence in `CONTEXT.md`. `SaveGameSyncState` re-reads under the lock and applies only that game's fields; the queue and health files merge the same way.

**State belongs beside the config file it came from**, not in the machine default — with `--config` those diverge, and each process would keep a private queue while believing it shared one.

The long-term shape is **one owner**: wrapper→daemon IPC over a Unix socket, standalone only when no daemon is up. The locking above makes two owners *correct*; IPC would make it *simple*. Deferred, not rejected.

### 9. A pulled archive is hostile input, and the restore is written that way
The archive arrives over the network from a server the agent may have been pointed at by a **forged enrollment file** (§4 — the policy file is deliberately unsigned, and the threat it accepts is a malicious server URL). Everything in it — entry names, entry count, declared sizes — is attacker-controlled. Phase 6 hardened the restore's *delete* pass; the *copy* pass was still trusting.

- **No destination may traverse a link below the save root.** If the target already held `linkdir -> /home/user` and the archive carried `linkdir/.bashrc`, `File.Copy` wrote **through** the link and overwrote a real file outside the save folder. `run-hardening-tests.ps1` reproduces this against pre-fix code — it is not theoretical.
- **The root itself IS followed**, which is a deliberate departure from "reject any symlink". The root is *user-chosen* (`add-game --dir`), and a Deck user symlinking saves onto an SD card is a legitimate setup that must keep working. The paths **inside** the archive are not user-chosen, and those are what get checked.
- **The whole restore is rejected, never partially applied.** Skipping the offending file would leave a half-restored save that reports success.
- **Size caps** — 100,000 entries, 2 GB uncompressed. Checked against the declared central-directory sizes first (cheap, rejects an obvious bomb before a byte lands) *and* against **bytes actually written**, because the declared size is attacker-controlled and may understate. Env-overridable, which is both an operator escape hatch and what makes the caps testable without a 2 GB fixture.
- ⚠️ **Extraction is hand-rolled now** (`ExtractChecked`), because a byte cap cannot be enforced through `ZipFile.ExtractToDirectory`. That means the zip-slip rejection it used to give for free is **ours to maintain** — the existing zip-slip test was kept and re-aimed at the replacement rather than deleted.
- A refused archive is reported to the console as an event, not just thrown: a Deck owner would otherwise see nothing, and "refused" looks identical to "already up to date" from the outside.

### 10. Sync state is read back before it is used, and `Save()` cannot write it (locked 2026-07-23)
Amends §8, which fixed only half of this. §8 stopped a long-lived process **erasing** a parent version another process had recorded. It did nothing to stop that process **using** a parent already superseded — and the daemon holds `TrackedGame` references from boot for its entire lifetime (`Daemon.StartFolderWatchers`), so once the launch wrapper pushed on game exit, every watch-push presented the boot-time parent. The server correctly recorded a conflict; the conflict path deliberately does not advance the pointer; the daemon never recovered. **75 conflicts and 2.66 GB on a retain-5 game, on a fleet of one machine** (`logs/2026-07-23_conflict-storm.md`).

- **Read back under the lock, immediately before use.** `SyncEngine` calls `AgentConfig.RefreshGameSyncState` inside the per-game lock at the top of push and pull. Refreshing `LastSyncedHash` matters as much as the parent: it gates the un-pushed-changes check, so a stale one makes a legitimate pull look like it would destroy local progress and the pull is refused.
- **`Save()` is safe by construction, not by convention.** It preserves per-game sync bookkeeping from disk; `SaveGameSyncState` is its only writer. §8 stated this as a rule in a doc comment and left 17 call sites to remember it — `CommandPoller.ReconcileGamesAsync` did not, and wrote the daemon's boot-time parent over the file on every game-list change. **A rule that 17 callers must remember is not a rule.** Audit first: every one of those callers writes settings or the game list, so none lost anything by the change.
- **`SaveDirectory` is the caller's to write**, deliberately. It is reconciled from the server (its highest authority) and set by the agent UI and CLI, all of which route through `Save()`. Preserving it from disk would discard exactly the write each caller is making.
- **Lock order is `ForGame` → `config`, never nested on the same name.** The refresh acquires and releases `config` before `PushCoreAsync` takes it via `SaveGameSyncState`, and `Save()`'s disk read happens inside the lock it already holds via an unlocked helper. Verified under real `flock` in WSL, which Windows share-deny semantics would not have exposed.
- ⚠️ **`changed` in `ReconcileGamesAsync` is edge-triggered.** All five paths that set it assign the value that makes their own next comparison equal, so it is false in steady state and **an idle measurement reads as "this cannot happen."** It flips when a human edits in the console — so the corruption landed mid-repair, which is exactly when it struck.

`run-concurrency-tests.ps1` went 12 → 17. All 12 originals covered the **write** race; checks 6 and 7 cover the read path and the poll path, and both were verified to fail against pre-fix code before being accepted.

## Environment facts (user-provided)
- Games are **standalone builds**, not bought on Steam/Epic → save locations unpredictable, hence manifest-based detection + manual `--dir` fallback.
- Sync trigger: **hybrid** (automatic background + manual override).

## 11. Conflict pressure is capped on bytes and escalated on attention (locked 2026-07-23)
A conflict is a safety stop, not permission to upload the same full save forever. The first three
rejected payloads preserve a useful divergent history. After that, ordinary pushes report the
condition without creating an archive or sending its bytes. The counter is persisted per game,
shared under the same config lock as parent/hash state, and resets only after a clean push or pull.
A forced push remains the explicit bypass.

Six hours is the attention threshold. The console marks the conflict overdue and every heartbeat
returns stale conflicts so a connected Windows tray can notify the user even when the stuck machine
is a Deck. Long-lived agents notify once per conflict ID; the server keeps returning it until it is
resolved so restarts still produce a reminder.

“Keep both” does not create two heads. The chosen snapshot becomes the single authoritative Latest,
while both conflict snapshots receive `SaveVersion.Protected` and are exempt from automatic
retention. Protection can be removed explicitly from the Versions table. Conflict resolution also
refuses to promote an option older than the current head, because a resolution must not silently
undo a later Set as Latest.
