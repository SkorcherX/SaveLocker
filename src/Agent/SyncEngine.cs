using LocalGameSync.Shared;

namespace LocalGameSync.Agent;

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
    private readonly string _tempDir;
    private readonly OfflineQueue? _offlineQueue;

    public SyncEngine(AgentConfig config, ApiClient api, Action<string>? log = null, OfflineQueue? offlineQueue = null)
    {
        _config = config;
        _api = api;
        _log = log ?? (_ => { });
        _offlineQueue = offlineQueue;
        _tempDir = Path.Combine(AgentConfig.DefaultDir, "tmp");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Upload local saves to the server. Returns the upload outcome.</summary>
    public async Task<UploadResult?> PushAsync(TrackedGame game, bool force = false, CancellationToken ct = default)
    {
        if (!Directory.Exists(game.SaveDirectory))
        {
            _log($"[{game.Name}] save directory missing, nothing to push.");
            return null;
        }

        var hash = SaveArchive.HashDirectory(game.SaveDirectory);
        if (!force && hash == game.LastSyncedHash)
        {
            _log($"[{game.Name}] no local changes since last sync.");
            return new UploadResult(UploadStatus.NoChange, null, null);
        }

        var archive = Path.Combine(_tempDir, $"{game.GameId:N}-push.zip");
        SaveArchive.CreateArchive(game.SaveDirectory, archive);

        try
        {
            var result = await _api.UploadAsync(game.GameId, hash, game.LastKnownVersionId, force, archive, ct);
            switch (result.Status)
            {
                case UploadStatus.Created:
                    game.LastKnownVersionId = result.Version!.Id;
                    game.LastSyncedHash = hash;
                    _log($"[{game.Name}] pushed new version.");
                    break;
                case UploadStatus.NoChange:
                    game.LastKnownVersionId = result.Version?.Id ?? game.LastKnownVersionId;
                    game.LastSyncedHash = hash;
                    _log($"[{game.Name}] server already had this content.");
                    break;
                case UploadStatus.Conflict:
                    _log($"[{game.Name}] CONFLICT: your save diverged from the server. " +
                         "Resolve it in the dashboard.");
                    break;
            }
            _config.Save();
            return result;
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            _log($"[{game.Name}] server unreachable — queued for retry. ({ex.Message})");
            _offlineQueue?.Enqueue(game.GameId, game.Name, force);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient internal timeout fired (not a user cancellation).
            _log($"[{game.Name}] upload timed out — queued for retry.");
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
            var localHash = SaveArchive.HashDirectory(game.SaveDirectory);
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
                _log($"[{game.Name}] BLOCKED pull: local saves have changes not yet pushed and " +
                     "would be overwritten. Push first (your progress becomes the server version), " +
                     "or force-pull to discard local and take the server copy.");
                return false;
            }

            SaveArchive.RestoreArchive(archive, game.SaveDirectory, _tempDir);
            game.LastKnownVersionId = versionId;
            game.LastSyncedHash = headHash;
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
    /// save before the game starts writing. Returns false if the lease is held
    /// by another machine (caller decides whether to warn the user).
    /// </summary>
    public async Task<bool> OnGameLaunchAsync(TrackedGame game, CancellationToken ct = default)
    {
        var lease = await _api.AcquireLeaseAsync(game.GameId);
        if (!lease.Granted)
        {
            _log($"[{game.Name}] WARNING: saves are checked out by " +
                 $"'{lease.Lease.HolderMachineName}'. Pull/launch may overwrite their progress.");
            return false;
        }
        await PullAsync(game, force: false, ct);
        return true;
    }

    /// <summary>Post-exit: push the final save and release the lease.</summary>
    public async Task OnGameExitAsync(TrackedGame game, CancellationToken ct = default)
    {
        await PushAsync(game, force: false, ct);
        try { await _api.ReleaseLeaseAsync(game.GameId); }
        catch (Exception ex) { _log($"[{game.Name}] lease release failed: {ex.Message}"); }
    }
}
