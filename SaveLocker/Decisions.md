# Decisions

Settled and shipped. Don't re-litigate — the "why" is kept to one line so a future
session can judge an edge case, not to reopen the choice.

- **Detection: reuse Ludusavi's manifest** (community save-location DB), don't re-map save
  locations ourselves. Build our own agent/server/dashboard for orchestration, leasing, conflicts.
  <br>**Corollary: do not scrape PCGamingWiki. The manifest IS that scrape** (2026-08-14, raised by
  a Breath of Fire IV page that looked like uncaptured data). Every row of that page's Game data
  table is in the manifest token-for-token — `<path-to-game>` → `<base>`, the per-store rows as
  `when: store:`, config rows tagged `config`. A scraper of our own would re-derive 52,973 entries we
  refresh with one `GET`, against a site that 403s automated fetches. What the wiki DOES hold that
  the manifest drops is nothing we want (launch commands, system requirements); what the manifest
  holds and we were dropping is covered by the next bullet. If save-path coverage needs to improve,
  the lever is contributing upstream to the wiki — it flows back on the next refresh.
- **Steam Cloud is per-game manifest data, never inferred from the storefront** (2026-08-14).
  Discovery hardcoded `HasSteamCloud: true` for anything installed through Steam. Only 14,340 of the
  manifest's 48,908 Steam-id titles actually have Steam Cloud, and the default Add Games view HIDES
  what is flagged — so the assumption made thousands of games the user owns invisible, with nothing
  backing them up. `ManifestLoader.HasSteamCloud` returns `bool?`; **`null` means "not in the
  manifest" and is not the same answer as "no"** — that case keeps the old assumption, which is what
  makes the change purely additive. This does not reopen the scope decision below: installed Steam
  games are still out of the *default view*, just no longer out of it for a reason that was false.
- **Conflict prevention: proactive lock/lease.** Server tracks a per-game checkout; the agent pulls
  before launch *where it has a real launch boundary* (see the next bullet); other machines are
  warned if leased elsewhere. Content-hash + parent-version lineage is the fallback detector.
- **Only Linux has a pre-launch pull; Windows refuses to restore under a running game** (WA-01,
  2026-07-27). `savelocker run -- %command%` runs *instead of* the game and starts it itself, so it
  restores with certainty that nothing has the save open. Windows has only `ProcessWatcher`, which
  polls every 4 s and therefore observes a game *after* it started and opened its saves — calling
  that "pre-launch" meant restoring underneath a live process, which the game then overwrote at
  exit, losing the pulled save silently. The Windows launch path now takes the lease and skips the
  pull. `GameActivity.IsActive` is the central refusal, enforced inside `SyncEngine.PullAsync`
  (twice — before download and again immediately before restore, since a game can launch during a
  long download) so no tray action, dashboard command, CLI call, or lifecycle callback can route
  around it. Each surface checks it too, only to word the reason. A game with no configured
  `ProcessNames` cannot be detected as running — that is what makes WA-08 a data-safety fix, not a
  convenience one. Adding a Windows launch wrapper would let the pull come back; nothing here
  precludes it.
- **Two tiers of save-path validation: absolute refusals and overridable warnings** (WA-02,
  2026-07-27). `SavePathGuard` is the hard floor — a drive root, a user profile (or the folder
  holding all of them), a Windows/system directory, Program Files, or the agent's own state or
  install directory can never be a save folder, and there is no override, because a force-pull
  *replaces* whatever it is pointed at. `SaveDirSanity` stays the heuristic tier (Wine prefix,
  repeated tail, over the upload cap): those have real false positives, so they are refused once and
  accepted on an explicit second confirmation (`confirm` on the local API, `--force-path` on the
  CLI). Paths are validated *and canonicalized* at all five entry points — folder picker, typed
  local API, enrollment, CLI, and server `MachineSavePath` reconciliation — and validated **again**
  inside `SyncEngine` immediately before archiving or restoring. The re-check is the load-bearing
  one: config.json is hand-editable, the server can push a path with no local confirmation, and a
  stored path can become a junction after it was accepted, so canonicalization resolves reparse
  points rather than trusting the stored string. The state directory is refused as
  same-or-ancestor only, not as a whole tree — `config.json` and `api-token` sit directly in it, and
  the test suites legitimately put save folders below it.
- **Windows agent state belongs to one account: whoever enrolled** (WA-03, 2026-07-27). Not a local
  group. `%PROGRAMDATA%` grants every authenticated user read access and it inherits, so
  `config.json` (this machine's **server API key**) and `api-token` (which grants the local
  management API) were readable by any local account. `StateDirSecurity` severs inheritance on the
  state directory and grants only the enrolling user, `SYSTEM`, and Administrators — the last two
  because an administrator can take ownership anyway, so removing them costs backup and repair
  while adding nothing. This matches the shape the installer already chose: it deliberately does
  **not** create `%PROGRAMDATA%\SaveLocker` while elevated, so the de-elevated tray user owns it
  (`installer/SaveLocker.iss`), and no installer change was needed. The ACL is applied to the
  *directory* with inheritable rules and enforced in `AtomicFile` — the one choke point every state
  writer already goes through — so a state file added later cannot quietly miss it. Failure to
  apply is logged loudly and is never fatal: an agent that refuses to start protects nothing.
