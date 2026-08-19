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
    /// <summary>
    /// The local API port. SAVELOCKER_TRAY_PORT moves it so a harness can drive a real tray without
    /// colliding with the installed one. Test-only and deliberately unadvertised, like the other two
    /// (Decisions.md — "kept for testing, never documented for users").
    /// </summary>
    private static readonly int AgentApiPort =
        int.TryParse(Environment.GetEnvironmentVariable("SAVELOCKER_TRAY_PORT"), out var p) && p > 0
            ? p : 5178;

    private readonly AgentConfig _config;
    private readonly NotifyIcon _icon;
    // Created, disposed and replaced only on the UI thread (StartFolderWatchers is the sole writer
    // and every caller of it dispatches). WA-09.
    private readonly List<FolderWatcher> _folderWatchers = new();
    private readonly UiDispatcher _ui;
    private readonly Detection _detection;
    private readonly GameScanner _scanner;
    private readonly CommandPoller _commandPoller;
    private readonly AgentApiServer _apiServer;
    private readonly OfflineQueue _offlineQueue;
    private readonly HealthReporter _health;
    private readonly OfflineQueueDrainer _drainer;
    private AgentWindow? _window;
    private readonly ProcessWatcher _processWatcher;
    // Rebuilt from an API request thread; read from watcher/poller threads. See Daemon._engine.
    private volatile SyncEngine _engine = null!;
    // Outlives every engine rebuild on purpose — see AgentApiServer's field of the same type.
    private readonly SyncActivityTracker _activity = new();

    private UpdateChecker? _updateChecker;
    private readonly System.Threading.Timer _updateTimer;
    // Last check result cached for the local API server to read.
    internal UpdateResult? LastUpdateResult;
    // Label of the "Check for Updates" menu item so we can mutate it after a check.
    private ToolStripMenuItem? _updateMenuItem;

    public TrayContext(AgentConfig config)
    {
        // FIRST: this constructor runs before Application.Run pumps anything, so the dispatcher has
        // to establish the UI owner rather than capture one that does not exist yet. WA-09.
        _ui = new UiDispatcher();

        _config = config;
        _offlineQueue = OfflineQueue.For(config);
        _health = HealthReporter.For(config);
        _detection = new Detection(config);
        _scanner = new GameScanner(_detection);

        AgentLogger.Log("SaveLocker agent starting…");
        AgentLogger.Log($"UI owner: {_ui.Owner}");
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
            doScan: () => _scanner.ScanAsync(),
            enroll: EnrollAsync,
            autoStart: new AutoStart(),
            pickFolder: () => FolderPicker.ShowAsync(_ui),
            onConnectionChanged: RebuildEngine,
            getUpdateResult: () => LastUpdateResult,
            browseRoots: GameScanner.BrowseRoots(),
            onGamesChanged: () => _ui.Post(() => { RebuildMenu(); StartFolderWatchers(); }),
            activity: _activity,
            syncAll: () => _engine.SyncAllAsync(_config.Games));
        _apiServer.Start();

        _commandPoller = new CommandPoller(
            _config,
            () => ApiClient.For(_config),
            () => _engine,
            _detection,
            _scanner,
            Notify,
            onGamesChanged: () => _ui.Post(() => { RebuildMenu(); StartFolderWatchers(); }),
            health: _health,
            offlineQueue: _offlineQueue);
        _commandPoller.Start();

        // 24 h periodic update check. First tick fires after 5 s so the tray is fully
        // visible before the balloon appears; subsequent ticks every 24 h.
        _updateTimer = new System.Threading.Timer(
            _ => FireAndForget(() => CheckForUpdateAsync(silent: true)),
            null,
            dueTime: TimeSpan.FromSeconds(5),
            period: TimeSpan.FromHours(24));

        _ui.Post(MaybeShowFirstRun);
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
        // Retire the update channel along with the engine. Both the checker (which caches the old
        // base URL and key) and any cached "an update is available" result belong to the server
        // that answered — after a server change the menu item still offered server A's installer,
        // and clicking it downloaded and ran a binary the currently-configured server never
        // authorised. WA-05.
        var previous = _updateChecker;
        if (previous is not null && !ServerOrigin.Same(previous.ServerUrl, _config.ServerUrl))
        {
            _updateChecker = null;
            previous.Dispose();
            LastUpdateResult = null;
            _ui.Post(RebuildMenu);
        }

        var api = ApiClient.For(_config);
        // Windows toasts AND reports: the tray tells the user in front of it, health reporting tells
        // the console. One dashboard then shows the whole fleet, Deck and PC alike.
        var replaced = _engine;
        _engine = new SyncEngine(_config, api, log: Log, notify: Notify,
            offlineQueue: _offlineQueue, health: _health, activity: _activity);

        // Retire the engine we just replaced, or its lease timers keep renewing against the old
        // server forever while exit releases against the new one. Off the calling thread because
        // this runs from a Kestrel request thread that must not block on a network release, and
        // because RebuildEngine is also called from the constructor. WA-06.
        if (replaced is not null)
            _ = Task.Run(async () =>
            {
                try { await replaced.RetireAsync(); }
                catch (Exception ex) { AgentLogger.LogException("RetireEngine", ex); }
            });
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
                // Checked here as well as in the engine so the toast names the real reason. The
                // engine refuses regardless; this is only about what the user is told.
                if (GameActivity.IsActive(game, out var proc))
                {
                    Notify($"{game.Name}: can't pull while the game is running" +
                           (proc is null ? "" : $" ({proc}.exe)") + ". Close it and try again.");
                    return;
                }
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
                (_, _) => FireAndForget(() => PromptAndUpdateAsync(avail)))
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
            // The view is handed to the constructor so it is already queued before OnLoad runs.
            _window = new AgentWindow(AgentApiPort, view);
            _window.FormClosed += (_, _) => { _window = null; };
        }
        else
        {
            // An existing window may still be starting WebView2, so this is queued rather than
            // dropped. The IsHandleCreated guard that used to sit at the bottom of this method
            // tested the wrong thing entirely: Show() creates the handle immediately, while
            // WebView2 comes up long afterwards, so it was true precisely when the navigation
            // could not yet be honoured. WA-12.
            _window.NavigateToView(view);
        }

        if (!_window.Visible)
            _window.Show();
        _window.BringToFront();
        _window.Activate();
    }

    // ─── Enrollment (called by AgentApiServer) ──────────────────────────────────

    private async Task<(int enrolled, int skipped)> EnrollAsync(
        IReadOnlyList<ScanCandidate> candidates, int[] ids)
    {
        var result = await Enroller.EnrollAsync(_config, candidates, ids);
        if (result.enrolled > 0)
            _ui.Post(() => { RebuildMenu(); StartFolderWatchers(); });
        return result;
    }

    // ─── Tray actions ────────────────────────────────────────────────────────────

    private async Task SyncAll() => Notify(await _engine.SyncAllAsync(_config.Games));

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
        _ui.Post(RebuildMenu);

        // Log EVERY outcome. A check that found nothing used to leave no trace at all — no balloon,
        // no log line — so "I clicked check for updates and nothing happened" was undiagnosable
        // after the fact. Balloon tips are also suppressed outright by Focus Assist and by
        // notification settings, so the log is the only account that always survives.
        AgentLogger.Log(result switch
        {
            UpdateResult.Available av => $"Update check: v{av.Version} available (current {UpdateChecker.CurrentVersionText}).",
            UpdateResult.UpToDate     => $"Update check: up to date (v{UpdateChecker.CurrentVersionText}).",
            UpdateResult.Skipped      => $"Update check: an update is available but v{_config.SkipVersion} was skipped by the user.",
            UpdateResult.Failed f     => $"Update check FAILED: {f.Reason}",
            _                         => $"Update check: {result.GetType().Name}."
        });

        if (result is UpdateResult.Available a)
        {
            Notify($"SaveLocker v{a.Version} is available. Click to update.");
            _ui.Post(() =>
            {
                _icon.BalloonTipClicked += OnBalloonUpdateClicked;
                _icon.BalloonTipClosed  += OnBalloonClosed;
            });
        }
        else if (!silent)
        {
            // A check the USER asked for must always answer. Failed and Skipped used to fall through
            // in silence, which is indistinguishable from the menu item doing nothing at all.
            Notify(result switch
            {
                UpdateResult.UpToDate => $"You're up to date (v{UpdateChecker.CurrentVersionText}).",
                UpdateResult.Skipped  => $"v{_config.SkipVersion} is available but you chose to skip it. Reinstall from the console to take it.",
                UpdateResult.Failed f => $"Could not check for updates: {f.Reason}",
                _                     => "Update check finished with no result."
            });
        }
    }

    private void OnBalloonUpdateClicked(object? sender, EventArgs e)
    {
        UnhookBalloon();
        if (LastUpdateResult is UpdateResult.Available a)
            FireAndForget(() => PromptAndUpdateAsync(a));
    }

    private void OnBalloonClosed(object? sender, EventArgs e) => UnhookBalloon();

    private void UnhookBalloon()
    {
        _icon.BalloonTipClicked -= OnBalloonUpdateClicked;
        _icon.BalloonTipClosed  -= OnBalloonClosed;
    }

    private async Task PromptAndUpdateAsync(UpdateResult.Available update)
    {
        var version = update.Version;
        // Both prompts run on the UI thread: this method is reached from FireAndForget (a thread-pool
        // task) as well as from the menu, and a MessageBox owned by a pool thread blocks that thread
        // instead of the message loop. WA-09.
        var choice = await _ui.InvokeAsync(() => MessageBox.Show(
            $"SaveLocker v{version} is available.\n\nUpdate now? The app will restart after installing.",
            "SaveLocker Update",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly));

        if (choice == DialogResult.No)
        {
            // Ask whether to skip this version entirely.
            var skip = await _ui.InvokeAsync(() => MessageBox.Show(
                "Skip this version and don't ask again?",
                "SaveLocker Update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2,
                MessageBoxOptions.DefaultDesktopOnly));

            if (skip == DialogResult.Yes)
            {
                _config.SkipVersion = version;
                _config.Save();
                LastUpdateResult = null;
                _ui.Post(RebuildMenu);
            }
            return;
        }

        try
        {
            Notify($"Downloading SaveLocker v{version}…");
            _updateChecker ??= new UpdateChecker(_config);

            // Re-checked at the moment of use, not only when the menu was built: the user can sit
            // on this prompt while the server changes underneath them. The result is authorised by
            // the server that answered, so it must not be launched against a different one. WA-05.
            if (!ServerOrigin.Same(_updateChecker.ServerUrl, _config.ServerUrl))
            {
                LastUpdateResult = null;
                _ui.Post(RebuildMenu);
                Notify("The server changed while this update was pending. " +
                       "Check for updates again to get one from the current server.");
                return;
            }

            var installerPath = await _updateChecker.DownloadInstallerAsync(
                version, update.DownloadUrl, update.Sha256);

            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /FORCECLOSEAPPLICATIONS /NORESTART",
            });

            // Release the single-instance mutex so the installer can replace the exe.
            _ui.Post(ExitThread);
        }
        catch (Exception ex)
        {
            AgentLogger.LogException("PromptAndUpdateAsync", ex);
            Notify("Update failed: " + ex.Message);
        }
    }

    // ─── Process watcher callbacks ───────────────────────────────────────────────

    // Asks the OS rather than the watcher's cached set. The set is only populated by a launch the
    // watcher observed, so a game that was already running when the tray started was invisible to
    // it — exactly the case the WA-01 baseline creates.
    private static bool IsRunning(TrackedGame g) => GameActivity.IsActive(g);

    private void OnLaunched(string gameName)
    {
        var game = _config.FindGame(gameName);
        if (game is not null) FireAndForget(async () =>
        {
            // preLaunch: false — ProcessWatcher observed the game up to a poll interval AFTER it
            // started, so this is not a pre-launch boundary and must not restore. WA-01.
            var (granted, holder) = await _engine.OnGameLaunchAsync(game, preLaunch: false);
            if (!granted && holder is not null)
            {
                _apiServer.AddLeaseWarning(game.Name, holder);
                _ui.Post(() => OpenWindow("overview"));
            }
        });
    }

    private void OnExited(string gameName)
    {
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

    private void Notify(string message) => _ui.Post(() =>
        _icon.ShowBalloonTip(4000, "SaveLocker", message, ToolTipIcon.Info));

    // The engine's routine-progress sink: the file log exactly as before, plus the agent UI's
    // Overview activity feed. One delegate so nothing else has to know the feed exists.
    private void Log(string message)
    {
        AgentLogger.Log(message);
        _activity.Record(message);
    }

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
            // Synchronous: Dispose cannot await, so a held lease expires rather than being released.
            // Exit is not a connection change — the same server is still the right one on restart.
            _engine?.Dispose();
            foreach (var w in _folderWatchers) w.Dispose();
            // Last: everything above may post a final menu rebuild or balloon on its way out.
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
