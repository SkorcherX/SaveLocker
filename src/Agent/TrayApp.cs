using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

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

    private UpdateChecker? _updateChecker;
    private readonly System.Threading.Timer _updateTimer;
    // Last check result cached for the local API server to read.
    internal UpdateResult? LastUpdateResult;
    // Label of the "Check for Updates" menu item so we can mutate it after a check.
    private ToolStripMenuItem? _updateMenuItem;

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
            onRegistered: RebuildEngine,
            getUpdateResult: () => LastUpdateResult);
        _apiServer.Start();

        _commandPoller = new CommandPoller(
            _config,
            () => new ApiClient(_config.ServerUrl, _config.ApiKey),
            () => _engine,
            _detection,
            Notify,
            onGamesChanged: () => _ui.Post(_ => { RebuildMenu(); StartFolderWatchers(); }, null));
        _commandPoller.Start();

        // 24 h periodic update check. First tick fires after 5 s so the tray is fully
        // visible before the balloon appears; subsequent ticks every 24 h.
        _updateTimer = new System.Threading.Timer(
            _ => FireAndForget(() => CheckForUpdateAsync(silent: true)),
            null,
            dueTime: TimeSpan.FromSeconds(5),
            period: TimeSpan.FromHours(24));

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
        _engine = new SyncEngine(_config, api, log: AgentLogger.Log, notify: Notify, offlineQueue: _offlineQueue);
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
            sub.DropDownItems.Add("Force Pull (get latest)", null, (_, _) => FireAndForget(async () =>
            {
                await _engine.PullAsync(game, force: true);
                Notify($"{game.Name}: force-pulled latest save.");
            }));
            sub.DropDownItems.Add("Force Push (send mine)", null, (_, _) => FireAndForget(async () =>
            {
                await _engine.PushAsync(game, force: true);
                Notify($"{game.Name}: force-pushed local save.");
            }));
            menu.Items.Add(sub);
        }
        if (_config.Games.Count > 0)
            menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Sync All (pull then push)", null, (_, _) => FireAndForget(SyncAll));
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add(new ToolStripSeparator());

        _updateMenuItem = LastUpdateResult is UpdateResult.Available avail
            ? new ToolStripMenuItem($"Update to v{avail.Version}…", null,
                (_, _) => FireAndForget(() => PromptAndUpdateAsync(avail.Version, avail.DownloadUrl)))
              { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) }
            : new ToolStripMenuItem("Check for Updates",  null,
                (_, _) => FireAndForget(() => CheckForUpdateAsync(silent: false)));
        menu.Items.Add(_updateMenuItem);

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
                if (!IsRunning(game)) FireAndForget(() => _engine.PushAsync(game, settle: true));
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

    // ─── Update check ───────────────────────────────────────────────────────────

    private async Task CheckForUpdateAsync(bool silent)
    {
        // Enforce 24 h cooldown for background checks; user-initiated checks always run.
        if (silent && _config.LastUpdateCheck.HasValue &&
            (DateTime.UtcNow - _config.LastUpdateCheck.Value).TotalHours < 24)
            return;

        _updateChecker ??= new UpdateChecker(_config);
        var result = await _updateChecker.CheckAsync();

        _config.LastUpdateCheck = DateTime.UtcNow;
        _config.Save();

        LastUpdateResult = result;
        _ui.Post(_ => RebuildMenu(), null);

        if (result is UpdateResult.Available a)
        {
            Notify($"SaveLocker v{a.Version} is available. Click to update.");
            _ui.Post(_ =>
            {
                _icon.BalloonTipClicked += OnBalloonUpdateClicked;
                _icon.BalloonTipClosed  += OnBalloonClosed;
            }, null);
        }
        else if (!silent && result is UpdateResult.UpToDate)
        {
            Notify($"You're up to date (v{UpdateChecker.CurrentVersion.ToString(3)}).");
        }
    }

    private void OnBalloonUpdateClicked(object? sender, EventArgs e)
    {
        UnhookBalloon();
        if (LastUpdateResult is UpdateResult.Available a)
            FireAndForget(() => PromptAndUpdateAsync(a.Version, a.DownloadUrl));
    }

    private void OnBalloonClosed(object? sender, EventArgs e) => UnhookBalloon();

    private void UnhookBalloon()
    {
        _icon.BalloonTipClicked -= OnBalloonUpdateClicked;
        _icon.BalloonTipClosed  -= OnBalloonClosed;
    }

    private async Task PromptAndUpdateAsync(string version, string downloadUrl)
    {
        var choice = MessageBox.Show(
            $"SaveLocker v{version} is available.\n\nUpdate now? The app will restart after installing.",
            "SaveLocker Update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly);

        if (choice == DialogResult.No)
        {
            // Ask whether to skip this version entirely.
            var skip = MessageBox.Show(
                "Skip this version and don't ask again?",
                "SaveLocker Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2,
                MessageBoxOptions.DefaultDesktopOnly);

            if (skip == DialogResult.Yes)
            {
                _config.SkipVersion = version;
                _config.Save();
                LastUpdateResult = null;
                _ui.Post(_ => RebuildMenu(), null);
            }
            return;
        }

        try
        {
            Notify($"Downloading SaveLocker v{version}…");
            _updateChecker ??= new UpdateChecker(_config);
            var installerPath = await _updateChecker.DownloadInstallerAsync(version, downloadUrl);

            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /FORCECLOSEAPPLICATIONS /NORESTART",
            });

            // Release the single-instance mutex so the installer can replace the exe.
            _ui.Post(_ => ExitThread(), null);
        }
        catch (Exception ex)
        {
            AgentLogger.LogException("PromptAndUpdateAsync", ex);
            Notify("Update failed: " + ex.Message);
        }
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
            _updateTimer.Dispose();
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