- **`ApiKey` + `MachineId` + `ServerPin` are one identity, bound to an origin** (WA-04, 2026-07-27).
  Changing `ServerUrl` to a different origin **clears all three**, and the machine must register or
  enroll again. Keeping them meant presenting server A's live machine key to server B — B rejects it,
  but the credential has still been handed to a host that was never meant to see it, and A's stale
  pin makes B's first genuine TLS identity look like a mismatch. Sameness is judged on
  scheme+host+port (`ServerOrigin`), not the raw string, so a trailing slash or a change of case
  keeps the enrollment. Separately, a candidate URL is validated as an absolute http/https address
  **before** anything is mutated: the old code persisted the raw string and only then built a client
  from it, so `htp://typo` reached disk, returned 500, and crashed every subsequent start.
  Registration through the local UI now persists the observed TLS pin *with* the key and machine id
  in one write — it previously ignored `ApiClient.ObservedPin` entirely, so registering against an
  https server established an identity with no pin at all and no TOFU guarantee.
  <br>**Deliberately not done:** the candidate connection is not probed before committing. Validation
  alone satisfies "a typo cannot brick startup", and requiring a successful probe would make a
  server that is merely offline impossible to configure. A failed transition rolls back every field
  together instead.
- **An update is verified by digest, and downloads are origin-bound** (WA-05, 2026-07-27). The
  server computes a SHA-256 while storing an installer, publishes it on `/api/agent/latest`, and the
  agent refuses to run anything that does not match. This is the direct consequence of shipping no
  certificates: the transport proves nothing, so the digest is the *only* control over what gets
  executed. A download to the configured server's own origin may use the authenticated client; a
  download anywhere else gets **no credential, no pin, and is refused outright without a digest** —
  the old code accepted an arbitrary absolute `DownloadUrl` using a client whose default headers
  carried the machine key, handing this machine's credential to any host the server named. Also
  bounded: a unique temp file created with `CreateNew` (the old fixed `%TEMP%` name was predictable
  and pre-placeable by another local user), a 300 MB cap, deletion on every failure path, and an
  MZ-header check so an HTML error page cannot reach `Process.Start`. A connection change retires
  the checker and clears any cached result, and the origin is re-checked at the moment of launch.
  <br>An installer stored before digests existed is hashed once at startup (`BackfillDigestAsync`),
  so already-deployed servers keep working rather than serving something the agent will not verify.
  <br>**Known limit, stated plainly:** a digest delivered over the same unauthenticated channel as
  the artifact does not stop an attacker who can rewrite both. Authenticode verification is the
  check that would, because it does not depend on the channel — there is a marked hook for it in
  `UpdateChecker.VerifyLooksExecutable`, pending a decision on code signing.
- **The Linux agent stages an update while running and installs it at the next start** (2026-08-15).
  Not a mid-session restart, and not a prompt. Three things forced the shape and none of them are
  worth rediscovering:
  <br>**`systemctl --user stop` kills the unit's entire cgroup.** An updater the daemon spawned dies
  together with the unit it just stopped — halfway through replacing the agent's files. So the
  daemon only does what is safe while running (download, verify the digest, unpack, smoke-test) and
  the swap runs from `ExecStartPre` of the *next* invocation, a fresh process with the old daemon
  already gone. `savelocker update` may restart the service because it runs in the user's shell
  session, not in the unit; anything the daemon spawns must not.
  <br>**The install prefix IS the state directory** (`~/.local/share/SaveLocker`), so `config.json`
  — the machine's server API key — sits inside the tree an update replaces. Apply therefore copies
  file-by-file, touching only paths the tarball carries; renaming the tree away would take the
  enrollment with it. Splitting app files from XDG state stays a separate Backlog item, deliberately
  not done here.
  <br>**A package that unpacks perfectly can still be unable to run** — wrong architecture, a glibc
  newer than the device has, a truncation the server hashed too. So the staged binary is executed
  and must report the version the server offered before it is allowed to replace a working agent.
  Old files are *moved* aside, not deleted: a rename is free, gives the replacement a new inode (so a
  `savelocker run` wrapper mid-game keeps running from the copy it mapped), and leaves a complete
  previous version. `applied.json` surviving into the next start is the failure signal — the daemon
  clears it once it is genuinely up, so a build that cannot start is rolled back automatically and
  reported as `update.rolled_back`.
  <br>**Consent, stated plainly:** a Deck installs updates without asking, because it has no surface
  to ask on. What it does not do is change anything mid-session. `AutoUpdate: false` in `config.json`
  stops the staging while keeping the checks, so the console still says the machine is behind.
