using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Coordinates a tracked game's saves with the server: archive + hash + upload
/// (push), download + restore (pull), and the pre-launch / post-exit flows.
/// Updates the persisted config with the last-known version and hash so the
/// next push carries the correct parent for conflict detection.
/// </summary>
public sealed class SyncEngine
{
    private readonly AgentConfig _config;
    private readonly ApiClient _api;
    private readonly Action<string> _log;
    private readonly Action<string> _notify;
    private readonly HealthReporter? _health;
    private readonly string _tempDir;
    private readonly OfflineQueue? _offlineQueue;
    private readonly TimeSpan _leaseRenewInterval = TimeSpan.FromHours(3);
    private readonly Dictionary<Guid, System.Threading.Timer> _leaseTimers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _pushLocks = new();

    /// <param name="log">Routine progress — written to the agent log only.</param>
    /// <param name="notify">User-facing alerts (conflicts, blocks, offline retries). Both notified and logged.</param>
    /// <param name="health">
    /// Reports the same alerts to the server. On Windows <paramref name="notify"/> raises a toast;
    /// on a headless Deck it can only write a log line nobody reads — so the console has to be told
    /// (Decisions.md §2). Both hosts report, so the console is one honest view of the whole fleet.
    /// </param>
    public SyncEngine(AgentConfig config, ApiClient api, Action<string>? log = null, Action<string>? notify = null,
        OfflineQueue? offlineQueue = null, HealthReporter? health = null)
    {
        _config = config;
        _api = api;
        _log = log ?? (_ => { });
        _notify = notify ?? (_ => { });
        _health = health;
        _offlineQueue = offlineQueue;
        _tempDir = Path.Combine(config.StateDir, "tmp");
        Directory.CreateDirectory(_tempDir);
        CleanStaleTemps();
    }

    /// <summary>
    /// A temp archive name that no other process can be using. It used to be
    /// <c>{gameId}-push.zip</c>, shared by every process on the box: the daemon's watch-push and the
    /// launch wrapper's exit-push for the same game wrote the same file at the same time, and
    /// whichever finished first had its archive deleted mid-upload by the other's <c>finally</c>.
    /// </summary>
    private string TempArchive(Guid gameId, string kind) =>
        Path.Combine(_tempDir, $"{gameId:N}-{kind}-{Environment.ProcessId}-{Guid.NewGuid():N}.zip");

