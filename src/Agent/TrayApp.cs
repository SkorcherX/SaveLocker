using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using LocalGameSync.Shared;

namespace LocalGameSync.Agent;

public static class TrayApp
{
    public static void Run(AgentConfig config)
    {
        // PerMonitorV2: ClientSize values are in logical pixels (physical ÷ DPI-scale),
        // so a 900×600 ClientSize produces 1350×900 physical pixels at 150% DPI.
        // WebView2 divides physical pixels by devicePixelRatio (1.5) → 900×600 CSS px.
        // SystemAware (the ApplicationConfiguration.Initialize default) passes physical
        // pixels straight through, giving WebView2 a 600×400 CSS viewport at 150% DPI.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(config));
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private const int AgentApiPort = 5178;

    private readonly AgentConfig _config;
    private readonly NotifyIcon _icon;
    private readonly List<FolderWatcher> _folderWatchers = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext _ui;
    private readonly Detection _detection;
    private readonly CommandPoller _commandPoller;
    private readonly AgentApiServer _apiServer;
    private readonly OfflineQueue _offlineQueue = new();
    private readonly OfflineQueueDrainer _drainer;
    private AgentWindow? _window;
    private ProcessWatcher _processWatcher;
    private SyncEngine _engine = null!;

    public TrayContext(AgentConfig config)
    {
        _config = config;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _detection = new Detection(config);

        AgentLogger.Log("SaveLocker agent starting…");
        RebuildEngine();
        _drainer = new OfflineQueueDrainer(
            _offlineQueue, _config, () => _engine,
            msg => { Notify(msg); AgentLogger.Log(msg); });

        _icon = new NotifyIcon
        {
            Icon = AppResources.Icon,
            Text = $"SaveLocker — {config.MachineName}",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenWindow();
        RebuildMenu();

        StartFolderWatchers();

        _processWatcher = BuildProcessWatcher();
        _processWatcher.Start();

        // Local HTTP server that drives the React agent UI.
        _apiServer = new AgentApiServer(
            port: AgentApiPort,
            config: _config,
            ui: _ui,
            doScan: async () =>
            {
                var scanner = new GameScanner(_detection);
                return await scanner.ScanAsync();
            },
            enroll: EnrollAsync,
            onRegistered: RebuildEngine);
        _apiServer.Start();

        _commandPoller = new CommandPoller(
            _config,
            () => new ApiClient(_config.ServerUrl, _config.ApiKey),
            () => _engine,
            _detection,
            Notify,
            onGamesChanged: () => _ui.Post(_ => { RebuildMenu(); StartFolderWatchers(); }, null));
        _commandPoller.Start();

        _ui.Post(_ => MaybeShowFirstRun(), null);
    }

    private void MaybeShowFirstRun()
    {
        if (_config.FirstRunCompleted || !string.IsNullOrEmpty(_config.ApiKey))
            return;

        var result = MessageBox.Show(
            "Welcome to SaveLocker!\n\n" +
            "This agent isn't connected to a server yet. Open Settings to set your " +
            "server URL and register this machine?\n\n" +
            "You can also enable \"Start with Windows\" there so it syncs automatically.",
            "SaveLocker — first-time setup",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);

        _config.FirstRunCompleted = true;
        _config.Save();

        if (result == DialogResult.Yes)
            OpenWindow(view: "settings");
    }

    // ─── Engine / Menu helpers ──────────────────────────────────────────────────

    private void RebuildEngine()
    {
        var api = new ApiClient(_config.ServerUrl, _config.ApiKey);
        _engine = new SyncEngine(_config, api, msg => { Notify(msg); AgentLogger.Log(msg); }, _offlineQueue);
    }

    private void RebuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(_config.MachineName) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open SaveLocker…", null, (_, _) => OpenWindow());
        menu.Items.Add(new ToolStripSeparator());

        foreach (var g in _config.Games)
        {
            var game = g;
            var sub = new ToolStripMenuItem(game.Name);
            sub.DropDownItems.Add("Force Pull (get latest)", null, (_, _) => FireAndForget(() => _engine.PullAsync(game, force: true)));
            sub.DropDownItems.Add("Force Push (send mine)", null, (_, _) => FireAndForget(() => _engine.PushAsync(game, force: true)));
            menu.Items.Add(sub);
        }
        if (_config.Games.Count > 0)
            menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Sync All (pull then push)", null, (_, _) => FireAndForget(SyncAll));
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void StartFolderWatchers()
    {
        foreach (var w in _folderWatchers) w.Dispose();
        _folderWatchers.Clear();

        foreach (var g in _config.Games.Where(g => Directory.Exists(g.SaveDirectory)))
        {
            var game = g;
            _folderWatchers.Add(new FolderWatcher(game.SaveDirectory, () =>
            {
                if (!IsRunning(game)) FireAndForget(() => _engine.PushAsync(game));
            }));
        }
    }

    private ProcessWatcher BuildProcessWatcher()
    {
        var pw = new ProcessWatcher(() =>
            _config.Games
                .Where(g => g.ProcessNames.Count > 0)
                .ToDictionary(g => g.Name, g => (IReadOnlyList<string>)g.ProcessNames));
        pw.GameLaunched += OnLaunched;
        pw.GameExited += OnExited;
        return pw;
    }

    // ─── Window ─────────────────────────────────────────────────────────────────

    private void OpenWindow(string? view = null)
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new AgentWindow(AgentApiPort);
            _window.FormClosed += (_, _) => { _window = null; };
        }

        if (!_window.Visible)
            _window.Show();
        _window.BringToFront();
        _window.Activate();

        // Navigate to a specific view via hash if requested.
        if (view is not null && _window.IsHandleCreated)
        {
            _window.Navigate($"http://localhost:{AgentApiPort}/#{view}");
        }
    }