- **A `SyncEngine` has an explicit lifetime, and its leases belong to the origin that issued them**
  (WA-06, 2026-07-27). Replacing `_engine` used to drop the old one on the floor. Its lease timers
  are rooted by the runtime timer queue, so they kept renewing against the **old** server forever,
  while the game's exit ran through the **new** engine and released against the new server — which
  had never issued anything. The old server's lease was then held in perpetuity by a machine that
  had stopped talking to it, locking every other machine out of that game. The engine now captures
  its origin at construction (`_config` is shared and mutable, so it cannot answer "who issued my
  leases?"), and `RetireAsync` stops every renewer, cancels work in flight, and releases held leases
  **through the client that acquired them** before the caller moves on. Both hosts retire the engine
  they replace. A retired engine refuses new work, so the offline-queue drainer and folder watchers
  — which captured a reference before the change — cannot push to the previous server.
  <br>Renewal callbacks check retirement twice, before the request and after it: disposing a timer
  does not recall a callback already running, so without the second check a retired engine renews
  and reports success. Release now happens in a `finally`, because a push that throws must not also
  leak the lease — that left a game checked out until expiry with nobody playing it.
  <br>`SAVELOCKER_LEASE_RENEW_SECONDS` shortens the three-hour interval for tests only; the interval
  is the thing under test and nothing observable happens at the production value.
- **The cross-process lock fails closed** (WA-07, 2026-07-27). `AgentStateLock` used to time out after
  30 s and hand back an *unheld* handle so the caller proceeded — the precise state the lock exists
  to prevent. **The justification for that was factually wrong** and worth recording so it is not
  reinstated: it claimed a lock file left by a crashed process must not block syncing forever, but
  the lock is a *handle*, not the file's existence, and every OS releases handles when a process
  dies. A crashed agent leaves a stale lock *file* that the next caller opens without difficulty.
  There is no stale-lock scenario, so nothing was being bought by failing open.
  <br>The 30 s was also shorter than one normal operation — the settle gate alone may hold the lock
  for `SettleMaxWaitSeconds` (120 by default) and the upload's HTTP timeout is 10 minutes — so a
  *healthy* exit-push routinely outlasted the wait and the other process barged in. The game lock is
  now sized to what the holder could legitimately be doing: settle + upload window + margin, ~13
  minutes. Short state writes (config, queue, health, lease warnings) get 60 s.
  <br>Failure is a typed `AgentStateLockException`, reported as "another SaveLocker process is
  syncing this game" by the tray, the CLI (`Busy:`, not a stack trace) and the console event stream,
  and acquisition is cancellable so a retired engine does not sit waiting.
  <br>`SAVELOCKER_SYNC_LOCK_SECONDS` shortens the wait for tests only — sibling of
  `SAVELOCKER_LEASE_RENEW_SECONDS`; both are listed in Gotchas.md.
- **Process names are derived where they are known, and admitted as missing where they are not**
  (WA-08, 2026-07-27). A game enrolled through the UI never received `ProcessNames`, so
  `ProcessWatcher` excluded it outright — no lease, no push on quit, and (since WA-01) no refusal to
  overwrite saves while it is running. Only the CLI's `--proc` populated it. A **non-Steam shortcut**
  is the one source where the answer is known rather than guessed: Steam records the exact
  executable, and `GameActivity.ProcessNameFromExe` reduces it to what `Process.ProcessName`
  reports. An installed Steam game or a save-root match gets **null** — a folder name is not an
  executable name, and guessing would be worse than admitting ignorance, because a wrong name is
  indistinguishable from a game that is never running.
  <br>Where it is null the UI says "Launch/exit sync not configured" and offers to set it, rather
  than implying automatic sync works. The same normalisation runs at every entry point (local API,
  CLI `--proc`), so `C:\Games\Foo\foo.exe` and `foo.exe` both persist as `foo` — stored verbatim,
  neither would ever match.
- **One thread owns every WinForms object, and it is established rather than captured** (WA-09,
  2026-07-28). `TrayContext` used to capture `SynchronizationContext.Current` in its constructor —
  which runs as the **argument** to `Application.Run`, before the message loop installs
  `WindowsFormsSynchronizationContext`. The capture fell through to `new SynchronizationContext()`,
  so every `_ui.Post` in the tray was a plain thread-pool post: the menu rebuild, the balloon, the
  first-run prompt, and the ones WA-01, WA-05 and WA-06 added. Nothing failed loudly, because
  WinForms only throws on a cross-thread call once a handle exists and a `NotifyIcon` menu has none.
  <br>`UiDispatcher` replaces it: it creates a control and **forces its handle**, which is itself
  what installs the WinForms context on that thread. It therefore does not depend on where in the
  startup sequence it is constructed — it makes the owner instead of hoping to find one. Every
  WinForms touch from a background thread routes through `Post` (fire-and-forget) or `InvokeAsync`
  (the modal surfaces: message boxes, the folder dialog). The owner is logged on every start
  (`UI owner: thread N, context …`), because the defect was invisible precisely in that the wrong
  context still accepted every post.
  <br>Two consequences worth keeping: the folder picker no longer runs on a private STA thread —
  `Main` is already `[STAThread]`, so there was nothing for a second one to provide, and reading
  `Application.OpenForms` from it handed `ShowDialog` an owner belonging to another thread. And the
  live game list is copy-on-write everywhere (`AgentConfig.MutateGames`), so a poller adoption cannot
  throw "Collection was modified" out of a UI or render thread enumerating it.
  <br>`SAVELOCKER_TRAY_PORT` was added under the rule below so a harness can drive a real tray; it
  scopes the single-instance mutex too, so a test tray and the installed one coexist.
- **A requested route is queued until WebView2 can honour it** (WA-12, 2026-07-28). `OpenWindow`
  called `Navigate` before WebView2 existed. `CoreWebView2` is null until `EnsureCoreWebView2Async`
  completes, so the call was dropped on the floor and `OnLoad` then navigated to `/` regardless:
  accepting the first-run prompt asked for Settings and landed on Overview, on exactly the install
  where the user has the most to configure. The guard that was there (`IsHandleCreated`) tested the
  wrong thing — `Show()` creates the handle immediately, while WebView2 comes up long afterwards.
  <br>`AgentWindow` now holds a pending route: `Navigate` applies it if the core is ready and stores
  it otherwise, and initialization navigates to the stored route in preference to the home page. A
  later deep link replaces an earlier one, so what opens is the last thing asked for. The field is
  UI-thread-only by construction (every caller goes through the WA-09 dispatcher, and the
  initialization continuation resumes there), which is why it carries no lock.
- **Discovery is per-source best-effort; one bad source cannot fail the scan** (WA-11, 2026-07-28).
  The scanner reads places the agent does not control — Steam `userdata` directories owned by other
  Windows accounts, a library on a drive that can be unplugged mid-enumeration, a redirected or
  cloud-backed Documents folder. Only **parse** errors were caught, so any of those threw out of
  `ScanAsync` entirely: zero candidates, and the manual setup path the user would have used to work
  around it was gone too, since the UI reaches it through the same failed request.
  <br>Each source now runs inside its own boundary and is logged and skipped on failure. The
  enumeration guard is the part worth remembering: `Directory.EnumerateDirectories` and
  `EnumerateFiles` fail **lazily**, on the `MoveNext` that reaches the bad entry — a `try` around the
  call site catches nothing, so the enumerator is stepped explicitly and whatever was read before the
  failure is kept.
  <br>`OperationCanceledException` still propagates: a cancelled scan is the caller's own doing, not
  a broken source. Registry keys are opened with `using` rather than left to the finalizer.
- **A platform toggle reports the effective state, not the requested one** (WA-10, 2026-07-28).
  "Start with Windows" had two independent ways to lie. `/api/config` discarded
  `IAutoStart.SetEnabled`'s result and always answered ok, so a registry write refused by group
  policy still drew a ticked box; and `IsEnabled` accepted **any** non-empty Run value, so an entry
  left by an install that has moved or been uninstalled read as enabled while Windows launched
  nothing at login.
  <br>Now: `IsEnabled` is true only when the Run entry resolves to **this** executable (canonical
  path, must exist). `SetEnabled` returns an `AutoStartResult` carrying a reason a person can act on,
  and **reads the entry back** — `SetValue` not throwing is not the same as the value being there,
  since policy and endpoint-protection products both virtualise and revert this key. The API applies
  the toggle **first**, so a refusal costs nothing and returns 400 with the reason, and it echoes the
  re-read effective state so the checkbox can correct itself.
  <br>The same contract covers Linux: `SystemdAutoStart` distinguishes "systemctl failed" from
  "could not run systemctl at all", which on a Deck reached over SSH means no user session bus — a
  different problem with a different fix.
  <br>`SAVELOCKER_RUNKEY_SUBPATH` (test-only, below) redirects the Run key so the access-denied
  branch can be tested with a real Deny ACE. Applying one to the real
  `…\CurrentVersion\Run` of a working machine to prove a point is not an acceptable test.
- **Test-only environment variables stay, and stay unadvertised** (2026-07-28, maintainer decision).
  `SAVELOCKER_LEASE_RENEW_SECONDS`, `SAVELOCKER_SYNC_LOCK_SECONDS`, `SAVELOCKER_TRAY_PORT`,
  `SAVELOCKER_RUNKEY_SUBPATH` and `SAVELOCKER_STATE_ROOT` are kept — they are the only
  way to observe a 3-hour renewal interval or a ~13-minute lock wait inside a test — but they are
  **not** promoted to `AgentConfig` settings and are **not** documented anywhere a user reads. They
  live in `Gotchas.md` (this vault) only; `web/src/help/cli-reference.md` and the release notes must
  not mention them. Anything added in the same spirit follows the same rule: read silently, default
  to the production value, clamp the override, and document it here rather than in the KB.
- **Stack: single-language .NET.** Agent = C#/WinForms (Windows), C#/headless (Linux); Server =
  ASP.NET Core in Docker on unRAID.
- **Runtime: .NET 10 (LTS)**, locked 2026-07-13. .NET 9 is STS, EOL 2026-11-10; .NET 10 is LTS to
  2028-11-14. `global.json` pins the SDK — bump it and the Dockerfile's `sdk:`/`aspnet:` tags
  together.
- **unRAID as hub, not peer-to-peer.** Async decoupling (offline machines sync later), single
  source of truth for conflict resolution, versioned history in one place. Rejected raw Syncthing
  (continuous sync risks mid-write copies; conflict files unusable for binary saves).
- **Dashboard auth:** `AdminPasswordFilter` + PBKDF2-SHA256. Cloudflare Access/Google SSO deferred
  — blocked by the Tunnel's 100 MB file limit conflicting with large save archives.
- **Plain HTTP is the default and supported configuration; TLS is bring-your-own** (2026-07-27,
  maintainer decision). SaveLocker ships no certificates, no ACME client, and no self-signed
  generation, and it will not nag about running over http. A user who wants TLS supplies their own
  cert (reverse proxy or Kestrel config) and the agent then pins it on first use (`ServerTrust`,
  §4). The product is a self-hosted LAN service on unRAID; the threat model is the household
  network, not the open internet. Nothing may force, redirect to, or require https — no
  `UseHttpsRedirection`, no HSTS, and no code path that treats an http server as misconfigured.
  <br>**The consequence that matters, and it is not a small one:** on the default configuration the
  transport provides *no* integrity guarantee. Anything the agent downloads and then **executes** —
  the auto-update installer above all — cannot lean on TLS to prove it is genuine. Integrity has to
  be carried in the payload's own verification (a digest published in the update metadata, and
  Authenticode once releases are signed), and that check is then the *only* control, not a
  belt-and-braces addition to a secure channel. See WA-05.
  <br>Be honest about the limit: a digest delivered over the same unauthenticated channel as the
  artifact does not stop an attacker who can rewrite both. It does stop a corrupted, truncated,
  wrong, or substituted-at-the-download-host payload, and combined with restricting downloads to the
  configured server's own origin it stops the agent being redirected to an arbitrary host. Full
  protection against an on-path attacker requires the user to supply a cert — which is exactly the
  trade this decision accepts.