    /// <summary>
    /// Sweep archives abandoned by a process that was killed mid-sync (a Deck suspending, Steam
    /// force-closing the wrapper). Unique names mean nothing reclaims them otherwise, and a save
    /// archive is not small.
    /// </summary>
    private void CleanStaleTemps()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(6);
            foreach (var file in Directory.EnumerateFiles(_tempDir, "*.zip"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* in use by a live process, or not ours to delete */ }
            }
        }
        catch { /* the sweep is a courtesy; never let it break startup */ }
    }

    /// <summary>An event the user should see as a toast — also written to the log.</summary>
    private void Alert(string msg) { _log(msg); _notify(msg); }

    /// <summary>
    /// An event the user must see, on <i>any</i> host: toasted where a toast is possible, logged
    /// always, and reported to the console — which is the only place a Deck's owner will ever see it.
    /// </summary>
    private void Alert(string msg, string code, AgentEventSeverity severity, Guid? gameId)
    {
        Alert(msg);
        _health?.Report(code, severity, msg, gameId);
    }

    /// <summary>A condition worth reporting that was never worth a toast (it is not a user action).</summary>
    private void ReportOnly(string msg, string code, AgentEventSeverity severity, Guid? gameId)
    {
        _log(msg);
        _health?.Report(code, severity, msg, gameId);
    }

    /// <summary>
    /// Upload local saves to the server. Returns the upload outcome.
    /// <paramref name="settle"/> waits for the game to finish writing before archiving — set it on
    /// automatic pushes (process-exit, folder-watch), where the save may still be mid-flush. Manual
    /// pushes leave it off: the user picked the moment and shouldn't wait on a timer.
    /// </summary>
    public async Task<UploadResult?> PushAsync(TrackedGame game, bool force = false, bool settle = false, CancellationToken ct = default)
    {
        // Two layers, and both are needed. The semaphore stops two THREADS here racing: the exit
        // push waits for the save to settle, and the folder watcher fires during that wait. The
        // file lock stops two PROCESSES racing — the daemon and the Steam launch wrapper sync the
        // same game — which a semaphore cannot do, and a flock alone cannot do either (it is held
        // per process, so both threads would sail through it).
        var gate = _pushLocks.GetOrAdd(game.GameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            using var crossProcess = AgentStateLock.ForGame(game.GameId, _config.StateDir);
            return await PushCoreAsync(game, force, settle, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<UploadResult?> PushCoreAsync(TrackedGame game, bool force, bool settle, CancellationToken ct)
    {
        if (!Directory.Exists(game.SaveDirectory))
        {
            // Not a toast — but on a headless box this is the difference between "syncing fine" and
            // "silently syncing nothing", so the console must hear about it.
            ReportOnly($"[{game.Name}] save directory missing, nothing to push.",
                AgentEventCodes.SaveDirMissing, AgentEventSeverity.Warning, game.GameId);
            return null;
        }

        if (settle)
        {
            var quiet = await SaveSettler.WaitForQuietAsync(
                game.SaveDirectory, game.ExcludeGlobs,
                TimeSpan.FromSeconds(_config.SettleQuietSeconds),
                TimeSpan.FromSeconds(_config.SettleMaxWaitSeconds),
                m => _log($"[{game.Name}] {m}"), ct);

            // The gate gave up and pushed anyway: the snapshot may be mid-write. It still uploads
            // (a stale-but-complete version beats none), but the user deserves to know it happened.
            if (!quiet)
                ReportOnly($"[{game.Name}] save never went quiet within {_config.SettleMaxWaitSeconds}s — " +
                           "archived anyway; the snapshot may be mid-write.",
                    AgentEventCodes.SettleTimeout, AgentEventSeverity.Warning, game.GameId);
        }

        var hash = SaveArchive.HashDirectory(game.SaveDirectory, game.ExcludeGlobs);
        if (!force && hash == game.LastSyncedHash)
        {
            _log($"[{game.Name}] no local changes since last sync.");
            return new UploadResult(UploadStatus.NoChange, null, null);
        }

        var archive = TempArchive(game.GameId, "push");
        SaveArchive.CreateArchive(game.SaveDirectory, archive, game.ExcludeGlobs);

        try
        {
            var result = await _api.UploadAsync(game.GameId, hash, game.LastKnownVersionId, force, archive, ct);
            var countPush = false;
            var touchSyncTime = false;
            switch (result.Status)
            {
                case UploadStatus.Created:
                    game.LastKnownVersionId = result.Version!.Id;
                    game.LastSyncedHash = hash;
                    countPush = true;
                    touchSyncTime = true;
                    _log($"[{game.Name}] pushed new version.");
                    _health?.MarkSynced(game.GameId);
                    break;
                case UploadStatus.NoChange:
                    game.LastKnownVersionId = result.Version?.Id ?? game.LastKnownVersionId;
                    game.LastSyncedHash = hash;
                    touchSyncTime = true;
                    _log($"[{game.Name}] server already had this content.");
                    _health?.MarkSynced(game.GameId);
                    break;
                case UploadStatus.Conflict:
                    // The server already recorded the ConflictFlag, so the dashboard knows a conflict
                    // exists. What it cannot know is WHICH machine is stuck behind it — that is this.
                    Alert($"[{game.Name}] CONFLICT: your save diverged from the server. " +
                         "Resolve it in the dashboard.",
                        AgentEventCodes.Conflict, AgentEventSeverity.Error, game.GameId);
                    break;
            }
            _config.SaveGameSyncState(game, countPush, touchSyncTime);
            return result;
        }
        // A non-null StatusCode means the server ANSWERED and rejected us (e.g. 413: the save blew
        // past the upload cap). That is not a network drop, and queueing it for retry would loop
        // forever on a request that can never succeed — so it is reported, not queued. This clause
        // must come first: HttpRequestException covers both cases, and the drop clause below would
        // otherwise swallow rejections and mislabel them "server unreachable".
        catch (HttpRequestException ex) when (ex.StatusCode is not null && !ct.IsCancellationRequested)
        {
            Alert($"[{game.Name}] push rejected by the server ({(int)ex.StatusCode}): {ex.Message}",
                AgentEventCodes.PushFailed, AgentEventSeverity.Error, game.GameId);
            return null;
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            // Reporting this NOW is impossible — the server is the thing we cannot reach. It is
            // persisted and goes out on the first heartbeat after contact returns. That round trip
            // is the whole reason HealthReporter writes to disk.
            Alert($"[{game.Name}] server unreachable — queued for retry. ({ex.Message})",
                AgentEventCodes.ServerUnreachable, AgentEventSeverity.Error, game.GameId);
            _offlineQueue?.Enqueue(game.GameId, game.Name, force);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient internal timeout fired (not a user cancellation).
            Alert($"[{game.Name}] upload timed out — queued for retry.",
                AgentEventCodes.ServerUnreachable, AgentEventSeverity.Error, game.GameId);
            _offlineQueue?.Enqueue(game.GameId, game.Name, force);
            return null;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    /// <summary>Download the server head and restore it locally if it differs.</summary>
    public async Task<bool> PullAsync(TrackedGame game, bool force = false, CancellationToken ct = default)
    {
        var archive = TempArchive(game.GameId, "pull");
        using var crossProcess = AgentStateLock.ForGame(game.GameId, _config.StateDir);
        try
        {
            var head = await _api.DownloadHeadAsync(game.GameId, archive, ct);
            if (head is null)
            {
                _log($"[{game.Name}] server has no saves yet; nothing to pull.");
                return false;
            }

            var (versionId, headHash) = head.Value;
            var localHash = SaveArchive.HashDirectory(game.SaveDirectory, game.ExcludeGlobs);
            if (localHash == headHash)
            {
                game.LastKnownVersionId = versionId;
                game.LastSyncedHash = headHash;
                _config.SaveGameSyncState(game);
                _log($"[{game.Name}] already up to date.");
                _health?.MarkSynced(game.GameId);
                return false;
            }

            // Safety: never silently overwrite local saves that hold changes which
            // were never pushed (e.g. the real progress on a machine's first sync).
            // localHash != LastSyncedHash means local has un-pushed edits — or this
            // machine has never synced and already has save data.
            var hasUnsyncedLocal = HasLocalData(game.SaveDirectory) && localHash != game.LastSyncedHash;
            if (hasUnsyncedLocal && !force)
            {
                // The machine is now stuck: it will not pull, and it cannot push without conflicting.
                // On a Deck nobody is being told that — this event is the only thing that says so.
                Alert($"[{game.Name}] BLOCKED pull: local saves have changes not yet pushed and " +
                     "would be overwritten. Push first (your progress becomes the server version), " +
                     "or force-pull to discard local and take the server copy.",
                    AgentEventCodes.PullBlocked, AgentEventSeverity.Error, game.GameId);
                return false;
            }

            try
            {
                SaveArchive.RestoreArchive(archive, game.SaveDirectory, _tempDir);
            }
            catch (SaveArchive.UnsafeArchiveException ex)
            {
                // The server sent something we refuse to write — a zip bomb, an escaping path, or a
                // destination that traverses a symlink. Nothing was restored. This must be loud:
                // silently declining to pull looks identical to "already up to date", and on a Deck
                // the console is the only place anyone would ever find out (Decisions.md §2).
                Alert($"[{game.Name}] REFUSED the server's save: {ex.Message}",
                    AgentEventCodes.PullBlocked, AgentEventSeverity.Error, game.GameId);
                return false;
            }
            game.LastKnownVersionId = versionId;
            game.LastSyncedHash = headHash;
            _config.SaveGameSyncState(game, touchSyncTime: true);
            _log($"[{game.Name}] restored latest save from server.");
            _health?.MarkSynced(game.GameId);
            return true;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    private static bool HasLocalData(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Pre-launch: take the lease (warn if held elsewhere) and pull the latest
    /// save before the game starts writing.
    /// Returns (Granted: false, HolderMachineName) if another machine holds the lease.
    /// </summary>
    public async Task<(bool Granted, string? HolderMachineName)> OnGameLaunchAsync(TrackedGame game, CancellationToken ct = default)
    {
        var lease = await _api.AcquireLeaseAsync(game.GameId);
        if (!lease.Granted)
        {
            var holder = lease.Lease.HolderMachineName;
            Alert($"[{game.Name}] WARNING: saves are checked out by '{holder}'. " +
                 "Launched without pulling — a conflict may occur on exit.",
                AgentEventCodes.LeaseHeldElsewhere, AgentEventSeverity.Warning, game.GameId);
            return (false, holder);
        }
        StartLeaseRenewer(game);
        await PullAsync(game, force: false, ct);
        return (true, null);
    }

    /// <summary>Post-exit: push the final save and release the lease.</summary>
    public async Task OnGameExitAsync(TrackedGame game, CancellationToken ct = default)
    {
        StopLeaseRenewer(game.GameId);
        await PushAsync(game, force: false, settle: true, ct: ct);
        try { await _api.ReleaseLeaseAsync(game.GameId); }
        catch (Exception ex) { _log($"[{game.Name}] lease release failed: {ex.Message}"); }
    }

    private void StartLeaseRenewer(TrackedGame game)
    {
        StopLeaseRenewer(game.GameId);
        var timer = new System.Threading.Timer(
            async _ =>
            {
                try
                {
                    var ok = await _api.RenewLeaseAsync(game.GameId);
                    _log(ok
                        ? $"[{game.Name}] lease renewed."
                        : $"[{game.Name}] lease renewal failed — lease may have been force-released.");
                }
                catch (Exception ex) { _log($"[{game.Name}] lease renewal error: {ex.Message}"); }
            },
            null, _leaseRenewInterval, _leaseRenewInterval);
        lock (_leaseTimers) _leaseTimers[game.GameId] = timer;
    }

    private void StopLeaseRenewer(Guid gameId)
    {
        System.Threading.Timer? timer;
        lock (_leaseTimers) { _leaseTimers.TryGetValue(gameId, out timer); _leaseTimers.Remove(gameId); }
        timer?.Dispose();
    }
}
