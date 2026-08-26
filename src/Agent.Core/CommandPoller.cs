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
    /// <summary>compatdata path for a Steam AppID. Host-supplied — Core cannot see Steam's layout.</summary>
    private readonly Func<string, string?>? _prefixForAppId;
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
        OfflineQueue? offlineQueue = null,
        Func<string, string?>? prefixForAppId = null)
    {
        _prefixForAppId = prefixForAppId;
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
                await _health.SendAsync(_api(), _config, _offlineQueue, _notify);

            Interlocked.Exchange(ref _busy, 0);
        }
    }

    // ----- game-list reconciliation (server → agent propagation) -----

    private async Task ReconcileGamesAsync()
    {
        var serverGames = await _api().ListGamesAsync();
        var serverById = serverGames.ToDictionary(g => g.Id);
        var changed = false;

        // Drop local games that were deleted on the server. Copy-on-write for the same reason as
        // enrollment: this runs on a timer thread while a UI thread enumerates the same list.
        var removed = 0;
        _config.MutateGames(list => removed = list.RemoveAll(g => !serverById.ContainsKey(g.GameId)));
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
                // "Highest authority" is not "unconditionally trusted": this is the one path source
                // with no local confirmation step at all, so a mistaken or hostile console value
                // would silently repoint a game at the user's profile. WA-02.
                if (!string.IsNullOrWhiteSpace(sg.MachineSavePath) &&
                    sg.MachineSavePath != local.SaveDirectory)
                {
                    var check = SavePathGuard.Check(sg.MachineSavePath, _config.StateDir);
                    if (!check.Ok)
                    {
                        // Not applied and not silent — the console sent it, so the console is where
                        // whoever set it is looking.
                        _health?.Report(AgentEventCodes.UnsafeSavePath, AgentEventSeverity.Error,
                            $"Refused the server's save folder for '{sg.Name}': {check.Reason} " +
                            $"(server sent '{sg.MachineSavePath}'). The previous mapping is unchanged.",
                            sg.Id);
                        AgentLogger.Log($"Refused server save path for '{sg.Name}': {check.Reason}");
                    }
                    else if (check.Canonical != local.SaveDirectory)
                    {
                        local.SaveDirectory = check.Canonical!;
                        changed = true;
                        _notify($"Updated '{sg.Name}' save folder from server: {check.Canonical}");
                    }
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
                // Mapped here, but the server has no path for this machine — say what we are using.
                // Without this the console shows "not set" for a machine that is syncing happily,
                // which reads as a broken agent and invites someone to "fix" it by setting a path.
                // It also never self-corrects: clearing the path in the console does not unmap the
                // agent, so the disagreement is permanent until the agent happens to re-resolve.
                else if (string.IsNullOrWhiteSpace(sg.MachineSavePath) &&
                         !string.IsNullOrWhiteSpace(local.SaveDirectory))
                {
                    ReportPathAsync(sg.Id, local.SaveDirectory);
                }

                // A machine that has a working path can describe it generically for the others.
                if (!string.IsNullOrWhiteSpace(local.SaveDirectory))
                    ReportTemplateAsync(sg, local.SaveDirectory);
                continue;
            }

            // Adopt a server game not tracked here yet — unless this machine opted out of it. The
            // game is still on the server for the rest of the fleet; this box just said no.
            if (_config.IsUntracked(sg.Id)) continue;

            var dir = await ResolveSaveDirAsync(sg);
            // If we resolved via detection (not from the server path), report it back.
            if (dir is not null && string.IsNullOrWhiteSpace(sg.MachineSavePath))
                ReportPathAsync(sg.Id, dir);

            // No HasSteamCloud here on purpose: it stays null ("nobody established this"). Only
            // enrollment has a ScanCandidate to decide it from, and GameDto carries no such signal —
            // re-deriving it from the name against the manifest is the exact mistake TrackedGame
            // .HasSteamCloud documents. Null lets the Decky plugin fall back to its own heuristic;
            // a false here would tell it, wrongly, that a Steam Cloud game definitely is not one.
            _config.MutateGames(list => list.Add(new TrackedGame
            {
                GameId = sg.Id,
                Name = sg.Name,
                ManifestKey = sg.ManifestKey,
                SaveDirectory = dir ?? "",
                ExcludeGlobs = (sg.ExcludeGlobs ?? Array.Empty<string>()).ToList()
            }));
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
    /// 2. SuggestedSaveDir, which may be a <b>template</b> (<c>&lt;winPublic&gt;/Documents/…</c>)
    ///    expanded against this machine, or a concrete path used as-is if it exists here.
    /// 3. Ludusavi manifest detection.
    /// Returns null if nothing resolves.
    /// </summary>
    private async Task<string?> ResolveSaveDirAsync(GameDto sg)
    {
        // Every return below goes through Safe(): adoption of a brand-new server game reaches the
        // config without passing the reconcile check above, and detection/template expansion can
        // land somewhere just as broad as a hostile server value. WA-02.
        if (!string.IsNullOrWhiteSpace(sg.MachineSavePath))
            return Safe(sg, sg.MachineSavePath);

        // A template beats a literal path, because it is the only form that means the same LOGICAL
        // folder on every machine. A literal from another machine is what lets two agents disagree
        // about the same game's save root by a segment — and that difference is what makes a restore
        // nest a folder under itself and delete the correctly-placed copy.
        if (PathResolver.IsTemplate(sg.SuggestedSaveDir) &&
            ResolverForGame(sg)?.ResolveToDirectory(sg.SuggestedSaveDir!) is { } expanded &&
            Directory.Exists(expanded))
            return Safe(sg, expanded);

        if (!string.IsNullOrWhiteSpace(sg.SuggestedSaveDir) &&
            !PathResolver.IsTemplate(sg.SuggestedSaveDir) &&
            Directory.Exists(sg.SuggestedSaveDir))
            return Safe(sg, sg.SuggestedSaveDir);
        try
        {
            var dirs = await _detection.ResolveSaveDirectoriesAsync(sg.ManifestKey ?? sg.Name);
            return dirs.FirstOrDefault() is { } d ? Safe(sg, d) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The canonical path if it is one we may ever sync, else null — leaving the game unmapped,
    /// which is the safe state. Reported, because an unmapped game otherwise looks like detection
    /// simply failing and invites someone to "fix" it by setting the very path we refused.
    /// </summary>
    private string? Safe(GameDto sg, string path)
    {
        var check = SavePathGuard.Check(path, _config.StateDir);
        if (check.Ok) return check.Canonical;

        _health?.Report(AgentEventCodes.UnsafeSavePath, AgentEventSeverity.Error,
            $"Refused a save folder for '{sg.Name}': {check.Reason} (was '{path}'). " +
            "The game is left unmapped; set its folder in Settings.", sg.Id);
        AgentLogger.Log($"Refused save path for '{sg.Name}': {check.Reason} (was '{path}')");
        return null;
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

    /// <summary>
    /// The resolver that expands Windows tokens the way THIS machine sees them.
    /// <para>
    /// On Windows that is the host's own known folders. Under Proton the same tokens mean folders
    /// <b>inside the game's prefix</b>, so the resolver is per-game and needs the game's AppID —
    /// which is why the host supplies the lookup (Core cannot see Steam's layout). A game with no
    /// AppID, or a prefix Steam has not created yet, resolves to nothing rather than to the host's
    /// own folders: pointing a Proton game at <c>~/Documents</c> would sync the wrong directory.
    /// </para>
    /// </summary>
    private PathResolver? ResolverForGame(GameDto sg)
    {
        if (OperatingSystem.IsWindows())
        {
            var tracked = _config.Games.FirstOrDefault(g => g.GameId == sg.Id);
            return PathResolver.Windows(tracked?.InstallDir);
        }

        var appId = _config.Games.FirstOrDefault(g => g.GameId == sg.Id)?.SteamAppId;
        if (string.IsNullOrWhiteSpace(appId)) return null;

        var compatData = _prefixForAppId?.Invoke(appId);
        if (string.IsNullOrWhiteSpace(compatData)) return null;

        // <root> comes off the prefix path itself; <base> is what the scanner recorded at
        // enrollment. Without these the poller expands a game's paths differently from the scanner
        // that first resolved them, and the two disagree about where the save lives.
        var storeRoot = SteamLayout.RootFromCompatData(compatData);
        var installDir = _config.Games.FirstOrDefault(g => g.GameId == sg.Id)?.InstallDir;
        return PathResolver.Proton(compatData, installDir, storeRoot);
    }

    /// <summary>
    /// If the server has no save location for this game, offer one described GENERICALLY.
    /// <para>
    /// This is the exact counterpart of the console's "Use as template" button, and a better source:
    /// the console matches on path layout because it cannot see another machine's environment, while
    /// this reverses the machine's own real token values. Under Proton it tokenizes against the
    /// game's own prefix, so a Deck describes <c>&lt;winDocuments&gt;/My Games/…</c> rather than a
    /// compatdata path no other machine could ever have.
    /// </para>
    /// <para>
    /// The server refuses to overwrite an existing value, so the first correctly-configured machine
    /// teaches the fleet and later ones are declined harmlessly.
    /// </para>
    /// </summary>
    private void ReportTemplateAsync(GameDto sg, string localPath)
    {
        if (!string.IsNullOrWhiteSpace(sg.SuggestedSaveDir)) return;
        if (ResolverForGame(sg)?.Tokenize(localPath) is not { } template) return;

        _ = Task.Run(async () =>
        {
            try
            {
                if (await _api().TrySetSaveTemplateAsync(sg.Id, template))
                    AgentLogger.Log($"Described '{sg.Name}' save location for the fleet: {template}");
            }
            catch (Exception ex) { AgentLogger.LogException("CommandPoller.ReportTemplate", ex); }
        });
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
            string result;
            try
            {
                result = await ExecuteAsync(cmd);
            }
            catch (Exception ex)
            {
                await SafeReportFailure(cmd.Id, ex.Message);
                _notify($"{cmd.Type} (from dashboard) failed: {ex.Message}");
                continue;
            }

            // Separate from the execution try/catch on purpose: the work is already done, so a
            // report that cannot be delivered must not be turned into "the command failed". The
            // server reclaims it when the lease expires and it runs again harmlessly.
            try { await _api().ReportCommandAsync(cmd.Id, CommandStatus.Done, result); }
            catch (Exception ex) { AgentLogger.LogException("CommandPoller.ReportSuccess", ex); }
            _notify(result);
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
        var running = new List<string>();
        foreach (var g in targets)
        {
            // A dashboard force-pull is the most dangerous surface there is: the person clicking it
            // is not sitting at the machine and cannot see that the game is open. Refusing here and
            // saying so in the command result is what puts the reason in front of them — the engine's
            // own refusal only reaches the agent's event stream. WA-01.
            var active = GameActivity.IsActive(g, out var proc);
            if (active) running.Add(proc is null ? g.Name : $"{g.Name} ({proc}.exe)");

            switch (cmd.Type)
            {
                case AgentCommandType.Pull:
                    if (!active) await engine.PullAsync(g, cmd.Force);
                    break;
                case AgentCommandType.Push:
                    await engine.PushAsync(g, cmd.Force);
                    break;
                case AgentCommandType.Sync:
                    if (!active) await engine.PullAsync(g, cmd.Force);
                    await engine.PushAsync(g, cmd.Force);
                    break;
            }
        }

        if (running.Count > 0 && cmd.Type == AgentCommandType.Pull)
            return $"REFUSED — still running: {string.Join(", ", running)}. " +
                   "Restoring saves under a live game loses them; close it and re-issue the pull.";

        // One concise summary (the per-step engine progress goes to the log, not toasts).
        // For a single game, include the save's timestamp so the user can confirm it's current.
        var verb = cmd.Type.ToString().ToLowerInvariant() + "ed";
        var suffix = running.Count > 0
            ? $" (not pulled, still running: {string.Join(", ", running)})"
            : "";
        if (targets.Count == 1)
        {
            var save = LatestSaveTimestamp(targets[0].SaveDirectory);
            return save is { } d
                ? $"{targets[0].Name} {verb} — latest save {d:MMM d, h:mm tt}{suffix}"
                : $"{targets[0].Name} {verb}.{suffix}";
        }
        return $"{verb} {targets.Count} games.{suffix}";
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

    /// <summary>
    /// Report a failure without letting the report itself become the failure. If this cannot reach
    /// the server the command keeps its claim until the server's visibility lease expires, and is
    /// then handed out again — the server treats delivery as at-least-once, so an unacknowledged
    /// command comes back rather than being lost. Re-running any command type is safe (see
    /// <c>SyncService.DequeueCommandsAsync</c>).
    /// </summary>
    private async Task SafeReportFailure(Guid commandId, string message)
    {
        try { await _api().ReportCommandAsync(commandId, CommandStatus.Failed, message); }
        catch (Exception ex) { AgentLogger.LogException("CommandPoller.SafeReportFailure", ex); }
    }

    public void Dispose() => _timer.Dispose();
}
