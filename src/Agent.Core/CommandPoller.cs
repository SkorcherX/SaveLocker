using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// The agent half of the command channel ([[UX Roadmap]] Workstream 5). On a
/// timer the agent polls the (passive) server to:
/// <list type="number">
///   <item><b>Reconcile games</b> — adopt games defined on the server that aren't
///   tracked here yet (auto-mapping the save dir from the Ludusavi manifest when
///   possible), and drop local games that were deleted on the server.</item>
///   <item><b>Run queued commands</b> — execute dashboard-issued pull/push/sync/scan
///   for this machine and report the outcome.</item>
/// </list>
/// Polling (vs server push) keeps the server passive and works through tunnels /
/// firewalls, since the agent only makes outbound requests.
/// </summary>
public sealed class CommandPoller : IDisposable
{
    private readonly AgentConfig _config;
    private readonly Func<ApiClient> _api;
    private readonly Func<SyncEngine> _engine;
    private readonly Detection _detection;
    private readonly IGameScanner _scanner;
    private readonly Action<string> _notify;
    private readonly Action _onGamesChanged;
    private readonly HealthReporter? _health;
    private readonly OfflineQueue? _offlineQueue;
    private readonly System.Timers.Timer _timer;
    private int _busy; // 0/1 guard so slow ticks don't overlap

    /// <summary>How often an unmapped game is worth re-scanning for. See UpdatePathCandidatesAsync.</summary>
    private static readonly TimeSpan CandidateScanInterval = TimeSpan.FromMinutes(15);
    private DateTime _lastCandidateScan = DateTime.MinValue;

