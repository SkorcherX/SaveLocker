namespace SaveLocker.Agent.Linux;

/// <summary>
/// The headless agent. There is no tray and no toast on a Deck (Game Mode has no desktop), so
/// the daemon is the whole Linux agent: it serves the same React UI the tray hosts, polls the
/// server for dashboard commands, drains the offline queue, and watches save folders.
///
/// The launch wrapper (<see cref="ProtonRun"/>), not a process watcher, is what drives
/// pull-on-launch and push-on-exit — see Decisions.md §3.
/// </summary>
public sealed class Daemon : IAsyncDisposable
{
    public const int DefaultApiPort = 5178;

    private readonly int _apiPort;
    private readonly AgentConfig _config;
    private readonly Detection _detection;
    private readonly LinuxGameScanner _scanner;
    private readonly OfflineQueue _offlineQueue;
    private readonly HealthReporter _health;
    private readonly List<FolderWatcher> _folderWatchers = new();

    private AgentApiServer? _apiServer;
    private CommandPoller? _commandPoller;
    private OfflineQueueDrainer? _drainer;
    private Timer? _updateTimer;

    // Written by the update timer, read by API request threads answering /api/agent-version.
    private volatile UpdateResult? _lastUpdateResult;

    // Bound to the origin it was built for, exactly like the engine, and retired the same way on a
    // connection change — an update authorised by server A must never be offered against server B.
    private UpdateChecker? _updateChecker;
    // Written from an API request thread (server URL change, registration), read from watcher,
    // poller and drainer threads — the publish has to be visible to all of them.
    private volatile SyncEngine _engine;
    // Outlives every engine rebuild on purpose — see AgentApiServer's field of the same type.
    private readonly SyncActivityTracker _activity = new();

    /// <summary>
    /// <paramref name="apiPort"/> is overridable so a test harness can run a daemon alongside the
    /// real agent. It is still loopback-only — the port moves, the exposure does not.
    /// </summary>
    public Daemon(AgentConfig config, int apiPort = DefaultApiPort)
    {
        _apiPort = apiPort;
        _config = config;
        _offlineQueue = OfflineQueue.For(config);
        _health = HealthReporter.For(config);
        _detection = new Detection(config);
        _scanner = new LinuxGameScanner(_detection);
        _engine = BuildEngine();
    }

    private SyncEngine BuildEngine() =>
        new(_config, ApiClient.For(_config),
            log: Log, notify: Notify, offlineQueue: _offlineQueue, health: _health, activity: _activity);

    /// <summary>
    /// There is nobody here to notify — a toast is impossible in Game Mode — so locally a
    /// "notification" is only a log line. What makes these visible is <see cref="HealthReporter"/>:
    /// the same alerts go to the server, and the console shows them. The console is the Deck's UI
    /// (Decisions.md §2).
    /// </summary>
    private static void Notify(string message) => AgentLogger.Log(message);

    // The engine's routine-progress sink: the file log exactly as before, plus the agent UI's
    // Overview activity feed. One delegate so nothing else has to know the feed exists.
    private void Log(string message)
    {
        AgentLogger.Log(message);
        _activity.Record(message);
    }

