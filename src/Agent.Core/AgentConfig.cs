using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveLocker.Agent;

/// <summary>
/// Per-machine agent configuration, persisted as JSON. Default location is
/// %PROGRAMDATA%\SaveLocker\config.json, overridable via --config for tests
/// (lets two "machines" run side by side against one server).
/// </summary>
public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "http://localhost:5179";
    public string MachineName { get; set; } = Environment.MachineName;
    public string? ApiKey { get; set; }
    public Guid? MachineId { get; set; }
    /// <summary>
    /// TOFU pin of the server's TLS public key, recorded at enrollment. Null over plain http, or
    /// for agents registered before enrollment existed. See <see cref="ServerTrust"/>.
    /// </summary>
    public string? ServerPin { get; set; }
    public string ManifestCachePath { get; set; } =
        Path.Combine(DefaultDir, "manifest.yaml");
    /// <summary>Set once the first-run welcome prompt has been shown/dismissed so we don't nag.</summary>
    public bool FirstRunCompleted { get; set; }
    public List<TrackedGame> Games { get; set; } = new();
    /// <summary>
    /// Server games this machine has deliberately stopped tracking. Removing a game locally is not
    /// enough on its own: the game still exists on the server, so the next
    /// <c>CommandPoller.ReconcileGamesAsync</c> adopts it straight back and the removal reads as a
    /// bug. This is the per-machine opt-out that makes "removed from this device" mean it — the
    /// game stays on the server for every other machine. Enrolling or adding the game here clears
    /// the opt-out. Written only by <see cref="SetTracked"/>; <see cref="Save"/> carries it through
    /// from disk untouched, for the same lost-update reason as the per-game sync state.
    /// </summary>
    public List<Guid> UntrackedGameIds { get; set; } = new();
    /// <summary>Cumulative count of save versions successfully pushed to the server.</summary>
    public int TotalSavesPushed { get; set; }
    /// <summary>UTC timestamp of the most recent push or pull across all games.</summary>
    public DateTime? LastSyncTime { get; set; }
    /// <summary>Version string the user chose to skip ("Skip This Version"); suppresses update prompts for that version.</summary>
    public string? SkipVersion { get; set; }
    /// <summary>UTC timestamp of the last update check; used to enforce a 24 h cooldown between background checks.</summary>
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>
    /// Whether the Linux agent may prepare a newer version by itself. True by default: a headless
    /// device that never updates is one nobody notices is stale.
    /// <para>
    /// Turning it off stops the agent <b>staging</b> anything. It keeps checking, so `doctor`, the
    /// agent UI and the console still say when the machine is behind — the point is to choose when
    /// the files change, not to stop knowing. `savelocker update` ignores this: it is someone asking
    /// for the update in as many words.
    /// </para>
    /// </summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>
    /// Seconds a save folder must be quiet — no file changes, nothing open for writing — before an
    /// automatic push archives it. Games that keep flushing after exit need a longer wait; 0 disables
    /// the gate and archives immediately (fast, but can capture a half-written save).
    /// </summary>
    public int SettleQuietSeconds { get; set; } = 10;
    /// <summary>Hard cap on the settle wait. Past this the push proceeds regardless, so a game that
    /// never goes quiet can't block syncing forever.</summary>
    public int SettleMaxWaitSeconds { get; set; } = 120;

    /// <summary>Mute the Game Mode UI's interface sounds (`savelocker ui`). Nothing else uses audio.</summary>
    public bool UiSoundsMuted { get; set; }

    [JsonIgnore] public string ConfigPath { get; private set; } = DefaultConfigPath;

    /// <summary>
    /// Agent state root. Windows keeps the machine-wide %PROGRAMDATA%\SaveLocker.
    /// Linux must NOT: SpecialFolder.CommonApplicationData maps to /usr/share there, which is
    /// not user-writable and — on SteamOS — is the immutable rootfs, wiped on every update.
    /// State goes in the user's XDG data dir instead (Decisions.md §5).
    /// </summary>
    public static string DefaultDir => Path.Combine(StateRoot(), "SaveLocker");

    private static string StateRoot()
    {
        // Test-only, deliberately unadvertised (Decisions.md — "kept for testing, never documented
        // for users"). Linux already has XDG_DATA_HOME for this; Windows had nothing, so a test
        // agent's --config moved config.json and left agent.log, the WebView2 profile and the
        // manifest cache in the installed agent's directory. Must be a rooted path — a relative one
        // would resolve against whatever directory the process happened to start in, which for a
        // tray launched from Explorer is not knowable.
        var overridden = Environment.GetEnvironmentVariable("SAVELOCKER_STATE_ROOT");
        if (!string.IsNullOrWhiteSpace(overridden) && Path.IsPathRooted(overridden))
            return overridden;

        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return xdg;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share");
    }

    public static string DefaultConfigPath => Path.Combine(DefaultDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AgentConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        if (!File.Exists(path))
        {
            var fresh = new AgentConfig { ConfigPath = path };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            fresh.Save();
            return fresh;
        }
        var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOpts)
                  ?? new AgentConfig();
        cfg.ConfigPath = path;
        return cfg;
    }

    [JsonIgnore] public string StateDir => Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!;

    /// <summary>Deserialize the config as it currently is on disk, or null if absent/unreadable.</summary>
    private AgentConfig? ReadOnDisk()
    {
        try
        {
            return File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath), JsonOpts)
                : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Write settings and the game list. Atomic, and serialized against other processes.
    /// <para>
    /// <b>Per-game sync bookkeeping is never written here</b> — <see cref="SaveGameSyncState"/> owns
    /// it, and this method carries whatever is on disk through untouched. Every caller of this one is
    /// persisting settings or the game list (server URL, machine name, API key, a save folder, a
    /// removed game); none of them holds a newer parent version than the file does.
    /// </para>
    /// <para>
    /// That used to be a rule stated here and left to callers to remember, and
    /// <c>CommandPoller.ReconcileGamesAsync</c> did not: the daemon and the tray hold this object for
    /// their entire lifetime, so on any game-list change it wrote the parent version that was current
    /// <i>at boot</i> over the one the launch wrapper had just recorded — and the WRAPPER's next push
    /// was then rejected as a conflict. Same lost update <see cref="SaveGameSyncState"/> exists to
    /// prevent (<c>Decisions.md</c> §8), reached through a different door. A rule 17 call sites must
    /// remember is not a rule, so the primitive is now safe instead.
    /// </para>
    /// <para>
    /// Refreshing the in-memory objects from disk is deliberate, not incidental: it converges this
    /// process's view toward the file rather than away from it, so a long-lived host that saves a
    /// setting also stops being stale.
    /// </para>
    /// <para>
    /// <b><see cref="TrackedGame.SaveDirectory"/> is the caller's to write.</b> It is reconciled from
    /// the server, which is its highest authority, and set directly by the agent UI and the CLI — all
    /// of which route through here. It is deliberately not preserved from disk.
    /// </para>
    /// </summary>
    public void Save()
    {
        using var guard = AgentStateLock.Acquire("config", StateDir);

        var onDisk = ReadOnDisk();
        if (onDisk is not null)
        {
            foreach (var game in Games)
            {
                // Absent on disk means this game is new here, so in-memory (null) is already right.
                var stored = onDisk.Games.FirstOrDefault(g => g.GameId == game.GameId);
                if (stored is null) continue;
                game.LastKnownVersionId = stored.LastKnownVersionId;
                game.LastSyncedHash = stored.LastSyncedHash;
                game.ConsecutiveConflicts = stored.ConsecutiveConflicts;
            }
            TotalSavesPushed = onDisk.TotalSavesPushed;
            LastSyncTime = onDisk.LastSyncTime;
            // Same reasoning as the sync state above: SetTracked owns this list, and a host that
            // loaded its config at boot must not write a stale copy over another process's opt-out.
            UntrackedGameIds = onDisk.UntrackedGameIds;

            // And the opt-out has to be ENFORCED here, not just carried. A long-lived host loaded
            // its game list at boot and holds it for the process lifetime, so after another process
            // untracks a game this one still has it in memory — and the next Save() from any of the
            // 17 call sites writes it straight back. Making the primitive honour the list means no
            // caller has to know: an untracked game cannot be resurrected by a stale writer.
            MutateGames(list => list.RemoveAll(g => UntrackedGameIds.Contains(g.GameId)));
        }

        AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts),
            restrictPermissions: true);
    }

    /// <summary>The identity fields a server issued, captured so a failed transition can restore them.</summary>
    public readonly record struct ServerIdentity(string ServerUrl, string? ApiKey, Guid? MachineId, string? ServerPin);

    /// <summary>A snapshot of the current connection identity, for rollback.</summary>
    public ServerIdentity CaptureIdentity() => new(ServerUrl, ApiKey, MachineId, ServerPin);

    /// <summary>Put back a captured identity, in memory only — the caller decides whether to persist.</summary>
    public void RestoreIdentity(ServerIdentity id)
    {
        ServerUrl = id.ServerUrl;
        ApiKey = id.ApiKey;
        MachineId = id.MachineId;
        ServerPin = id.ServerPin;
    }

    /// <summary>
    /// Point this agent at a server, in memory. Returns false — changing nothing — if the URL is not
    /// an absolute http/https address, so a typo can never reach disk and brick the next startup.
    /// <para>
    /// <paramref name="identityCleared"/> reports the security-relevant half: when the origin
    /// actually changes, the machine key, machine id and TLS pin are <b>dropped</b>, because all
    /// three were issued by the previous server and mean nothing to the new one. Sending them on
    /// would hand a live credential to a host that was never meant to see it (WA-04). The user
    /// re-registers or re-enrolls against the new origin. A cosmetic edit — a trailing slash, a
    /// change of case — is not an origin change and keeps the enrollment.
    /// </para>
    /// </summary>
    public bool TrySetServerUrl(string? url, out bool identityCleared)
    {
        identityCleared = false;
        if (ServerOrigin.CanonicalUrl(url) is not { } canonical) return false;

        if (!ServerOrigin.Same(ServerUrl, canonical))
        {
            ApiKey = null;
            MachineId = null;
            ServerPin = null;
            identityCleared = true;
        }

        ServerUrl = canonical;
        return true;
    }

    /// <summary>
    /// Change one or more scalar settings without writing this process's whole view of the config.
    ///
    /// <see cref="Save"/> is the right primitive for a process that owns the game list — the daemon
    /// and the tray, which reconcile it. <c>savelocker ui</c> owns none of it: it is a separate,
    /// long-lived process that loads <c>config.json</c> at startup and never reloads it, so a
    /// <see cref="Save"/> to toggle interface sounds serialized a game list minutes stale and undid
    /// whatever the daemon had reconciled in between. Nothing in the write said so.
    ///
    /// <para>
    /// So: re-read under the lock, apply <paramref name="apply"/> to the <b>fresh</b> state, write
    /// that, and converge this object onto the result — the caller's view ends up matching the file
    /// rather than drifting further from it.
    /// </para>
    /// </summary>
    public void UpdateSettings(Action<AgentConfig> apply)
    {
        using var guard = AgentStateLock.Acquire("config", StateDir);

        var fresh = ReadOnDisk();
        if (fresh is null)
        {
            // Nothing on disk to merge with — our own copy is the best truth available.
            apply(this);
            AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts),
                restrictPermissions: true);
            return;
        }

        fresh.ConfigPath = ConfigPath;
        apply(fresh);

        AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(fresh, JsonOpts),
            restrictPermissions: true);

        ServerUrl = fresh.ServerUrl;
        MachineName = fresh.MachineName;
        ApiKey = fresh.ApiKey;
        MachineId = fresh.MachineId;
        ServerPin = fresh.ServerPin;
        FirstRunCompleted = fresh.FirstRunCompleted;
        SettleQuietSeconds = fresh.SettleQuietSeconds;
        SettleMaxWaitSeconds = fresh.SettleMaxWaitSeconds;
        UiSoundsMuted = fresh.UiSoundsMuted;
        TotalSavesPushed = fresh.TotalSavesPushed;
        LastSyncTime = fresh.LastSyncTime;
        Games = fresh.Games;
        UntrackedGameIds = fresh.UntrackedGameIds;
    }

    /// <summary>Is this server game one this machine has opted out of tracking?</summary>
    public bool IsUntracked(Guid gameId) => UntrackedGameIds.Contains(gameId);

    /// <summary>
    /// Record that this machine does — or does not — track a server game, and add or drop the local
    /// entry to match. Field-level and re-read under the lock, so a concurrent daemon save cannot
    /// undo it: untracking a game from the Game Mode UI while the daemon is running has to survive
    /// both the daemon's next <see cref="Save"/> and its next reconcile.
    /// </summary>
    public void SetTracked(Guid gameId, bool tracked, TrackedGame? entry = null)
    {
        using var guard = AgentStateLock.Acquire("config", StateDir);

        var onDisk = ReadOnDisk() ?? this;
        onDisk.ConfigPath = ConfigPath;

        if (tracked)
        {
            onDisk.UntrackedGameIds.RemoveAll(id => id == gameId);
            if (entry is not null && onDisk.Games.All(g => g.GameId != gameId))
                onDisk.Games.Add(entry);
        }
        else
        {
            if (!onDisk.UntrackedGameIds.Contains(gameId)) onDisk.UntrackedGameIds.Add(gameId);
            onDisk.Games.RemoveAll(g => g.GameId == gameId);
        }

        AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(onDisk, JsonOpts),
            restrictPermissions: true);

        // Converge this process's view on what we just wrote — the caller's next Save() must not
        // reintroduce the entry we removed.
        UntrackedGameIds = onDisk.UntrackedGameIds;
        MutateGames(list =>
        {
            if (!tracked) list.RemoveAll(g => g.GameId == gameId);
            else if (entry is not null && list.All(g => g.GameId != gameId)) list.Add(entry);
        });
    }

    /// <summary>
    /// Change the in-memory game list <b>copy-on-write</b>: mutate a copy, then publish it as one
    /// reference assignment.
    ///
    /// <para>
    /// Enrollment runs on a thread-pool continuation while a render loop or a UI thread is
    /// enumerating this same list — <c>savelocker ui</c> calls <see cref="FindGame"/> for every
    /// candidate row, every frame. Mutating the live <see cref="List{T}"/> under that throws
    /// "Collection was modified" out of the render loop and takes the window down mid-enrollment.
    /// A reader holding the old reference simply finishes its frame against a consistent snapshot
    /// and picks up the new list on the next one.
    /// </para>
    /// </summary>
    public void MutateGames(Action<List<TrackedGame>> mutate)
    {
        var next = new List<TrackedGame>(Games);
        mutate(next);
        Games = next;
    }

    /// <summary>
    /// Re-read one game's sync bookkeeping from disk into the caller's object.
    ///
    /// The mirror of <see cref="SaveGameSyncState"/>, and the other half of the same bug. That method
    /// stops this process <b>erasing</b> a parent version another process recorded; this one stops it
    /// <b>using</b> a parent another process has already superseded.
    ///
    /// The daemon loads its config once at boot and holds <see cref="TrackedGame"/> references for the
    /// process lifetime (<c>Daemon.StartFolderWatchers</c>), so once the launch wrapper pushes on game
    /// exit, every watch-push here would otherwise present a stale parent. The server correctly
    /// rejects it as a conflict — and because the conflict path deliberately does not advance the
    /// pointer (<c>SyncEngine</c>), the daemon never recovers: it conflicts on every save until it is
    /// restarted, on a fleet of exactly one machine.
    ///
    /// <para>
    /// <see cref="TrackedGame.SaveDirectory"/> is deliberately NOT refreshed here — reconcile owns it,
    /// and pulling it back from disk mid-sync would fight the poller for no benefit.
    /// </para>
    /// </summary>
    public void RefreshGameSyncState(TrackedGame game)
    {
        using var guard = AgentStateLock.Acquire("config", StateDir);

        var stored = ReadOnDisk()?.Games.FirstOrDefault(g => g.GameId == game.GameId);
        if (stored is null) return;

        game.LastKnownVersionId = stored.LastKnownVersionId;
        game.LastSyncedHash = stored.LastSyncedHash;
        game.ConsecutiveConflicts = stored.ConsecutiveConflicts;
    }

    /// <summary>
    /// Persist one game's sync bookkeeping without clobbering concurrent changes to anything else.
    ///
    /// This is the lost-update that mattered. The daemon and the launch wrapper each hold their own
    /// <see cref="AgentConfig"/>, loaded at startup. The wrapper pushes on game exit and records the
    /// new parent version; the daemon's folder watcher then saves ITS copy, which still carries the
    /// old <c>LastKnownVersionId</c>, and the new one is gone. The next push then presents a stale
    /// parent and the server rejects it as a conflict — the machine is stuck, and nothing in the
    /// logs says why. It looks exactly like the two-machine divergence in CONTEXT.md, but there is
    /// only one machine.
    ///
    /// So: re-read what is on disk under the lock, apply only this game's fields plus the counters
    /// this call earned, and write that back.
    /// </summary>
    public void SaveGameSyncState(TrackedGame game, bool countPush = false, bool touchSyncTime = false)
    {
        using var guard = AgentStateLock.Acquire("config", StateDir);

        AgentConfig onDisk;
        try
        {
            onDisk = File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? this
                : this;
        }
        catch
        {
            // Unreadable on disk — our in-memory copy is the best truth available.
            onDisk = this;
        }
        onDisk.ConfigPath = ConfigPath;

        // The other process removed this game while we were syncing it. Respect that rather than
        // resurrecting an entry the user deleted — the sync itself already completed, so its
        // counters are still earned and are written below; only the per-game entry is dropped.
        var target = onDisk.Games.FirstOrDefault(g => g.GameId == game.GameId);
        if (target is not null)
        {
            target.LastKnownVersionId = game.LastKnownVersionId;
            target.LastSyncedHash = game.LastSyncedHash;
            target.ConsecutiveConflicts = game.ConsecutiveConflicts;
            target.SaveDirectory = game.SaveDirectory;
        }

        if (countPush) onDisk.TotalSavesPushed++;
        if (touchSyncTime) onDisk.LastSyncTime = DateTime.UtcNow;

        AtomicFile.WriteAllText(ConfigPath, JsonSerializer.Serialize(onDisk, JsonOpts),
            restrictPermissions: true);

        // Keep our own copy consistent with what we just wrote, so a later full Save() from this
        // process does not push a stale counter back over it.
        TotalSavesPushed = onDisk.TotalSavesPushed;
        LastSyncTime = onDisk.LastSyncTime;
    }

    public TrackedGame? FindGame(string name) =>
        Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A game this machine syncs, with its resolved local save location.</summary>