    // ─── Enrollment (called by AgentApiServer) ──────────────────────────────────

    private async Task<(int enrolled, int skipped)> EnrollAsync(
        IReadOnlyList<ScanCandidate> candidates, int[] ids)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
            throw new InvalidOperationException("Not registered yet. Open Settings and click Register first.");

        var api = new ApiClient(_config.ServerUrl, _config.ApiKey);
        var enrolled = 0;
        var skipped = 0;

        foreach (var id in ids)
        {
            if (id < 0 || id >= candidates.Count) continue;
            var c = candidates[id];
            if (_config.FindGame(c.Name) is not null) { skipped++; continue; }
            if (string.IsNullOrEmpty(c.SuggestedSaveDir)) { skipped++; continue; }

            var game = await api.CreateGameAsync(new CreateGameRequest(c.Name, c.ManifestKey, null));
            _config.Games.Add(new TrackedGame
            {
                GameId = game.Id,
                Name = game.Name,
                ManifestKey = c.ManifestKey,
                SaveDirectory = c.SuggestedSaveDir!,
            });
            enrolled++;
        }

        if (enrolled > 0)
        {
            _config.Save();
            _ui.Post(_ => { RebuildMenu(); StartFolderWatchers(); }, null);
        }

        return (enrolled, skipped);
    }

    // ─── Tray actions ────────────────────────────────────────────────────────────

    private async Task SyncAll()
    {
        foreach (var g in _config.Games)
        {
            await _engine.PullAsync(g);
            await _engine.PushAsync(g);
        }
        Notify("Sync all complete.");
    }

    private void OpenDashboard()
    {
        try { Process.Start(new ProcessStartInfo(_config.ServerUrl) { UseShellExecute = true }); }
        catch (Exception ex) { Notify("Could not open dashboard: " + ex.Message); }
    }

    // ─── Process watcher callbacks ───────────────────────────────────────────────

    private bool IsRunning(TrackedGame g)
    {
        lock (_running) return _running.Contains(g.Name);
    }

    private void OnLaunched(string gameName)
    {
        lock (_running) _running.Add(gameName);
        var game = _config.FindGame(gameName);
        if (game is not null) FireAndForget(async () =>
        {
            var (granted, holder) = await _engine.OnGameLaunchAsync(game);
            if (!granted && holder is not null)
            {
                _apiServer.AddLeaseWarning(game.Name, holder);
                _ui.Post(_ => OpenWindow("overview"), null);
            }
        });
    }

    private void OnExited(string gameName)
    {
        lock (_running) _running.Remove(gameName);
        _apiServer.ClearLeaseWarning(gameName);
        var game = _config.FindGame(gameName);
        if (game is not null) FireAndForget(() => _engine.OnGameExitAsync(game));
    }

    // ─── Infrastructure ──────────────────────────────────────────────────────────

    private void FireAndForget(Func<Task> action) => _ = Task.Run(async () =>
    {
        try { await action(); }
        catch (Exception ex)
        {
            AgentLogger.LogException("FireAndForget", ex);
            Notify("Error: " + ex.Message);
        }
    });

    private void Notify(string message) => _ui.Post(_ =>
        _icon.ShowBalloonTip(4000, "SaveLocker", message, ToolTipIcon.Info), null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _drainer.Dispose();
            _apiServer.Dispose();
            _window?.Dispose();
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
            _commandPoller.Dispose();
            _processWatcher.Dispose();
            foreach (var w in _folderWatchers) w.Dispose();
        }
        base.Dispose(disposing);
    }
}