    /// <summary>
    /// Record the AppID a tracked game's own save folder reveals, for games that never got one.
    ///
    /// <para>
    /// Everything that matches on the AppID resolves it on the fly, so this is not what makes those
    /// games work — it is what makes <c>config.json</c>, <c>list</c> and the console tell the truth
    /// about them. The daemon does it because it is the single long-lived writer of the config; a
    /// short-lived process doing the same write is the documented lost-update trap.
    /// </para>
    /// </summary>
    private void BackfillSteamAppIds()
    {
        var filled = 0;
        foreach (var game in _config.Games)
        {
            if (!string.IsNullOrWhiteSpace(game.SteamAppId)) continue;
            if (SteamLayout.CompatDataIdIn(game.SaveDirectory) is not { } id) continue;

            game.SteamAppId = id;
            filled++;
            AgentLogger.Log($"recorded appid {id} for '{game.Name}' from its save path — " +
                            "the launch wrapper could not match this game before");
        }

        if (filled > 0) _config.Save();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        AgentLogger.Log($"SaveLocker daemon starting — machine '{_config.MachineName}', server {_config.ServerUrl}");

        BackfillSteamAppIds();

        _apiServer = new AgentApiServer(
            port: _apiPort,
            config: _config,
            doScan: () => _scanner.ScanAsync(),
            enroll: async (candidates, ids) =>
            {
                var result = await Enroller.EnrollAsync(_config, candidates, ids, ct);
                if (result.enrolled > 0) StartFolderWatchers();
                return result;
            },
            autoStart: new SystemdAutoStart(),
            pickFolder: null, // headless: no native dialog — the UI browses via /api/browse instead
            // Retire the engine being replaced, off the request thread. Dropping it kept its lease
            // timers renewing against the old server for the life of the process. WA-06.
            onConnectionChanged: () =>
            {
                var replaced = _engine;
                _engine = BuildEngine();
                _ = Task.Run(async () =>
                {
                    try { await replaced.RetireAsync(); }
                    catch (Exception ex) { AgentLogger.LogException("RetireEngine", ex); }
                });

                // The offer belonged to the old server. Keeping it would advertise a version this
                // server may not have, at a URL on an origin this machine no longer trusts.
                var oldChecker = _updateChecker;
                _updateChecker = null;
                _lastUpdateResult = null;
                oldChecker?.Dispose();
            },
            getUpdateResult: () => _lastUpdateResult,
            browseRoots: SteamRoots.BrowseRoots().Concat(HeroicRoots.BrowseRoots()),
            launchInfo: LinuxLaunchCommand,
            onGamesChanged: StartFolderWatchers,
            // Read per request rather than captured once: the plugin can be installed, updated or
            // removed while the daemon runs — by Decky, by the user, or by this agent itself.
            deckyStatus: DeckyPlugin.Status,
            // Also per request: this daemon stages updates on its own timer, and whether a game is
            // running — which is what decides if a restart would install anything — changes minute
            // to minute. A value captured at startup would be wrong by the time anyone read it.
            stagedUpdate: () => Updater.StagedUpdate(_config),
            activity: _activity,
            syncAll: () => _engine.SyncAllAsync(_config.Games));
        _apiServer.Start();

        _drainer = new OfflineQueueDrainer(_offlineQueue, _config, () => _engine, Notify);

        _commandPoller = new CommandPoller(
            _config,
            () => ApiClient.For(_config),
            () => _engine,
            _detection,
            _scanner,
            Notify,
            onGamesChanged: StartFolderWatchers,
            health: _health,
            offlineQueue: _offlineQueue,
            // Lets a templated save path expand inside the game's own Proton prefix. Core has no
            // way to find it: only the Linux host knows where Steam keeps compatdata.
            prefixForAppId: appId => SteamRoots.Find()
                .Select(root => SteamRoots.CompatDataPath(root, appId))
                .FirstOrDefault(p => p is not null));
        _commandPoller.Start();

        StartFolderWatchers();
        StartUpdateChecks();

        // Everything is up: the API server is listening, the poller and watchers are running. That
        // is the bar for "this build works", and it is what turns an applied update from provisional
        // into permanent. Until this runs, the next start treats the installed version as suspect
        // and puts the previous one back (Updater.cs).
        Updater.Commit(_config, AgentLogger.Log);

        var where = $"http://localhost:{_apiPort}/";
        AgentLogger.Log($"daemon ready — agent UI on {where}");
        Console.WriteLine($"SaveLocker daemon running. Agent UI: {where}");
        Console.WriteLine($"To reach it from another device, tunnel it: ssh -L {_apiPort}:localhost:{_apiPort} <user>@<this-machine>");

        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { /* SIGTERM / Ctrl-C */ }

        AgentLogger.Log("SaveLocker daemon stopping.");
    }

    /// <summary>
    /// The Steam launch-options command for this device. We resolve the binary's real path from
    /// <c>/proc/self/exe</c> rather than assuming <c>$HOME/.local/bin</c>: that is where install.sh
    /// links <c>savelocker</c>, but the daemon may have been started from anywhere, and the full path
    /// is required because Game Mode does not put <c>~/.local/bin</c> on PATH.
    /// </summary>
    internal static LaunchCommandDto LinuxLaunchCommand()
    {
        var exe = WrapperPath();
        return exe is null
            ? new LaunchCommandDto(null, "Could not determine the installed path — see install.sh for the exact command.")
            : new LaunchCommandDto(LaunchOptions.Invocation(exe), null);
    }

    /// <inheritdoc cref="LinuxLaunchCommand"/>
    internal static string? WrapperPath()
    {
        string? exe = null;
        try { exe = new FileInfo("/proc/self/exe").LinkTarget; } catch { /* fall back below */ }
        if (string.IsNullOrEmpty(exe)) exe = Environment.ProcessPath;
        return string.IsNullOrEmpty(exe) ? null : exe;
    }

    /// <summary>
    /// The wrapper path for display when there may not be one. The placeholder is deliberately not a
    /// plausible-looking path: a user pasting it would get a command that fails immediately, which is
    /// far better than one that looks right and silently never runs.
    /// </summary>
    internal static string WrapperPathOrDefault() =>
        WrapperPath() ?? "<path to savelocker>";