public sealed class TrackedGame
{
    public Guid GameId { get; set; }
    public string Name { get; set; } = "";
    public string? ManifestKey { get; set; }
    /// <summary>The local save directory to archive/restore.</summary>
    public string SaveDirectory { get; set; } = "";
    /// <summary>Process names (without .exe) that, when running, mean the game is in use.</summary>
    public List<string> ProcessNames { get; set; } = new();
    /// <summary>
    /// Steam AppID this game launches under, as the <b>unsigned</b> string Steam uses to name
    /// <c>compatdata/&lt;appid&gt;/</c>. Set for non-Steam shortcuts on Linux; the launch wrapper
    /// matches on it to find the game for the prefix Steam handed it. Null on Windows.
    /// </summary>
    public string? SteamAppId { get; set; }
    /// <summary>
    /// The game's install directory on this machine — what the Ludusavi manifest calls
    /// <c>&lt;base&gt;</c>, and the placeholder its save paths use more than any other. Recorded at
    /// enrollment so a later re-resolve (the poller, the launch wrapper) expands the same templates
    /// the scanner did; without it they silently resolve fewer paths than discovery already had.
    /// </summary>
    public string? InstallDir { get; set; }
    /// <summary>The server version this machine last pulled or pushed (its parent for the next push).</summary>
    public Guid? LastKnownVersionId { get; set; }
    /// <summary>Content hash of the local save at last sync, to detect real changes.</summary>
    public string? LastSyncedHash { get; set; }
    /// <summary>
    /// Consecutive uploads the server rejected as conflicts. After three, ordinary pushes stop
    /// sending full archives until a clean pull/push resets the count; forced pushes still bypass it.
    /// </summary>
    public int ConsecutiveConflicts { get; set; }
    /// <summary>Effective exclude globs (global defaults ∪ per-game) from the server;
    /// files matching these are skipped when hashing and archiving.</summary>
    public List<string> ExcludeGlobs { get; set; } = new();
    /// <summary>
    /// When something last confirmed this game's Steam launch options carry the wrapper, and what
    /// went wrong if it could not.
    ///
    /// <para>
    /// Both null is <b>unknown</b>, not broken: the agent cannot read Steam's launch options itself
    /// (see <see cref="LaunchOptions"/>), so this is only ever filled in by something that can — a
    /// Decky plugin today. Most machines have none, and reporting them all as faulty would make the
    /// signal worthless. A recorded <see cref="LaunchOptionsError"/>, by contrast, is a real fault:
    /// something tried and failed.
    /// </para>
    /// </summary>
    public DateTime? LaunchOptionsAppliedAt { get; set; }
    /// <inheritdoc cref="LaunchOptionsAppliedAt"/>
    public string? LaunchOptionsError { get; set; }

    /// <summary>
    /// The AppID this game launches under: the recorded one, or — when it was never recorded — the
    /// one its own save directory reveals, because a Proton prefix is named for the AppID that
    /// launches into it.
    ///
    /// <para>
    /// A method rather than a property so it is never serialised into <c>config.json</c>: the stored
    /// value stays the one something actually established, and this stays a derivation. Use it
    /// anywhere the AppID is being *matched* or *reported*; the daemon separately backfills the
    /// stored field so the file itself becomes correct.
    /// </para>
    /// </summary>
    public string? ResolveSteamAppId() =>
        string.IsNullOrWhiteSpace(SteamAppId)
            ? SteamLayout.CompatDataIdIn(SaveDirectory)
            : SteamAppId;
}
