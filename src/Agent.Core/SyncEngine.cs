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
    private readonly string _tempDir;
    private readonly OfflineQueue? _offlineQueue;
    private readonly TimeSpan _leaseRenewInterval = TimeSpan.FromHours(3);
    private readonly Dictionary<Guid, System.Threading.Timer> _leaseTimers = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _pushLocks = new();

    /// <param name="log">Routine progress — written to the agent log only.</param>
    /// <param name="notify">User-facing alerts (conflicts, blocks, offline retries). Both notified and logged.</param>
    public SyncEngine(AgentConfig config, ApiClient api, Action<string>? log = null, Action<string>? notify = null, OfflineQueue? offlineQueue = null)
    {
        _config = config;
        _api = api;
        _log = log ?? (_ => { });
        _notify = notify ?? (_ => { });
        _offlineQueue = offlineQueue;
        _tempDir = Path.Combine(AgentConfig.DefaultDir, "tmp");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>An event the user should see as a toast — also written to the log.</summary>
    private void Alert(string msg) { _log(msg); _notify(msg); }

    /// <summary>
    /// Upload local saves to the server. Returns the upload outcome.
    /// <paramref name="settle"/> waits for the game to finish writing before archiving — set it on
    /// automatic pushes (process-exit, folder-watch), where the save may still be mid-flush. Manual
    /// pushes leave it off: the user picked the moment and shouldn't wait on a timer.
    /// </summary>
    public async Task<UploadResult?> PushAsync(TrackedGame game, bool force = false, bool settle = false, CancellationToken ct = default)
    {
        // The exit push now waits for the save to settle, and the folder watcher fires during that
        // wait — without this, the two would archive and upload the same game concurrently.
        var gate = _pushLocks.GetOrAdd(game.GameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await PushCoreAsync(game, force, settle, ct); }
        finally { gate.Release(); }
    }

    private async Task<UploadResult?> PushCoreAsync(TrackedGame game, bool force, bool settle, CancellationToken ct)
    {
        if (!Directory.Exists(game.SaveDirectory))
        {
            _log($"[{game.Name}] save directory missing, nothing to push.");
            return null;
        }

        if (settle)
            await SaveSettler.WaitForQuietAsync(
                game.SaveDirectory, game.ExcludeGlobs,
                TimeSpan.FromSeconds(_config.SettleQuietSeconds),
                TimeSpan.FromSeconds(_config.SettleMaxWaitSeconds),
                m => _log($"[{game.Name}] {m}"), ct);

        var hash = SaveArchive.HashDirectory(game.SaveDirectory, game.ExcludeGlobs);
        if (!force && hash == game.LastSyncedHash)
        {
            _log($"[{game.Name}] no local changes since last sync.");
            return new UploadResult(UploadStatus.NoChange, null, null);
        }

        var archive = Path.Combine(_tempDir, $"{game.GameId:N}-push.zip");
        SaveArchive.CreateArchive(game.SaveDirectory, archive, game.ExcludeGlobs);

        try
        {
            var result = await _api.UploadAsync(game.GameId, hash, game.LastKnownVersionId, force, archive, ct);
            switch (result.Status)
            {
                case UploadStatus.Created:
                    game.LastKnownVersionId = result.Version!.Id;
                    game.LastSyncedHash = hash;
                    _config.TotalSavesPushed++;
                    _config.LastSyncTime = DateTime.UtcNow;
                    _log($"[{game.Name}] pushed new version.");
                    break;
                case UploadStatus.NoChange:
                    game.LastKnownVersionId = result.Version?.Id ?? game.LastKnownVersionId;
                    game.LastSyncedHash = hash;
                    _config.LastSyncTime = DateTime.UtcNow;
                    _log($"[{game.Name}] server already had this content.");
                    break;
                case UploadStatus.Conflict:
                    Alert($"[{game.Name}] CONFLICT: your save diverged from the server. " +
                         "Resolve it in the dashboard.");
                    break;
            }
            _config.Save();
            return result;
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            Alert($"[{game.Name}] server unreachable — queued for retry. ({ex.Message})");
            _offlineQueue?.Enqueue(game.GameId, game.Name, force);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient internal timeout fired (not a user cancellation).
            Alert($"[{game.Name}] upload timed out — queued for retry.");
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
        var archive = Path.Combine(_tempDir, $"{game.GameId:N}-pull.zip");
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
                _config.Save();
                _log($"[{game.Name}] already up to date.");
                return false;
            }

            // Safety: never silently overwrite local saves that hold changes which
            // were never pushed (e.g. the real progress on a machine's first sync).
            // localHash != LastSyncedHash means local has un-pushed edits — or this
            // machine has never synced and already has save data.
            var hasUnsyncedLocal = HasLocalData(game.SaveDirectory) && localHash != game.LastSyncedHash;
            if (hasUnsyncedLocal && !force)
            {
                Alert($"[{game.Name}] BLOCKED pull: local saves have changes not yet pushed and " +
                     "would be overwritten. Push first (your progress becomes the server version), " +
                     "or force-pull to discard local and take the server copy.");
                return false;
            }

            SaveArchive.RestoreArchive(archive, game.SaveDirectory, _tempDir);
            game.LastKnownVersionId = versionId;
            game.LastSyncedHash = headHash;
            _config.LastSyncTime = DateTime.UtcNow;
            _config.Save();
            _log($"[{game.Name}] restored latest save from server.");
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
                 "Launched without pulling — a conflict may occur on exit.");
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