    /// <summary>
    /// Ask the server what it is offering for this platform: shortly after start, then every few
    /// hours. Cheap — one small GET — so a Deck that reboots often simply checks often.
    /// <para>
    /// <b>This checks and reports; it does not update.</b> Nothing is downloaded, unpacked or run
    /// here. The result exists so `doctor`, the agent UI and (through the heartbeat) the console can
    /// say a device is behind — which on a headless Deck is the entire user-visible mechanism, since
    /// there is nothing here that can toast (Decisions.md §2). Staging and applying arrive with the
    /// updater; until then a Deck is still updated by re-running install.sh.
    /// </para>
    /// </summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new Timer(
            _ => _ = CheckForUpdateAsync(),
            null,
            // Short, not instant: long enough for the API server and the first reconcile to settle,
            // short enough that someone who opens the agent UI right after a restart — which is
            // exactly when they do — is not looking at a blank update line.
            dueTime: TimeSpan.FromSeconds(5),
            period: TimeSpan.FromHours(6));
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            // An unregistered agent has no key, and the route is authenticated. Asking anyway would
            // log a 401 every six hours on a machine whose real problem is that nobody enrolled it.
            if (string.IsNullOrEmpty(_config.ApiKey)) return;

            var checker = _updateChecker ??= new UpdateChecker(_config);
            var result = await checker.CheckAsync();
            _lastUpdateResult = result;

            _config.UpdateSettings(c => c.LastUpdateCheck = DateTime.UtcNow);

            // Every outcome is logged, including the boring one. On a box with no UI the log is the
            // only account of whether the check happened at all, and "it never said anything" has to
            // be distinguishable from "it said nothing was there".
            AgentLogger.Log(result switch
            {
                UpdateResult.Available a =>
                    $"Update check: v{a.Version} available (running {UpdateChecker.CurrentVersionText}).",
                UpdateResult.UpToDate => $"Update check: up to date (v{UpdateChecker.CurrentVersionText}).",
                UpdateResult.Skipped   => $"Update check: v{_config.SkipVersion} is available but was skipped.",
                UpdateResult.Failed f  => $"Update check FAILED: {f.Reason}",
                _                      => $"Update check: {result.GetType().Name}.",
            });

            if (result is UpdateResult.Available update &&
                _config.AutoUpdate &&
                !string.Equals(Updater.PendingVersion(_config), update.Version, StringComparison.Ordinal))
            {
                // Staged, never applied. Swapping the agent's files out from under a running daemon
                // is what `systemctl --user stop` killing the whole cgroup makes impossible to do
                // safely from here — so the work that CAN be done now is done now, and the swap
                // waits for the next start (Updater.cs). `savelocker update` is the way to say
                // "now" instead.
                try { await Updater.StageAsync(_config, update, AgentLogger.Log); }
                catch (Exception ex) { AgentLogger.Log($"update: could not stage v{update.Version} — {ex.Message}"); }
            }

            await CheckPluginUpdateAsync();
        }
        catch (Exception ex)
        {
            AgentLogger.LogException("CheckForUpdate", ex);
        }
    }

    /// <summary>
    /// The Decky plugin, on the same schedule and the same switch as the agent's own update.
    /// <para>
    /// Unlike the agent, this one <b>applies</b>. Nothing has to be stopped to replace another
    /// application's files, and Decky reloads the plugin by itself when they change — so there is no
    /// next-start step to wait for, and staging it would only invent one.
    /// </para>
    /// <para>
    /// Silent on a machine with no Decky, which is almost all of them: <see cref="DeckyPlugin"/>
    /// answers <see cref="PluginUpdateState.NoDecky"/> before it does any work at all.
    /// </para>
    /// </summary>
    private async Task CheckPluginUpdateAsync()
    {
        var outcome = await DeckyPlugin.CheckAsync(_config, AgentLogger.Log, apply: _config.AutoUpdate);

        // NoDecky is the only outcome that says nothing. Every other one — including "up to date" —
        // is logged, for the same reason the agent's own check is: on a box with no UI, the log is
        // the only account of whether the check happened at all.
        if (outcome.State is PluginUpdateState.NoDecky) return;
        AgentLogger.Log($"plugin check: {outcome.Message}");
    }

    /// <summary>
    /// Watch each mapped save folder so a game that syncs *without* the launch wrapper (started
    /// outside Steam, or a save written while the game idles) still gets pushed. The settle gate
    /// keeps this from archiving a save mid-write.
    /// </summary>
    private void StartFolderWatchers()
    {
        foreach (var w in _folderWatchers) w.Dispose();
        _folderWatchers.Clear();

        foreach (var g in _config.Games.Where(g => Directory.Exists(g.SaveDirectory)))
        {
            var game = g;
            _folderWatchers.Add(new FolderWatcher(game.SaveDirectory, () =>
                _ = PushQuietlyAsync(game)));
        }
    }

    private async Task PushQuietlyAsync(TrackedGame game)
    {
        try { await _engine.PushAsync(game, settle: true); }
        catch (Exception ex) { AgentLogger.LogException($"watch-push {game.Name}", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var w in _folderWatchers) w.Dispose();
        _updateTimer?.Dispose();
        _updateChecker?.Dispose();
        _commandPoller?.Dispose();
        _drainer?.Dispose();
        _apiServer?.Dispose();
        // Shutdown, not a connection change: stop renewing and let any held lease expire rather
        // than blocking the exit on a release to a server that may be why we are stopping.
        _engine.Dispose();
    }
}
