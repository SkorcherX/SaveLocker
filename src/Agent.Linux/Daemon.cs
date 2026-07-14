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
    private const int AgentApiPort = 5178;

    private readonly AgentConfig _config;
    private readonly Detection _detection;
    private readonly LinuxGameScanner _scanner;
    private readonly OfflineQueue _offlineQueue = new();
    private readonly List<FolderWatcher> _folderWatchers = new();

    private AgentApiServer? _apiServer;
    private CommandPoller? _commandPoller;
    private OfflineQueueDrainer? _drainer;
    private SyncEngine _engine;

    public Daemon(AgentConfig config)
    {
        _config = config;
        _detection = new Detection(config);
        _scanner = new LinuxGameScanner(_detection);
        _engine = BuildEngine();
    }

    private SyncEngine BuildEngine() =>
        new(_config, ApiClient.For(_config),
            log: AgentLogger.Log, notify: Notify, offlineQueue: _offlineQueue);

    /// <summary>
    /// There is nobody to notify on a headless box — a toast is impossible in Game Mode — so
    /// "notifications" are log lines. The console is the Deck's UI, and surfacing these to it is
    /// agent health reporting (Phase 5), not something to fake here.
    /// </summary>
    private static void Notify(string message) => AgentLogger.Log(message);

    public async Task RunAsync(bool listenOnAllInterfaces, CancellationToken ct)
    {
        AgentLogger.Log($"SaveLocker daemon starting — machine '{_config.MachineName}', server {_config.ServerUrl}");

        _apiServer = new AgentApiServer(
            port: AgentApiPort,
            config: _config,
            ui: new SynchronizationContext(),
            doScan: () => _scanner.ScanAsync(),
            enroll: async (candidates, ids) =>
            {
                var result = await Enroller.EnrollAsync(_config, candidates, ids, ct);
                if (result.enrolled > 0) StartFolderWatchers();
                return result;
            },
            autoStart: new SystemdAutoStart(),
            pickFolder: null, // headless: no native dialog, so the UI takes a typed path
            onRegistered: () => _engine = BuildEngine(),
            getUpdateResult: () => null, // self-update is Windows-only (installer-based)
            listenOnAllInterfaces: listenOnAllInterfaces);
        _apiServer.Start();

        _drainer = new OfflineQueueDrainer(_offlineQueue, _config, () => _engine, Notify);

        _commandPoller = new CommandPoller(
            _config,
            () => ApiClient.For(_config),
            () => _engine,
            _detection,
            _scanner,
            Notify,
            onGamesChanged: StartFolderWatchers);
        _commandPoller.Start();

        StartFolderWatchers();

        var where = listenOnAllInterfaces ? $"http://0.0.0.0:{AgentApiPort}/" : $"http://localhost:{AgentApiPort}/";
        AgentLogger.Log($"daemon ready — agent UI on {where}");
        Console.WriteLine($"SaveLocker daemon running. Agent UI: {where}");

        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { /* SIGTERM / Ctrl-C */ }

        AgentLogger.Log("SaveLocker daemon stopping.");
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
        _commandPoller?.Dispose();
        _drainer?.Dispose();
        _apiServer?.Dispose();
        await Task.CompletedTask;
    }
}
