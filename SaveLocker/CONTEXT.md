# SaveLocker — Session Context

**What:** Self-hosted Windows game save sync. Hub-and-spoke: C#/WinForms tray agent on each PC syncs saves through an ASP.NET Core server (Docker on unRAID). React admin dashboard + embedded React agent UI.

**Repo:** https://github.com/SkorcherX/SaveLocker | **Branch:** main

**Current version:** **v0.3.1** (tagged 2026-07-19, PR #15 — **the Windows tray could not exit**).
Earlier: v0.3.0 (2026-07-19, PRs #9–#13 — Deck path setup, and four bugs the first real hardware
session exposed); v0.2.0 (2026-07-18, PR #8 — Linux/Deck security hardening, `Decisions.md` §7–§9).

🚧 **v0.3.1 is TAGGED BUT NOT PUBLISHED.** The tag is pushed and correct, and both jobs *built*
fine — but every attempt to attach the assets died in the GitHub incident of 2026-07-19 23:34 UTC
(Actions degraded, API Requests partial outage). The release exists as a **draft with 0 assets**, so
**there is no v0.3.1 installer or tarball to download yet.**

- **To finish it:** `gh run rerun 29708968300 --failed`, then confirm the release has both assets and
  is no longer a draft. `action-gh-release` reuses the existing draft rather than duplicating it, so
  nothing needs cleaning up first.
- The failure was purely the upload step (`Attach tarball to the release` → 503, then
  `Too many retries`). Nothing in this repo is implicated — do not go looking for a build problem.
- **v0.3.0's Linux tarball is unaffected and published**, and the tray deadlock is Windows-only, so
  Deck work is not blocked by this.

### ▶ WHEN GITHUB IS BACK — do these in order

`main` is **2 commits ahead of origin, unpushed on purpose** (pushing during the incident would only
queue runs that fail). Nothing is broken; this is a deliberate pause.

1. `git push` — the held docs commit plus **`d3bb977`, a data-loss fix that has never been pushed**.
2. `gh run rerun 29708968300 --failed`, then confirm the v0.3.1 release has both assets and is no
   longer a draft.
3. **Consider folding this into v0.3.2 instead.** v0.3.1's assets never uploaded, so nobody has it —
   and `d3bb977` is data-loss-class, which argues for one release that contains both rather than
   shipping v0.3.1 and immediately superseding it.

**`d3bb977` — a save folder mapped too deep silently DELETED the real saves.** An archive stores
paths relative to the save root of the machine that made it. A PC rooted at `X` and a Deck rooted at
`X/sub` meant restoring produced `X/sub/sub/…` **and the delete pass removed the correctly-placed
files**, because at that depth they are absent from the archive. The pull reported success. Now
refused before the copy or delete pass, with the repeated segment named. Proven against pre-fix code:
`FAIL: the correctly-placed save was NOT deleted`. Hardening suite is now 33 (Win) / 34 (Linux).

⚠️ **The maintainer's fleet is still on v0.3.0**, which has the silent-nesting behaviour. Until the
agents are updated, a save path set to the wrong depth destroys saves with no error anywhere.

### v0.3.1 — the tray Exit deadlock

**Right-click → Exit did not stop the agent**, and a second Exit left the menu frozen on screen;
only Task Manager could end it. `AgentApiServer.Dispose()` blocked the WinForms UI thread with
`StopAsync().GetAwaiter().GetResult()`, and the continuation needed that same thread. Kestrel stops
listening first, so :5178 went dead while the process lived on — which is what made it look like a
half-crash rather than a deadlock.

- **Present since v0.1.8**, when the local API landed. Every Windows agent from 0.1.8 to 0.3.0 has it.
- Diagnosed from a **stack captured off the live hung process** (`dotnet-stack report -p <pid>`) —
  worth remembering as the tool for this, since nothing was in the log.
- Fixed with `Task.Run` so continuations land on the thread pool, plus a bounded wait. `Start()` had
  the same shape and got the same treatment.
- Reproduced in a harness that drives `Dispose` from a real message loop: **old = deadlock at 30 s,
  fixed = 8 ms**.
- Also: an update check that returned `Failed` or `Skipped` produced **no toast and no log line**, so
  "I clicked check for updates and nothing happened" was undiagnosable. Every outcome is now logged
  and a user-initiated check always answers.

### v0.3.0 — what and why

Setting a save path on a Deck no longer needs a terminal: a rooted folder browser in the agent UI,
scan candidates offered for one-click **Apply** in the console, and `scan` offering to map an
unmapped game. Detail in `Backlog.md` under "Steam Deck onboarding UX".

**The four bugs matter more than the feature.** Every one was found by putting the agent on real
hardware for an afternoon, and none was hypothetical:

| Bug | Why it was invisible |
|---|---|
| **`install.sh` destroyed a running agent, then printed "Installed."** | `cp` cannot replace a running binary (`Text file busy`), overwriting mapped DLLs killed the daemon with **SIGBUS**, and `find -exec` hides its child's exit status so `set -e` never fired. This is THE documented Linux update path. |
| **`savelocker status` 401'd on any server with an admin password** | It called an **admin-filtered** route with a machine key. `AdminPasswordFilter` is wide open when no password is set — so it worked on a fresh server, and the test suite (which runs without one) passed for as long as the bug existed. |
| **A stale Proton prefix synced silently** | A non-Steam AppID is recomputed when a shortcut is renamed or re-pointed; Steam then makes a fresh prefix. The old path still exists, reads fine and hashes fine — it is just not where the game writes. `doctor` now compares the two. |
| **A save folder mapped one level too deep nests on restore** | Archives store paths relative to the save root, so pulling an archive rooted at X into X/sub recreates `sub` under itself. The pull *succeeds*. `SaveDirSanity` now flags a repeated path tail. |

⚠️ **The lesson to carry:** three of these four were invisible to the test suite *by construction* —
it ran in the state where the bug could not fire (no admin password; no running daemon; a clean
`.verify/`). When a suite passes, ask what state it never puts the system in.
⚠️ **BREAKING: `savelocker daemon --lan` is REMOVED** and now exits non-zero. Remote access is an SSH
tunnel: `ssh -L 5178:localhost:5178 <user>@<host>`.
**The key-rotation follow-up is DONE** (2026-07-18): container updated, both Windows agents upgraded
to 0.2.0 and re-registered, so no machine still holds a key issued by a version that served it.

Earlier: v0.1.8 (agent UI types from the local OpenAPI); v0.1.7 fixed a v0.1.6 regression that broke
silent auto-update (`NextButtonClick` fires under `/SILENT`; see `Gotchas.md`) — **v0.1.6 must never
be hosted for auto-update**. v0.1.5 released glob depth-matching; v0.1.4 shipped 5e glob filters.
Recently-shipped work indexed in `logs/shipped-2026-07.md`.

⚠️ **Check `git tag -l` before tagging.** This file claimed v0.1.7 while v0.1.8 was already pushed,
which nearly caused a duplicate tag on 2026-07-18.

---

## Status (2026-07-18)

| Area | State |
|------|-------|
| Tray agent (WinForms + React/WebView2) | ✅ done |
| Game scanning (Steam VDF + Ludusavi) | ✅ done |
| Server (REST API, EF/SQLite, leases, conflicts) | ✅ done |
| Admin dashboard (React + Tailwind, baked into Docker) | ✅ done |
| Agent auto-update (version, silent relaunch, installer persistence) | ✅ verified on device (v0.1.2) |
| Fetch installer from GitHub — manual dashboard button | ✅ done (2026-07-11) |
| Scheduled GitHub installer auto-poll | ✅ dashboard-configurable in Agent Updates; persisted server-side and applied within a minute |
| Sync notifications (one toast + save date, not 4) | ✅ v0.1.3, verified on device |
| Per-game exclude globs + 200 MB upload cap (5e) | ✅ v0.1.4; depth-matching fix in v0.1.5 |
| CI/CD (push → Docker → GHCR; tag → GitHub Release) | ✅ done (Watchtower removed) |
| Console Help KB (8 articles, search, deep-links) | ✅ done (2026-07-11, `be54374`) |
| Save-in-use safety (settle gate before auto-push) | ✅ built and device-verified |
| Cross-OS round-trip in CI (Windows agent ↔ Linux agent) | ✅ done 2026-07-13 — byte-identical both ways |
| **Runtime: .NET 10 (LTS)** | ✅ merged 2026-07-13 (PR #2). net9 was STS, EOL 10 Nov 2026 |
| **Known vulnerabilities** | ✅ **none** — `dotnet build` reports no NU1903 (PR #3) |
| Linux agent **Phase 4** — enrollment token + policy import | ✅ done 2026-07-13 — 16/16 + 6/6 TLS (PR #4, merged) |
| Linux agent **Phase 5** — agent health reporting | ✅ done 2026-07-14 — 17/17 (PR #5, merged) |
| Linux agent **Phase 6** — hardening | ✅ done 2026-07-14 — 14/14. **Fixed a real data-loss bug** (below) |
| Agent local API + generated UI types | ✅ ASP.NET Core minimal API + OpenAPI; agent UI schemas generated from the live contract |
| **Local agent API hardening** | ✅ 2026-07-18 — 15/15 (`run-local-api-tests.ps1`, in CI). Token-auth + Host/Origin validation, no CORS, `--lan` withdrawn, machine key no longer served. `Decisions.md` §7. ✅ **fleet keys rotated 2026-07-18** |
| **unRAID server on the current image** | ✅ 2026-07-18 — container updated by the maintainer. Was flagged as possibly still running a pre-net10 image |
| **Cross-process state safety** | ✅ 2026-07-18 — 12/12 (`run-concurrency-tests.ps1`, in CI). Fixed a **self-conflict bug**: a daemon's stale `config.json` write erased another process's parent version, so the next push was rejected as a conflict. `Decisions.md` §8 |
| **Restore treats archives as hostile** | ✅ 2026-07-18 — hardening now 27 (Win) / 28 (Linux), **7 flip against pre-fix code**. Closed a **proven arbitrary-file-overwrite**: the copy pass wrote through a symlink in the save folder. Plus zip-bomb entry/byte caps. `Decisions.md` §9 |
| **Installer GUI enrollment** (Windows) | ✅ v0.1.6 built the wizard page. 🐛 **v0.1.6 broke silent auto-update** (NextButtonClick fires under /SILENT → abort). ✅ **fixed in v0.1.7** (`WizardSilent` guard + `ShouldSkipPage` for enrolled machines). ✅ **silent upgrade of an enrolled agent device-verified on v0.1.7 (2026-07-14)**, and again 0.1.8 → **0.2.0 on two machines (2026-07-18)**. ⏳ fresh-install happy-path enroll (page shows server/name, machine goes online) still unverified on device |
| **0.2.0 upgrade + re-register on device** | ✅ 2026-07-18 — two Windows agents upgraded and re-registered **from the agent window**, confirmed working by the maintainer. That exercises the whole new auth path on real hardware: WebView2 loads the injected token from `index.html` and sends it on every `/api/*` call. The local-API change is device-verified |
| **Help KB** | ✅ complete 2026-07-18 — 14 articles. `restore-safety.md` added for v0.2.0's refusal messages; Deck troubleshooting lives in `troubleshooting.md` |

Shipped-feature detail: `logs/shipped-2026-07.md` + `logs/sessions.md`. Open work: `Backlog.md`.
Full record of the .NET 10 upgrade: `logs/2026-07-13_dotnet-10-upgrade.md`.

---

## Deck path setup — shipped 2026-07-19 (all 5 backlog items)

Setting a save path on a Deck no longer requires a terminal. Three surfaces changed; detail and
rationale in `Backlog.md` under "Steam Deck onboarding UX".

- **Agent UI path browser** — `GET /api/browse?path=` (`Agent.Core/PathBrowser.cs`), **rooted** at
  `$HOME` + host Steam roots, containment checked *after* canonicalization, symlinks never followed,
  `/run/media` included so an **SD card** is reachable. `run-local-api-tests.ps1` is now **22 checks**.
- **The native dialog still wins where there is one.** The UI calls `folderPick` first: the Windows
  tray shows Explorer and the browser never appears; the Linux daemon returns null and *that* opens
  the browser. `agent-ui/` is one shared bundle — this is the seam that keeps the Deck flow off Windows.
- **Console** — per-machine path editing is now inline in the machine's row, labelled
  `Save path on <Machine>`; the `prompt()` is gone. Where an agent reported a guess the row offers
  one-click **Apply**.
- **Heartbeat carries guesses** — `AgentHeartbeat.PathCandidates`, appended + optional, into the new
  `MachineScanCandidates` table (migration `20260719190348`). The `openapi.json` diff is **+83 / −0**,
  so the either-order deploy note below still holds. A guess is deliberately **not** written to
  `MachineSavePaths`, which is pushed back to agents as authority — it would auto-apply itself.
- **`scan` offers to map** an unmapped tracked game (`--yes` / `--no-prompt`; prompting is skipped
  when stdin is redirected, so systemd and the harness never block).

⚠️ **Two things to know before continuing:**
1. **`npm run gen:api` in `agent-ui/` hardcodes :5178 — an installed agent's port.** It silently
   generated types from the *installed* v0.2.0 build. New schemas were just missing, no error.
   Generate against a dev daemon on a free port. See `Gotchas.md`.
2. **Giving `run-agent-tests.ps1` an isolated server DB *causes* 3 failures** unless you clear
   `.verify/` too. This is the documented "clear the server DB and `.verify/` **TOGETHER**" trap in
   `Gotchas.md` — its table predicts this exactly: *server DB only → "Laptop pull restores save" is
   BLOCKED, ~3 fail*, because `.verify/laptop_save` still holds the previous run's file and the pull
   correctly refuses to overwrite apparent un-pushed progress.
   - The habit of isolating `Storage__DbPath` (right for the enrollment suite) is **wrong on its own
     here** — it resets one half of a pair. Delete `.verify/` in the same breath.
   - Verified 2026-07-19: `.verify/` removed + fresh DB → **10/10**. Nothing was wrong with the code.
   - The suite does not self-clean on purpose: wiping `.verify/` alone against a persistent dev DB
     produces the *other* failure mode in that table (initial push reports CONFLICT, ~4 fail).

**Verified on a real Deck 2026-07-19.** The browser works; `/run/media` is in the root list, so SD
cards are reachable. **The D-pad does not work** — Desktop Mode maps the right stick to the cursor
and the left stick to scroll, the D-pad to neither. Point-and-click is the real interaction (the
44 px rows already assumed it), so only the KB claim was wrong, and it is corrected. Arrow keys still
work with a keyboard attached.

⚠️ **"Headless" is actively misleading in our own docs.** The maintainer's first reaction on hardware
was *"I didn't know we had a UI because we kept calling it headless"* — and `adding-games.md` did
literally say there was no UI to click. It means **no tray icon and no pop-ups**; the daemon serves
the full agent UI on :5178. Both KB articles now say so up front. Prefer "no tray, no pop-ups" over
the bare word.

---

## ▶ NEXT ACTION: **Device-verify the fresh-install enroll path**

Everything else that was queued is done: the three security-hardening items shipped in v0.2.0, the
container is updated, the fleet is rotated, and the Help KB is complete (below).

**The Help KB is DONE (2026-07-18).** ⚠️ Do not restart it from `CONTEXT.md`/`Backlog.md` history —
both files spent days pointing at `tasks/linux-kb-articles.md`, which had *already been deleted*, and
listing articles that had *already shipped*. On checking: `deck-supported-games` was written and
registered; `deck-troubleshooting` was folded into `troubleshooting.md` (the task file explicitly
allowed that); all four §4 edits were applied. The one real gap was newer than the task file —
v0.2.0's restore-refusal messages were undocumented — and is now `restore-safety.md`.
**Lesson: check the filesystem before trusting a task pointer in this vault.**

---

## Then: **Finish device-verifying the fresh-install enroll path**

Requires a machine where `%PROGRAMDATA%\SaveLocker` does **not** exist — i.e. not the maintainer's
daily driver. The *upgrade* path is well verified (0.1.8 → 0.2.0 on two machines, 2026-07-18); it is
the **fresh install** that has never been exercised (scenarios in the archived task
`logs/2026-07-14_installer-enrollment.md`):

- **Happy path:** on a machine where `%PROGRAMDATA%\SaveLocker` does *not* exist, run the installer,
  choose an enrollment file → the page shows the right server + machine name → install → the machine
  appears in **Configuration → Machines**, **online**, with its version.
- ⚠️ **The ACL trap** (verify right after the happy path): `icacls "%PROGRAMDATA%\SaveLocker"` — the
  interactive user needs **Modify**. The enroll runs via `ExecAsOriginalUser` precisely so the config
  dir is created de-elevated by the user that later rewrites it.
- Also: expired-token (page says so, install still succeeds), skip path (installs unenrolled), and the
  unattended switch `Setup.exe /SILENT /ENROLL="C:\path\policy.json"`.

Also unverified on a device: the **agent window itself** since the local-API auth change. The token is
injected into `index.html` and WebView2 must send it back on every `/api/*` call. It is proven in
tests and in a real browser, but nobody has confirmed the WinForms/WebView2 window renders and
functions on a device post-0.2.0 (the two re-registrations may have been done via the CLI).

**Do not host v0.1.6 for auto-update** — it aborts under /SILENT. Host **v0.2.0** or newer.

### Steam Deck hardware — NOW AVAILABLE (2026-07-19)

The maintainer now owns a Steam Deck. The long-standing "no hardware" blocker is resolved.
gamescope / Game Mode, the immutable rootfs, SD-card library paths, and suspend/resume **can now
be tested on real hardware**. KB claims about Game Mode and the Launch Options UI should be
validated on-device and updated from observation rather than documentation. Linux auto-update
(previously deferred partly for lack of hardware) is now unblocked on the hardware side.

Other open work is in `Backlog.md` — fresh installer-enrollment verification, code-signing the exe,
and deploying the net10 server to unRAID.

### 🐛 Phase 6 fixed a REAL data-loss bug (2026-07-14) — worth knowing about

`Directory.EnumerateFiles(..., AllDirectories)` **follows symlinks**, and a Wine prefix is full of
them. The archive leak was the *lesser* half. The dangerous half: **`RestoreArchive` deletes target
files that are absent from the archive**, so walking through a link it **deleted files outside the
save folder entirely.** The pre-fix harness run reproduced both for real. **Windows was affected too**,
via junctions.

- Fixed with a no-follow walk (`SaveArchive.EnumerateFilesNoFollow`): links are never archived, never restored, never deleted.
- ⚠️ **Do not "simplify" the link test to `FileAttributes.ReparsePoint`.** OneDrive files-on-demand placeholders are *also* reparse points — that version silently stops archiving every OneDrive save. It keys on `FileSystemInfo.LinkTarget`, which is non-null only for symlinks and junctions. See `Gotchas.md`.
- Also landed: `SaveDirSanity` (names a Wine prefix mistaken for a save folder, surfaced by `doctor`), a proven zip-slip rejection, and a **monotonic** settle gate — wall-clock counted suspended hours as elapsed, so a suspended Deck woke up and published a possibly mid-flush save.

### Phase 5, shipped 2026-07-14 — how a Deck's failures reach you

**The console is the Deck's UI** (`Decisions.md` §2). A headless box cannot toast, so the agent
reports to the server and the dashboard surfaces it: a **problem badge** in the NavBar (absent when
the fleet is healthy) opening a list of events with Dismiss, plus per-machine health on the Machines
card — online / offline / **never reported**, agent version, platform, last sync, unmapped games,
queued pushes.

- **Scope, and it matters:** the server already knows what happens *server-side* (a conflict is a `ConflictFlag` the moment the upload lands). Agents report only what the server **cannot infer** — blocked pull, missing save folder, rejected upload, settle timeout, unreachable server. The conflict event exists solely to name **which machine is stuck**.
- **Events deduplicate** on (machine, game, code) while open — a persistent fault bumps a count, it does not write a row every 20 s. A game that **syncs cleanly auto-closes** that machine's events for it, so a Deck that recovers leaves no stale alarm.
- **Pending events persist to disk**, because the most important thing to report — "I cannot reach the server" — happens precisely when reporting is impossible. It is delivered on the first contact after the network returns.
- **Every sync path reports**, not just the daemon: the launch wrapper (`ProtonRun`) *is* the Deck's Proton sync path and has no poller, so it flushes before exiting; one-shot `push`/`pull` do too.
- The Windows tray **also** reports (it toasts *and* reports), so the console is one honest view of the whole fleet.

### Phase 4, shipped 2026-07-13 — how enrollment works now

A machine is set up with **one file and one command**, and **no API key is ever copied by hand**:
Console → *Configuration → Enroll a machine* mints a **single-use, 15-min token** wrapped in a
policy file (server URL + games + settle delay); the agent runs `enroll --file <policy>` and trades
the token for its own machine key. The token's **raw value exists only in that one download** — the
server stores a hash.

- **Unsigned, on purpose** (`Decisions.md` §4) — the threat is a *malicious server URL*, not a bogus token, and a fresh agent has no trust anchor to check a signature against. Do not "fix" this with a PKI.
- **TOFU pin:** the agent pins the server's TLS public key at enrollment and **warns (never blocks)** if it changes — a hard fail would take a headless Deck offline on a routine cert renewal. `trust` shows it; `trust --accept` re-pins.
- A token minted **for a machine name binds it** — `--name` cannot override it. Redeeming an existing name **rotates** that machine's key: that is the re-enrollment path for a wiped device.
- `enroll` lives in **`Agent.Core`**, so Windows and Linux run the same implementation.

---

## Open

- **Code-sign the exe** — SmartScreen warns for unsigned installers. Explicitly deferred by the maintainer.
- ⚠️ **`%PROGRAMDATA%\SaveLocker` ACLs on a multi-user Windows box.** v0.2.0 added `api-token` (the local-API secret) to that directory. **Not a new exposure** — `config.json`'s machine key already sat there under the same ACL — but both are readable by other local users on a shared machine. `Backlog.md`, medium priority.
- **Deploy note for next time:** old agents are wire-compatible with the current server (v0.1.8 ↔ v0.2.0 changed **nothing** in `Contracts.cs`, `src/Server/` or `ApiClient.cs`), so the container and the fleet can be upgraded in either order and independently.

**Gotcha surfaced 2026-07-12:** with two agents, saves diverge → dashboard conflict when the pushing machine's known head ≠ current server head (another machine advanced it). A "behind" machine keeps conflicting until resolved (dashboard resolve → pull, or tray Force Pull); the agent doesn't auto-advance its parent on conflict. Version/glob skew between agents guarantees this — keep both agents identical. (This is the seed for the Help KB "Understanding conflicts" article.)

**🐛 Real bug fixed 2026-07-13 (found by the Phase 3 cross-OS test):** `ArchiveStore` persisted the archive's store path into the DB using `Path.Combine`, so a **Windows-hosted server wrote `gameid\versionid.zip`**. On Linux a backslash is a filename character, not a separator — the archive becomes unreachable, `/download` 404s, and the agent reports **"server has no saves yet"** while `status` still shows a head. Production is fine (server only runs in Docker/Linux) but **the dev workflow runs the server on Windows**, so a DB or backup carried from a dev box to Docker has unreachable archives. Fixed: canonical `/` on write, either separator tolerated on read. Rule: `Path.Combine` is for *this* machine *now* — anything persisted gets a `/`. See `Gotchas.md`.

See `Backlog.md` for the full list.

---

## Dev quick-reference

| Task | Command |
|------|---------|
| Run server | `cd src/Server && dotnet run` → http://localhost:5179 |
| Run dashboard | `cd web && npm run dev` → http://localhost:5173 |
| Build agent | `dotnet build src/Agent/SaveLocker.Agent.csproj --no-incremental` |
| Build installer | `.\installer\build-installer.ps1` |
| Run tests (Windows) | `.\tests\run-agent-tests.ps1` (server must be on :5179) |
| Run tests (Linux) | `pwsh tests/run-agent-tests.ps1` — same script, drives the Linux agent |
| Enrollment tests | `.\tests\run-enrollment-tests.ps1` (16 checks; needs :5179). Run it **after** the agent suite — it adds a game + machine to the DB |
| Local agent API security | `.\tests\run-local-api-tests.ps1` (**22** checks). Starts its own daemon on **:5188** via `daemon --port`, so it never collides with a real agent on :5178. Needs nothing running. Now also proves the path browser is **rooted** — outside `$HOME`/Steam is refused, including via `..` and via a symlink that lives *inside* `$HOME` |
| Cross-process state | `.\tests\run-concurrency-tests.ps1` (12 checks; own server on **:5183**, own daemon on **:5189**). Daemon vs. a second process over `config.json`, the offline queue and health events. Verified to FAIL against pre-fix code |

**WSL is a working test bed — use it.** Ubuntu 24.04 is provisioned (see Toolchain below) with a clone at
`~/SaveLocker`. `dotnet` and `pwsh` are **not on a non-interactive PATH**, which makes them look absent;
export first. This is how to run the Linux-only harness and to exercise real `flock` and `0600` semantics
that Windows cannot show you:
```sh
wsl -d Ubuntu-24.04
export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
cd ~/SaveLocker && git fetch /mnt/e/Projects/SaveLocker <branch> && git reset --hard FETCH_HEAD
cp -r /mnt/e/Projects/SaveLocker/agent-ui/dist/. agent-ui/dist/   # avoids the Windows-npm-on-PATH trap
dotnet build src/Server/SaveLocker.Server.csproj src/Agent.Linux/SaveLocker.Agent.Linux.csproj --no-incremental
bash tests/linux/run-linux-tests.sh          # 33 checks
```
⚠️ Suites that need a server on :5179 must be given **isolated `Storage__DbPath` / `Storage__ArchiveRoot`**.
Reusing a dirty dev DB fails 12/16 of the enrollment suite for reasons that look exactly like a code regression.
| Health tests | `.\tests\run-health-tests.ps1` (17 checks). **Starts and stops its own server on :5181** — it has to, since one check pushes while the server is *down*. Needs nothing running |
| Hardening tests | `.\tests\run-hardening-tests.ps1` (14 on Linux / 13 on Windows; own server on :5182). Security: symlink escape on archive **and on restore-delete**, zip-slip. Windows uses junctions (no elevation); Linux uses symlinks |
| TOFU pin tests (TLS) | `.\tests\run-enrollment-tls-tests.ps1` (6 checks; starts its own HTTPS server on :5443). Needs `dotnet dev-certs https --trust` — local only, not in CI |
| Linux fake-game harness | `tests/linux/run-linux-tests.sh` (27 checks; starts its own server) |
| Cross-OS round-trip | `tests/cross-os/crossos.ps1 -Leg author\|roundtrip\|confirm` — one leg per OS; CI chains them by passing the server's state as an artifact |
| Password-hash compat | `.\tests\verify-password-compat.ps1` — builds a server from an older git ref, has it hash an admin password, then asserts the current code still verifies it |

**Always use `--no-incremental` for server builds** — stale DLL reuse has masked changes before. Stop the running agent/server first (they lock the DLLs).

**CI (`ci.yml`) runs 8 jobs on every PR:** `build-dotnet`, `build-web`, `build-agent-ui`, `docker-build` (builds the server image — publishes nothing), `agent-tests-linux` (agent **+ enrollment + health + hardening**), and the chained `crossos-author → crossos-roundtrip → crossos-confirm`. The cross-OS chain is the one that matters: it hands the **server's own state** (SQLite DB + archive store) between a Windows and an Ubuntu runner as an artifact, because runners cannot share a network.

### Toolchain (installed 2026-07-13 — a fresh session does not need to redo this)

| Where | What |
|------|------|
| Windows | .NET SDK **9.0.315 + 10.0.301** side by side. `global.json` pins **10.0.x** — that is why `dotnet --version` at the repo root says 10.0.301 and never silently picks another. Only **Windows PowerShell 5.1** (no `pwsh`), so scripts here must stay 5.1-compatible (no `??`, no `$IsWindows`, and a BOM-less `.ps1` is read as **ANSI** — keep them ASCII). |
| WSL (Ubuntu 24.04) | .NET SDK **9.0.315 + 10.0.301** in `~/.dotnet`; **pwsh 7.4.6** in `~/.local/pwsh` (symlinked to `~/.local/bin/pwsh`); **Node 22 via nvm** (`. ~/.nvm/nvm.sh`). Repo clone at `~/SaveLocker` on **ext4** — never build or test from `/mnt/*`. |

⚠️ **Inside WSL, `npm` resolves to the WINDOWS npm** on the shared PATH unless a Linux node is put first — the symptom is the baffling `error TS5083: Cannot read file 'C:/Windows/tsconfig.json'`. Source nvm and prepend it before running anything that shells out to npm (e.g. `packaging/linux/build-linux.sh`).

---

## Deployment
- **unRAID:** Docker on port 5080. `git push main` → Actions build → GHCR. To deploy: `docker compose pull && docker compose up -d`.
- **Tag a release:** `git tag v0.2.0 && git push origin v0.2.0` → `release.yml` builds **both** agents → GitHub Release:
  - **Windows:** `SaveLocker-Agent-Setup-<ver>.exe` (Inno Setup, built on `windows-latest`).
  - **Linux / Steam Deck:** `savelocker-<ver>-linux-x64.tar.gz` (self-contained, built on **`ubuntu-latest`** — see below).

### How a Steam Deck user installs the agent
```
tar -xzf savelocker-<ver>-linux-x64.tar.gz
./SaveLocker/install.sh
savelocker enroll --file <policy.json>      # from the console: Configuration → Enroll a machine
savelocker doctor
```
Installs to `~/.local/share/SaveLocker`, symlinks `~/.local/bin/savelocker`, enables a `systemd --user`
unit. **Never `/usr`** — SteamOS's rootfs is immutable and wiped on update (`Decisions.md` §5).

- ⚠️ **The Linux tarball MUST be built on `ubuntu-latest`.** A self-contained .NET binary binds to the **build host's glibc**, and an older-glibc build runs on newer systems but *never the reverse*. Ubuntu 24.04 (glibc 2.39) is older than SteamOS's rolling Arch, so Ubuntu → Deck is forward-compatible. Build it on anything newer and users get `GLIBC_2.4x not found` — an error you cannot reproduce on the machine that built it.
- CI's **`package-linux`** job builds the tarball on every PR and *installs it into a throwaway HOME*, so packaging cannot rot silently between releases (it is otherwise only exercised on a tag — i.e. too late).
- **That tarball is now kept as a run artifact** (`savelocker-linux-x64-<sha>`, 14 days), so **you can put any commit on a real Deck without tagging**. Download it from the run's Artifacts, then
  `tar -xzf savelocker-9.9.9-ci-linux-x64.tar.gz && ./SaveLocker/install.sh`. It is stamped
  **9.9.9-ci** on purpose — the agent reports its version in every heartbeat, so a test build must be
  impossible to mistake for a release in the console. Before this, hand-building one in WSL was the
  only way to get code onto hardware, which is precisely why Deck-only paths stayed unverified.
- **Linux has no auto-update** (deliberate, not shipped). The update channel is installer-shaped and Windows-only; a Deck user re-runs `install.sh` from a newer tarball. See `Backlog.md`.
  - ⚠️ **Before v0.3.0 that upgrade path was broken** and looked like it worked: the agent was killed
    by SIGBUS mid-upgrade while the script printed "Installed." and exited 0. **Upgrading *to* v0.3.0
    from v0.2.0 still runs the OLD install.sh**, so stop the agent first:
    `systemctl --user stop savelocker.service`. From v0.3.0 onward the script handles it.

---

## Critical gotchas (read before touching builds or paths)
- Incremental builds can silently reuse stale DLLs → always `--no-incremental`
- Stop running agent/server before building (DLL file-lock)
- Dev storage uses `localstate/` not `data/` (Windows case-collision: `Data/` = source folder)
- `dotnet` may not be on PATH in an open shell after winget install — open a new shell
- OneDrive save paths block `Directory.Move` — RestoreArchive uses file-by-file copy to `_tempDir`
- PowerShell array splatting to native exes splits strings containing `:` character-by-character — always use `if/else` + `"--property:Key=Value"` long-form
- MinVer requires git access; silently fails to `0.0.0.0` on GitHub Actions Windows runners. CI must pass `--property:Version=$v --property:AssemblyVersion=$v` explicitly from `github.ref_name`
- See `Gotchas.md` for the full list with fixes

---

## Key files
| Topic | File |
|-------|------|
| Codebase map | `REPO_MAP.md` |
| System design | `Architecture.md` |
| Locked decisions | `Decisions.md` |
| Known traps | `Gotchas.md` |
| REST endpoints | `API Reference.md` |
| Dev build & run | `Build and Run.md` |
| Agent CLI | `web/src/help/cli-reference.md` (KB article) |
| Active backlog | `Backlog.md` |
| Session history | `logs/sessions.md` |
