using LocalGameSync.Shared;

namespace LocalGameSync.Agent;

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
    private readonly Action<string> _notify;
    private readonly Action _onGamesChanged;
    private readonly System.Timers.Timer _timer;
    private int _busy; // 0/1 guard so slow ticks don't overlap

    public CommandPoller(
        AgentConfig config,
        Func<ApiClient> api,
        Func<SyncEngine> engine,
        Detection detection,
        Action<string> notify,
        Action onGamesChanged,
        double pollMs = 20000)
    {
        _config = config;
        _api = api;
        _engine = engine;
        _detection = detection;
        _notify = notify;
        _onGamesChanged = onGamesChanged;
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
            await RunCommandsAsync();
        }
        catch (Exception ex)
        {
            AgentLogger.LogException("CommandPoller.TickAsync", ex);
        }
        finally
        {
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
                SaveDirectory = dir ?? ""
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
                _notify($"{cmd.Type} (from dashboard): {result}");
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
            var scanner = new GameScanner(_detection);
            var candidates = await scanner.ScanAsync();
            return $"found {candidates.Count} candidate(s): " +
                   string.Join(", ", candidates.Take(8).Select(c => c.Name)) +
                   (candidates.Count > 8 ? "…" : "");
        }

        // pull / push / sync target one game or all of this machine's games.
        var targets = TargetGames(cmd.GameId).ToList();
        if (targets.Count == 0)
            return "no matching mapped game on this machine.";

        var engine = _engine();
        var n = 0;
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
            n++;
        }
        return $"{cmd.Type.ToString().ToLowerInvariant()}ed {n} game(s).";
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