- **Enrollment model:** a game is defined once on the server; each agent maps its own local save
  dir. The server game is the single definition; scanners only suggest candidates.
- **"Latest" = `Game.HeadVersionId`.** UI label "Latest"; admin action "Set as Latest".
- **Artwork:** SteamGridDB images are downloaded/cached server-side, not stored as bare URLs
  (offline-safe, survives upstream changes).
- **Product name: SaveLocker.** Rename complete 2026-07-10 — config dir, mutex, registry key,
  DB path, namespaces, solution/project files. Existing Docker deployments may still have
  `/data/localgamesync.db`; rename or set `Storage__DbPath`.
- **Installer: Inno Setup 6**, not WiX/MSIX. MSIX's virtualization would interfere with reading
  the Steam registry + arbitrary save folders. Machine-wide install, UAC up front. Uninstall
  prompts before deleting `%PROGRAMDATA%\SaveLocker` (API key + config).
- **Linux agent scope: non-Steam Windows games run under Proton only** (v1). Steam-bought games
  were assumed to already have Steam Cloud — **most do not** (see the Steam Cloud bullet above;
  the scope call stands, its stated premise does not). Native Linux game builds are explicitly out
  of scope (would need a
  save-variant model — different formats/paths/line-endings per platform). A Proton save is a
  Windows save, byte-identical to a Windows PC's — existing content-hash lineage works with zero
  server schema change. **Never sync a native-Linux save into a Windows install.**
  <br>**Revisit requested 2026-08-19** (maintainer: willing to accept per-game uncertainty on restore
  in exchange for being able to try it at all — "if it doesn't [work] I have my OS save already
  there"). The scope call itself still stands until the save-variant model exists; a phased plan for
  building that model — the missing piece named above — is scoped in [[Backlog]] → *Native Linux save
  support*. The one part of this decision that plan must not weaken: a Windows/Proton save and a
  native-Linux save of the same game must never share a version lineage, however the UI presents them.
- **Linux discovery:** `shortcuts.vdf` (non-Steam shortcuts), Heroic's library files, **and — since
  2026-08-10 — `libraryfolders.vdf`/`*.acf` (installed Steam games)**. Steam's shortcut AppID is
  signed in the VDF but the `compatdata/<id>/` folder name is unsigned —
  `SteamShortcuts.CompatDataId()` is the one place that converts.
  <br>**Reversal, deliberate.** Installed Steam games were originally out of scope because Steam
  Cloud already covers them. That reasoning justifies a default view, not an absence: the agent UI
  filters what the scan RETURNS, so a scan that returns nothing leaves "hidden by default" and
  "never discovered" indistinguishable from the couch — and only one of those can be undone by the
  user. Windows had always scanned them and flagged `HasSteamCloud`; Linux now matches. They stay
  out of the default view, one filter click away — and **since 2026-08-14 only the ones the manifest
  says are genuinely cloud-synced are flagged at all**, which is most of the point: the default view
  was hiding games nothing was backing up.
  <br>An installed game's prefix is `compatdata/<appid>` **in the library it is installed in**, not
  in the main Steam root where every shortcut's prefix goes — so libraries and prefixes are walked
  together, per library. A Deck's SD card is exactly this case, and getting it wrong finds the game
  and never finds its saves.
  <br>**A compat tool is identified by `toolmanifest.vdf` in its install directory, never by appid**
  (found on a Deck, 2026-08-10). The first cut filtered a hardcoded list of five appids and the Deck
  offered Proton 9.0/10.0/11.0, Proton Hotfix and Steam Linux Runtime 4.0 as enrollable games —
  Valve mints a new appid per Proton release, so any such list is stale the day it is written. Both
  scanners use the marker file; the appid list survives only for Steamworks Common Redistributables,
  which ships no toolmanifest. Pinned by a fixture whose appid is deliberately unguessable.
  <br>**A MoonDeck (Decky streaming plugin) shortcut's own AppID never gets a prefix either**
  (2026-08-19, `logs/2026-08-19_moondeck-save-detection.md`) — its `Exe` launches MoonDeck's own
  streaming client, never the game, so Steam has nothing to make compatdata for. When the same title
  is (or was) ALSO installed locally under Proton, the real AppID is in `LaunchOptions`
  (`MOONDECK_STEAM_APP_ID=`), and that is the prefix any local save data lives in — found via
  Cyberpunk 2077 on the maintainer's own Deck, where the Steam-Store copy had since been uninstalled
  but its compatdata (and real save data, 9 days old) was still there. `SteamShortcuts.MoonDeckAppId`
  carries it; `LinuxGameScanner` tries it only after the shortcut's own prefix fails to resolve, with
  `installDir` left null since `StartDir` here is MoonDeck's script directory, not the game's.
  <br>**An installed game's `&lt;root&gt;` is always the caller's verified Steam root, never derived
  from whichever compatdata path is being tried** (same 2026-08-19 session). Steam Cloud's own sync
  folder (`&lt;root&gt;/userdata/&lt;storeUserId&gt;/&lt;appid&gt;/remote`) exists exactly once, at
  the true Steam root, regardless of which library holds a game's files or compatdata — deriving
  `&lt;root&gt;` from the prefix path instead (the general-purpose `SteamLayout.RootFromCompatData`)
  silently pointed it at a secondary library with no `userdata` at all. Found via Left 4 Dead 2, whose
  compatdata does not exist anywhere on the maintainer's Deck at all — resolution is now attempted
  even then, since a `&lt;root&gt;`-anchored template needs no prefix to exist.
  <br>**A relocated install (most commonly internal storage → SD card) can leave a fresh, USELESS
  prefix at the new location while the original, working one stays in the main root** — Steam
  re-creates a prefix there rather than moving the old one, and the fresh one's special folders can be
  auto-created with different casing than an existing save history expects (Borderlands 2:
  `My games` where the manifest and the original prefix both say `My Games`; Linux is
  case-sensitive). The fallback to the main root's own compatdata is therefore tried whenever the
  current library's prefix resolves NOTHING, not only when the directory is missing outright — a
  present-but-unusable prefix looks identical to an absent one from outside, and the first cut of this
  fix (checking only `Directory.Exists`) missed exactly this shape.
  <br>**A MoonDeck shortcut's real AppID is reclassified as `SteamInstalled`, not `SteamShortcut`,
  when Steam's own `libraryfolders.vdf` — specifically its per-library `apps` block — says the AppID
  is installed somewhere** (`SteamRoots.AllKnownInstalledAppIds`, same session). A resolved compatdata
  prefix can never justify this by itself: Steam does not clean up compatdata on uninstall, so a real
  prefix full of real save data is equally consistent with "genuinely installed, on media not
  reachable right now" and "was installed once, uninstalled since, orphaned data left behind." The
  `apps` block is the one place that says which — and it survives its own library's media being
  absent, because it lives in the SAME file on the always-reachable main root, not on the card
  itself. Found via Cyberpunk 2077 a second time: genuinely Steam-installed on a card labelled "SD2"
  that stayed unmounted throughout, reclassified correctly with no card swap needed at all.
- **Linux UI: headless daemon serving the existing React UI** on `:5178` (Desktop Mode = KDE +
  browser). **Game Mode has no browser**, so `savelocker ui` (SDL + Dear ImGui, in-process against
  `Agent.Core`, no second API client) covers Status/Add game/Set folder/Launch setup as a gamepad
  view — not a second frontend. Themed to the console's exact palette (`Ui/Theme.cs` mirrors
  `web/src/index.css`). 60 fps cap (was 30; this is a menu, not a running game — VSync bounds it).
  Rejected: Flatpak+WebKitGTK (665 MB+), Godot (needs ≤net9), Avalonia (still hand-built nav),
  `steam://openurl` (Game Mode won't open a browser on request). Detail:
  `logs/2026-07-24_linux-agent-streamline.md`.
- **Launch trigger: the Steam wrapper (`savelocker run %command%`), not process polling.**
  Gives exact prefix path + precise hooks via `STEAM_COMPAT_DATA_PATH`/`SteamAppId`. Process-name
  polling is the fallback for non-Steam launchers only (Lutris/Heroic/Bottles) — `/proc/pid/comm`
  truncation and Proton wrapper processes make it unpleasant.
- **Enrollment token is short-lived and unsigned**, not a long-lived API key. Redeemed once for
  the real machine key; expires ~15 min. Deliberately unsigned — the threat is a forged file
  pointing at a malicious server, which signing can't fix (no trust anchor exists before the
  user's own download). Mitigated by HTTPS + hardened restore path + TOFU pin after enrollment.
- **Linux install: `~/.local/share/SaveLocker` + `systemd --user`**, never `/usr` — SteamOS's
  rootfs is immutable and wiped on update. Self-contained publish (SteamOS ships no .NET runtime).
- **Linux dev: WSL2 (Ubuntu 24.04 LTS) on ext4**, not a VM or Arch. Reproduces everything that
  matters (FileShare/inotify/proc/systemd/case-sensitivity) and gives CI parity (`ubuntu-latest`
  is Ubuntu 24.04). Never build from `/mnt/*`. Release job builds on `ubuntu-latest` for the
  oldest-glibc guarantee (package floor is glibc 2.27 either way — .NET's native libs are
  prebuilt by Microsoft, not compiled against the build host).
- **Agent local API: loopback-only, token-authenticated, never serves the machine key.**
  `AgentApiServer` can re-point a machine at another server — reaching it is equivalent to owning
  the box. `--lan` is withdrawn and exits non-zero. Remote access is an SSH tunnel. Full rationale
  in git history if ever revisited.
- **Config/queue/health files are cross-process shared state**, not single-owner. Daemon +
  launch-wrapper are separate processes sharing `config.json` etc. — per-game `flock` +
  in-process semaphore both required, every write atomic (temp+rename), reads happen under the
  lock immediately before use (not just before writing).
- **Untracking a game is a per-machine opt-out, not a local delete.** `AgentConfig.UntrackedGameIds`
  records that this machine does not sync a server game; the game stays on the server for the rest
  of the fleet. A plain `Games.RemoveAll` does not stick — the game is still on the server, so the
  daemon's next reconcile adopts it back, and any stale in-memory writer rewrites the entry. So the
  opt-out is enforced in the primitive, not by convention: `Save()` drops anything on the list,
  `SaveGameSyncState` never re-adds an entry absent from disk, and `CommandPoller` skips adoption.
  `add-game` / enrollment clear the opt-out — explicitly adding a game back is the only "track this
  here again". Untrack from the CLI with `remove-game --name`.
- **Connection-affecting config changes must rebuild the host's `SyncEngine`.** `SyncEngine` caches
  an `ApiClient` (base URL + key + pin). `CommandPoller` builds a fresh client per tick, so a server
  URL change moved control traffic immediately while watcher pushes and queue drains kept hitting
  the old host — split-brain with no error anywhere. `AgentApiServer` fires `onConnectionChanged`
  before the settings response returns; both hosts rebuild there.
- **A pulled archive is hostile input.** No destination write may traverse a symlink below the
  save root (the root itself is followed — it's user-chosen; paths *inside* the archive are not).
  Size caps (100k entries / 2 GB), checked against both declared and actual bytes. A refused
  restore is reported to the console as an event.
- **LAN-only over plain HTTP. No tunnel, no TLS termination, no reverse proxy** (2026-07-27). The
  payload is save files, not credentials or PII, and the earlier Cloudflare Tunnel plan is dropped.
  So `X-Forwarded-*` is never read and no trusted-proxy configuration exists — there is nothing in
  front to trust. The one URL the server hands to *other machines* (enrollment policy, hosted
  installer link) comes from `PublicUrl.For`: `Server:PublicBaseUrl` if configured, else the request
  origin. Minting is **refused when an inferred URL is loopback**, because a console opened at
  `http://localhost:5080` on the server box otherwise mints a file telling the new machine to sync
  with itself. A loopback URL that was *stated* — typed into the override, or put in
  `Server:PublicBaseUrl` — is honoured: agent and server on one box is a real setup, and it is what
  both enrollment suites do. `Server:PublicBaseUrl` is also the single knob to set if a proxy is
  ever added.
- **Lease writes are single statements; losing a race is an answer, not an error.** Acquisition is a
  conditional `UPDATE` (take over a row that is mine or expired) falling back to an `INSERT` whose
  unique `GameId` index arbitrates — the losing caller catches the constraint violation and returns
  `Granted=false` with the holder's lease. Read-then-insert gave one of two simultaneous launches a
  500 at exactly the moment leasing is the thing being relied on. Release, renew, force-release and
  the expiry sweep are conditional statements for the same reason, and the sweep can no longer
  delete a lease renewed since it was selected. `ActiveLeaseAsync` is a pure read: it used to delete
  expired rows, which made `GET /api/overview` a write that two parallel dashboard requests could
  collide on.
- **Every server-authoritative head change fans out; an ordinary push does not.** One code path
  (`SetHeadAndPropagateAsync`) moves `HeadVersionId`. Where the *server* decided — automatic conflict
  policy, manual resolution, rollback, Set as Latest — it queues a deduplicated **unforced** Pull for
  every live machine that syncs the game (mapped save path or uploaded version), skipping the
  uploader that already learned the head from its own response. An agent's parent advances only on
  its own push or pull, so without this the fleet stays on the old parent and re-conflicts on its
  next save — the console said resolved while the machines disagreed. An ordinary push deliberately
  does **not** fan out: the other machines are not displaced by a decision, and a command arriving
  mid-session is the failure the lease and settle gate exist to prevent. Unforced is the safety
  property: unsynced local work reports *blocked* rather than being overwritten (only the console's
  own Pull button forces).
- **Set as Latest / rollback supersedes the conflicts it decides, and only those.** A conflict
  offering the chosen version is resolved in its favour — leaving it open makes the console insist
  the version it was just told to trust is unresolved. A conflict between two *other* versions stays
  open: the admin has said nothing about it, its machine is still stuck, and closing it would also
  disarm the rule that resolution may not rewind a newer Latest. Each supersession is audited as
  `conflict.resolve_superseded`.
- **A credential is only consumed once the thing it buys has been issued.** The SteamGridDB key is
  verified *before* it replaces the stored one, and a rejection is a 4xx — it used to store first,
  ask second, and answer 200 with `{ ok: false }`, so a typo overwrote a working key while the
  console reported success. Enrollment burns its single-use token and issues the machine key inside
  **one transaction**; anything that throws rolls the burn back, so a file is never spent for
  nothing. The single-winner guarantee is unchanged: the burn is still a conditional `UPDATE`, and
  a second redeemer matches zero rows.
- **The SteamGridDB probe must hit an authenticated endpoint.** `search/autocomplete` is served
  *without* a key — it returned 200 with real Celeste data for 25 characters of nonsense, so
  "API key verified" only ever meant the site was reachable. `grids/game/{id}` answers 401 without
  a valid key, which is the question being asked.
- **The hosted installer is staged, published, then the old one is removed — under one gate.**
  `AgentInstallerService` is a singleton holding a `SemaphoreSlim` that manual upload, manual GitHub
  fetch, the background poller and delete all pass through; the fetch holds it across its
  "is this newer?" read too, since that decision is read-then-write. Validation (`.exe`, a usable
  filename, a parseable version) happens **before** anything on disk is touched: the old code
  accepted a 1 KB `notes.txt` with version `not-a-version` and served it to the fleet as the
  installer. The request body is capped at `AgentUpdate:MaxInstallerMb` (200) rather than having
  Kestrel's limit removed outright — `ReadFormAsync` buffers to memory and temp storage, and the
  route is open until an admin password is set.
- **An archive is staged, then published; rows are deleted before files.** Uploads land in
  `{ArchiveRoot}/.incoming/*.part` (same volume, so the publish is one rename) and are size-capped
  while copying, not on declared length — a disconnect used to leave a truncated archive sitting at
  the exact path a `SaveVersion` row would name. Deletion runs the other way round: commit the DB,
  then delete the file, because an orphaned file is wasted space while a live row whose archive is
  gone is a save the console offers and cannot produce. Failed deletes are audited as
  `archive.orphaned` rather than ignored; staging is swept at startup (1 h age floor).
- **Command delivery is at-least-once, under a visibility lease.** A claimed `AgentCommand` is
  Dispatched with a `LeaseExpiresAt` + `ClaimToken`, written by one atomic UPDATE so two pollers
  sharing a machine identity cannot both receive it; when the lease expires unacknowledged the
  command is claimable again (`ClaimCount` records the redelivery, and a reclaim is audited).
  At-most-once was the old behaviour and it silently lost any command whose agent died between the
  poll and the result. Retrying is safe for every type — Pull re-reads the head, Push re-hashes and
  the server answers `NoChange` on identical content, Sync is both, Scan only reads — so a lost
  request is the worse failure. Terminal states stick: a late duplicate result is a no-op.
- **Conflict pressure caps at 3 rejected payloads**, then further pushes report the condition
  without re-uploading. 6-hour overdue threshold surfaces stale conflicts to a connected agent.
  "Keep both" doesn't create two heads — the chosen snapshot becomes Latest, both conflicting
  snapshots get `Protected` (exempt from retention) until explicitly unprotected. Resolution
  refuses to promote something older than the current head.

## Environment facts (user-provided)
- Games are standalone builds, not bought on Steam/Epic → manifest-based detection + manual
  `--dir` fallback is the primary path, not a fallback, on Linux.
- Sync trigger: hybrid (automatic background + manual override).