    public CommandPoller(
        AgentConfig config,
        Func<ApiClient> api,
        Func<SyncEngine> engine,
        Detection detection,
        IGameScanner scanner,
        Action<string> notify,
        Action onGamesChanged,
        double pollMs = 20000,
        HealthReporter? health = null,
        OfflineQueue? offlineQueue = null)
    {
        _config = config;
        _api = api;
        _engine = engine;
        _detection = detection;
        _scanner = scanner;
        _notify = notify;
        _onGamesChanged = onGamesChanged;
        _health = health;
        _offlineQueue = offlineQueue;
        _timer = new System.Timers.Timer(pollMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = TickAsync();
    }

    public void Start() => _timer.Start();

    private async Task TickAsync()
    {
        // Skip if unregistered or a previous tick is still running.
        if (string.IsNullOrEmpty(_config.ApiKey) || _config.MachineId is null) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            await ReconcileGamesAsync();
            await UpdatePathCandidatesAsync();
            await RunCommandsAsync();
        }
        catch (Exception ex)
        {
            AgentLogger.LogException("CommandPoller.TickAsync", ex);
        }
        finally
        {
            // Outside the try, and last: the heartbeat must go out even when reconcile or a command
            // threw — a tick that failed is precisely the tick the console needs to hear about.
            // HealthReporter.SendAsync never throws.
            if (_health is not null)
                await _health.SendAsync(_api(), _config, _offlineQueue);

            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // ----- game-list reconciliation (server → agent propagation) -----

    private async Task ReconcileGamesAsync()
    {
        var serverGames = await _api().ListGamesAsync();
        var serverById = serverGames.ToDictionary(g => g.Id);
        var changed = false;

        // Drop local games that were deleted on the server.
        var removed = _config.Games.RemoveAll(g => !serverById.ContainsKey(g.GameId));
        if (removed > 0)
        {
            changed = true;
            _notify($"Removed {removed} game(s) deleted on the server.");
        }

        var localById = _config.Games.ToDictionary(g => g.GameId);
        foreach (var sg in serverGames)
        {
            if (localById.TryGetValue(sg.Id, out var local))
            {
                // Keep exclude globs in sync with the server's effective set (silent).
                var serverGlobs = sg.ExcludeGlobs ?? Array.Empty<string>();
                if (!serverGlobs.SequenceEqual(local.ExcludeGlobs))
                {
                    local.ExcludeGlobs = serverGlobs.ToList();
                    changed = true;
                }

                // Server now has a stored path for this machine → apply it (highest authority).
                if (!string.IsNullOrWhiteSpace(sg.MachineSavePath) &&
                    sg.MachineSavePath != local.SaveDirectory)
                {
                    local.SaveDirectory = sg.MachineSavePath;
                    changed = true;
                    _notify($"Updated '{sg.Name}' save folder from server: {sg.MachineSavePath}");
                }
                // Already tracked but unmapped: detect locally and report back to server.
                else if (string.IsNullOrEmpty(local.SaveDirectory) &&
                         await ResolveSaveDirAsync(sg) is { } fill)
                {
                    local.SaveDirectory = fill;
                    changed = true;
                    _notify($"Mapped '{sg.Name}' to {fill}.");
                    ReportPathAsync(sg.Id, fill);
                }
                continue;
            }

            // Adopt a server game not tracked here yet.
            var dir = await ResolveSaveDirAsync(sg);
            // If we resolved via detection (not from the server path), report it back.
            if (dir is not null && string.IsNullOrWhiteSpace(sg.MachineSavePath))
                ReportPathAsync(sg.Id, dir);

            _config.Games.Add(new TrackedGame
            {
                GameId = sg.Id,
                Name = sg.Name,
                ManifestKey = sg.ManifestKey,
                SaveDirectory = dir ?? "",
                ExcludeGlobs = (sg.ExcludeGlobs ?? Array.Empty<string>()).ToList()
            });
            changed = true;
            _notify(dir is null
                ? $"'{sg.Name}' was added on the server — set its save folder in Settings…"
                : $"Added '{sg.Name}' (save folder {dir}).");
        }

        if (changed)
        {
            _config.Save();
            _onGamesChanged();
        }
    }

    /// <summary>
    /// Best save folder for a server game on THIS machine:
    /// 1. Server-stored path for this machine (highest authority — set by dashboard or prior run).
    /// 2. SuggestedSaveDir (server hint) if the directory exists here.
    /// 3. Ludusavi manifest detection.
    /// Returns null if nothing resolves.
    /// </summary>
    private async Task<string?> ResolveSaveDirAsync(GameDto sg)
    {
        if (!string.IsNullOrWhiteSpace(sg.MachineSavePath))
            return sg.MachineSavePath;
        if (!string.IsNullOrWhiteSpace(sg.SuggestedSaveDir) && Directory.Exists(sg.SuggestedSaveDir))
            return Path.GetFullPath(sg.SuggestedSaveDir);
        try
        {
            var dirs = await _detection.ResolveSaveDirectoriesAsync(sg.ManifestKey ?? sg.Name);
            return dirs.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>
    /// Tell the console what this machine's scan <b>guesses</b> the save folder is, for every game
    /// still unmapped after reconcile. Reconcile has already tried manifest detection and reported
    /// anything it resolved; what is left is the case the scan can still answer — a Steam shortcut
    /// or a Proton prefix the manifest does not describe.
    /// <para>
    /// Only unmapped games are reported. This is not a privacy nicety: uploading the whole scan
    /// would put the user's entire game library on the server for no purpose the console has.
    /// </para>
    /// </summary>
    private async Task UpdatePathCandidatesAsync()
    {
        if (_health is null) return;

        var unmapped = _config.Games
            .Where(g => string.IsNullOrWhiteSpace(g.SaveDirectory))
            .ToList();

        // Clear immediately when nothing is unmapped — a machine that fixed itself should stop
        // offering guesses at once, without waiting out the scan interval below.
        if (unmapped.Count == 0)
        {
            _health.SetPathCandidates(Array.Empty<ScanPathCandidate>());
            return;
        }

        // A scan walks the disk; the poll is every 20 s. Rescanning on every tick would make an
        // unmapped game a permanent background I/O load on a device with a slow SD card.
        if (DateTime.UtcNow - _lastCandidateScan < CandidateScanInterval) return;
        _lastCandidateScan = DateTime.UtcNow;

        IReadOnlyList<ScanCandidate> found;
        try { found = await _scanner.ScanAsync(); }
        catch (Exception ex)
        {
            AgentLogger.LogException("CommandPoller.UpdatePathCandidates", ex);
            return;
        }

        var reports = new List<ScanPathCandidate>();
        foreach (var game in unmapped)
        {
            var match = found.FirstOrDefault(c =>
                string.Equals(c.Name, game.Name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(c.SuggestedSaveDir));

            // Offering a path that is not there wastes the one click this exists to save.
            if (match?.SuggestedSaveDir is { } dir && Directory.Exists(dir))
                reports.Add(new ScanPathCandidate(game.GameId, Path.GetFullPath(dir)));
        }

        _health.SetPathCandidates(reports);
    }

    /// <summary>Best-effort: tell the server what save path this machine resolved for a game.</summary>
    private void ReportPathAsync(Guid gameId, string path) =>
        _ = Task.Run(async () =>
        {
            try { await _api().SetMachinePathAsync(gameId, path); }
            catch (Exception ex) { AgentLogger.LogException("CommandPoller.ReportPath", ex); }
        });

    // ----- command execution -----

    private async Task RunCommandsAsync()
    {
        var commands = await _api().GetAgentCommandsAsync();
        foreach (var cmd in commands)
        {
            try
            {
                var result = await ExecuteAsync(cmd);
                await _api().ReportCommandAsync(cmd.Id, CommandStatus.Done, result);
                _notify(result);
            }
            catch (Exception ex)
            {
                await SafeReportFailure(cmd.Id, ex.Message);
                _notify($"{cmd.Type} (from dashboard) failed: {ex.Message}");
            }
        }
    }

    private async Task<string> ExecuteAsync(AgentCommandDto cmd)
    {
        if (cmd.Type == AgentCommandType.Scan)
        {
            var candidates = await _scanner.ScanAsync();
            return $"found {candidates.Count} candidate(s): " +
                   string.Join(", ", candidates.Take(8).Select(c => c.Name)) +
                   (candidates.Count > 8 ? "…" : "");
        }

        // pull / push / sync target one game or all of this machine's games.
        var targets = TargetGames(cmd.GameId).ToList();
        if (targets.Count == 0)
            return "no matching mapped game on this machine.";

        var engine = _engine();
        foreach (var g in targets)
        {
            switch (cmd.Type)
            {
                case AgentCommandType.Pull:
                    await engine.PullAsync(g, cmd.Force);
                    break;
                case AgentCommandType.Push:
                    await engine.PushAsync(g, cmd.Force);
                    break;
                case AgentCommandType.Sync:
                    await engine.PullAsync(g, cmd.Force);
                    await engine.PushAsync(g, cmd.Force);
                    break;
            }
        }

        // One concise summary (the per-step engine progress goes to the log, not toasts).
        // For a single game, include the save's timestamp so the user can confirm it's current.
        var verb = cmd.Type.ToString().ToLowerInvariant() + "ed";
        if (targets.Count == 1)
        {
            var save = LatestSaveTimestamp(targets[0].SaveDirectory);
            return save is { } d
                ? $"{targets[0].Name} {verb} — latest save {d:MMM d, h:mm tt}"
                : $"{targets[0].Name} {verb}.";
        }
        return $"{verb} {targets.Count} games.";
    }

    /// <summary>Newest last-write time among a game's save files, or null if none/unreadable.</summary>
    private static DateTime? LatestSaveTimestamp(string dir)
    {
        try
        {
            DateTime? newest = null;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTime(f);
                if (newest is null || t > newest) newest = t;
            }
            return newest;
        }
        catch { return null; }
    }

    /// <summary>Mapped games matching the command's target (skips unmapped ones).</summary>
    private IEnumerable<TrackedGame> TargetGames(Guid? gameId) =>
        _config.Games.Where(g =>
            !string.IsNullOrEmpty(g.SaveDirectory) &&
            (gameId is null || g.GameId == gameId));

    private async Task SafeReportFailure(Guid commandId, string message)
    {
        try { await _api().ReportCommandAsync(commandId, CommandStatus.Failed, message); }
        catch { /* will be retried implicitly only if still pending; ignore */ }
    }

    public void Dispose() => _timer.Dispose();
}
