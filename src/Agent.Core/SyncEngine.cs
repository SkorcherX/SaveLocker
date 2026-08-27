using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Coordinates a tracked game's saves with the server: archive + hash + upload
/// (push), download + restore (pull), and the pre-launch / post-exit flows.
/// Updates the persisted config with the last-known version and hash so the
/// next push carries the correct parent for conflict detection.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable, IDisposable
{
    private readonly AgentConfig _config;
    private readonly ApiClient _api;
    private readonly Action<string> _log;
    private readonly Action<string> _notify;
    private readonly HealthReporter? _health;
    private readonly string _tempDir;
    private readonly OfflineQueue? _offlineQueue;
    private readonly SyncActivityTracker? _activity;
    private readonly TimeSpan _leaseRenewInterval = ResolveRenewInterval();
    private const int ConflictUploadLimit = 3;
    private readonly Dictionary<Guid, System.Threading.Timer> _leaseTimers = new();

    /// <summary>
    /// The origin this engine was built for, captured at construction. <see cref="_config"/> is
    /// shared and mutable — a settings change rewrites <c>ServerUrl</c> underneath every engine that
    /// holds it — so the config cannot answer "which server issued my leases?". This can.
    /// </summary>
    private readonly string _origin;

    /// <summary>
    /// Cancels work in flight when the engine is retired. A renewal callback that has already been
    /// scheduled must not fire a request, reschedule itself, or report success afterwards.
    /// </summary>
    private readonly CancellationTokenSource _retired = new();
    private int _disposed;

    /// <summary>True once the engine has been retired; every network path checks it.</summary>
    public bool IsRetired => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Three hours in production. <c>SAVELOCKER_LEASE_RENEW_SECONDS</c> shortens it so a test can
    /// observe several renewals in a few seconds instead of half a day — there is no other way to
    /// prove a retired engine stopped renewing, since the interval is the whole thing being tested.
    /// Clamped, and ignored entirely if it is not a positive number.
    /// </summary>
    private static TimeSpan ResolveRenewInterval()
    {
        var raw = Environment.GetEnvironmentVariable("SAVELOCKER_LEASE_RENEW_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 24 * 3600))
            : TimeSpan.FromHours(3);
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _pushLocks = new();

    /// <param name="log">Routine progress — written to the agent log only.</param>
    /// <param name="notify">User-facing alerts (conflicts, blocks, offline retries). Both notified and logged.</param>
    /// <param name="health">
    /// Reports the same alerts to the server. On Windows <paramref name="notify"/> raises a toast;
    /// on a headless Deck it can only write a log line nobody reads — so the console has to be told
    /// (Decisions.md §2). Both hosts report, so the console is one honest view of the whole fleet.
    /// </param>
    public SyncEngine(AgentConfig config, ApiClient api, Action<string>? log = null, Action<string>? notify = null,
        OfflineQueue? offlineQueue = null, HealthReporter? health = null, SyncActivityTracker? activity = null)
    {
        _config = config;
        _api = api;
        _origin = config.ServerUrl;
        _log = log ?? (_ => { });
        _notify = notify ?? (_ => { });
        _health = health;
        _offlineQueue = offlineQueue;
        _activity = activity;
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
        // A retired engine is still reachable: the offline-queue drainer and the folder watchers
        // captured a reference to it before the connection changed. Refusing here is what stops
        // that stale reference pushing a save to the previous server. WA-06.
        if (RefuseIfRetired(game, "push")) return null;

        using var linked = LinkRetirement(ct);
        ct = linked.Token;

        var gate = _pushLocks.GetOrAdd(game.GameId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        // Brackets the whole attempt regardless of which exit path below is taken (success, no
        // change, conflict, contention, or an exception) — the alternative was threading Begin/End
        // through every return in PushCoreAsync, and a phase change mid-attempt (settle -> upload)
        // is handled there instead, once the state that matters (which game, when it started) exists.
        _activity?.Begin(game.Name, settle ? SyncPhase.Settling : SyncPhase.Pushing);
        try
        {
            // Fails closed. Proceeding unlocked here meant this process archived and rewrote sync
            // state while another was doing the same to the same directory. WA-07.
            using var crossProcess = AgentStateLock.ForGame(
                game.GameId, _config.StateDir, GameLockTimeout, ct);
            // Inside the lock, so the parent we read cannot be superseded between here and the
            // upload. A long-lived host (the daemon, the tray) has been holding this object since it
            // started; the launch wrapper may have pushed since, and presenting the boot-time parent
            // is what makes the server record a conflict on a single-machine fleet.
            _config.RefreshGameSyncState(game);
            return await PushCoreAsync(game, force, settle, ct);
        }
        catch (AgentStateLockException ex)
        {
            // A typed failure, not a crash: the tray, the CLI and the dashboard all need to say
            // "another process owns this game" rather than surface a stack trace.
            ReportContention(game, "push", ex);
            return null;
        }
        finally { gate.Release(); _activity?.End(); }
    }

    private async Task<UploadResult?> PushCoreAsync(TrackedGame game, bool force, bool settle, CancellationToken ct)
    {
        // Re-checked at the boundary, not merely where the path was configured. A mapping accepted
        // last week can become dangerous without anyone touching this agent's UI: config.json is
        // hand-editable, the server can push a MachineSavePath, and a junction can be re-pointed at
        // the user's profile after the fact. WA-02.
        if (RefuseUnsafePath(game, "push")) return null;

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

        // Whether or not settle ran, everything from here on is the "Pushing" phase — settle only
        // ever set Settling in the first place when it was going to run at all.
        _activity?.SetPhase(SyncPhase.Pushing);

        // One pass over the save folder answers both questions a push asks of it: the aggregate
        // content hash (has anything changed at all?) and the per-file manifest a delta negotiates
        // with. Hashing the directory and then building a manifest read every byte twice.
        var (manifest, hash) = SaveArchive.ComputeManifest(game.SaveDirectory, game.ExcludeGlobs);
        if (!force && hash == game.LastSyncedHash)
        {
            _log($"[{game.Name}] no local changes since last sync.");
            return new UploadResult(UploadStatus.NoChange, null, null);
        }

        if (!force && game.ConsecutiveConflicts >= ConflictUploadLimit)
        {
            ReportOnly(
                $"[{game.Name}] CONFLICT still unresolved — upload paused after " +
                $"{game.ConsecutiveConflicts} rejected attempts; no archive was sent. " +
                "Resolve it in the dashboard or force-push explicitly.",
                AgentEventCodes.Conflict, AgentEventSeverity.Error, game.GameId);
            return new UploadResult(UploadStatus.Conflict, null, null);
        }

        try
        {
            var result = await SendPushAsync(game, hash, manifest, force, ct);
            if (result is null) return null;   // refused outright; SendPushAsync already alerted

            var countPush = false;
            var touchSyncTime = false;
            switch (result.Status)
            {
                case UploadStatus.Created:
                    game.LastKnownVersionId = result.Version!.Id;
                    game.LastSyncedHash = hash;
                    game.ConsecutiveConflicts = 0;
                    countPush = true;
                    touchSyncTime = true;
                    _log($"[{game.Name}] pushed new version.");
                    _health?.MarkSynced(game.GameId);
                    break;
                case UploadStatus.NoChange:
                    game.LastKnownVersionId = result.Version?.Id ?? game.LastKnownVersionId;
                    game.LastSyncedHash = hash;
                    game.ConsecutiveConflicts = 0;
                    touchSyncTime = true;
                    _log($"[{game.Name}] server already had this content.");
                    _health?.MarkSynced(game.GameId);
                    break;
                case UploadStatus.Conflict:
                    game.ConsecutiveConflicts++;
                    // The server already recorded the ConflictFlag, so the dashboard knows a conflict
                    // exists. What it cannot know is WHICH machine is stuck behind it — that is this.
                    Alert(game.ConsecutiveConflicts >= ConflictUploadLimit
                            ? $"[{game.Name}] CONFLICT: your save diverged from the server. " +
                              $"Automatic uploads are now paused after {game.ConsecutiveConflicts} attempts " +
                              "until the conflict is resolved and pulled."
                            : $"[{game.Name}] CONFLICT: your save diverged from the server. " +
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
    }

    /// <summary>
    /// Bytes below which a per-file delta is pure overhead: an ordinary push is already one round
    /// trip, and the manifest it would carry is not worth paying for on a save this small.
    /// <para>
    /// There is deliberately no file-COUNT floor beside it. A game keeping three 400 MB slots is
    /// exactly the case a delta exists for, and a count floor would send it down the full-archive
    /// path forever — re-uploading gigabytes to change one slot.
    /// </para>
    /// </summary>
    private const long DeltaTotalBytesFloor = 2 * 1024 * 1024;

    /// <summary>
    /// Decide what this push actually sends, and send it. A forced push — and a first-ever one, with
    /// no parent to diff against — is unconditionally a full archive: those are exactly the cases
    /// where the server cannot assume what "the old content" was to copy forward from. Everything
    /// else negotiates a per-file delta, and falls back to a full archive within the SAME session
    /// whenever the server declines one.
    /// <para>
    /// This lives here rather than in <see cref="ApiClient"/> because it is a sync-policy question,
    /// not a transport one; ApiClient's job is Begin, stream, Complete. Returns null when the push
    /// was refused outright and the caller should treat it as a failed attempt.
    /// </para>
    /// </summary>
    private async Task<UploadResult?> SendPushAsync(
        TrackedGame game, string hash, IReadOnlyList<FileManifestEntry> manifest, bool force,
        CancellationToken ct)
    {
        void Progress(long done, long total) => _activity?.Progress(done, total);

        var deltaWorthwhile = !force
            && game.LastKnownVersionId is not null
            && manifest.Sum(f => f.Size) >= DeltaTotalBytesFloor;
        if (!deltaWorthwhile)
            return await SendFullArchiveAsync(game, hash, force, Progress, ct);

        var begin = await _api.BeginUploadAsync(
            game.GameId, hash, game.LastKnownVersionId, force: false, manifest.ToArray(), ct);
        if (begin is null)                                    // server predates the chunked routes
            return await SendFullArchiveAsync(game, hash, force, Progress, ct);
        if (begin.NoChange is { } noChange) return noChange;

        var payload = TempArchive(game.GameId, "delta");
        try
        {
            if (begin.UseDeltaPath)
            {
                var needPaths = begin.NeedPaths ?? Array.Empty<string>();
                if (FirstUndeclaredPath(needPaths, manifest) is { } rogue)
                {
                    // The server may only ask for files this agent just told it about. Anything else
                    // is a server reaching for a file on this machine that is not part of the save —
                    // the upload-side mirror of the hostile-archive rule RestoreArchive applies on
                    // pull (Decisions.md §4), and the same reason an enrollment file cannot be taken
                    // at face value. Refused outright rather than quietly falling back to a full
                    // archive, because a fallback would still answer a request that should never
                    // have been made, and nothing would ever say so.
                    Alert($"[{game.Name}] REFUSED the server's delta request: it asked for " +
                          $"'{rogue}', which is not part of this game's saves. Nothing was uploaded.",
                        AgentEventCodes.PushFailed, AgentEventSeverity.Error, game.GameId);
                    return null;
                }
                SaveArchive.CreateArchiveSubset(game.SaveDirectory, payload, needPaths);
            }
            else
            {
                SaveArchive.CreateArchive(game.SaveDirectory, payload, game.ExcludeGlobs);
            }

            var result = await _api.UploadSessionPayloadAsync(game.GameId, begin.SessionId!.Value,
                payload, Progress, ct);
            if (result.Status != UploadStatus.RetryFull) return result;
        }
        finally { TryDeleteTemp(payload); }

        // The negotiated base went stale between Begin and Complete — another machine pushed in
        // between, so the delta no longer reconstructs a complete version. Retry ONCE with a full
        // archive against the same (now stale) parent: the server re-runs its checks fresh and
        // reports a conflict if the content genuinely diverged, or NoChange if it happens to match
        // — the two outcomes any ordinary push could reach. RetryFull never escapes this method.
        return await SendFullArchiveAsync(game, hash, force, Progress, ct);
    }

    private async Task<UploadResult> SendFullArchiveAsync(
        TrackedGame game, string hash, bool force, Action<long, long> onProgress, CancellationToken ct)
    {
        var archive = TempArchive(game.GameId, "push");
        try
        {
            SaveArchive.CreateArchive(game.SaveDirectory, archive, game.ExcludeGlobs);
            return await _api.UploadAsync(
                game.GameId, hash, game.LastKnownVersionId, force, archive, onProgress, ct);
        }
        finally { TryDeleteTemp(archive); }
    }

    /// <summary>The first path the server asked for that this agent never declared, or null if the
    /// request is entirely within what was offered.</summary>
    private static string? FirstUndeclaredPath(
        IEnumerable<string> needPaths, IReadOnlyList<FileManifestEntry> manifest)
    {
        var declared = new HashSet<string>(manifest.Select(f => f.Path), StringComparer.Ordinal);
        return needPaths.FirstOrDefault(p => !declared.Contains(p));
    }

    private static void TryDeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Download the server head and restore it locally if it differs.</summary>
    public async Task<bool> PullAsync(TrackedGame game, bool force = false, CancellationToken ct = default)
    {
        if (RefuseIfRetired(game, "pull")) return false;
        using var linked = LinkRetirement(ct);
        ct = linked.Token;

        // The backstop for WA-01. Every caller is expected to have checked already so it can word
        // the refusal for its own surface, but this is the check that is actually load-bearing: it
        // is the only one no tray action, dashboard command, CLI invocation, or lifecycle callback
        // can route around.
        if (GameActivity.IsActive(game, out var activeProc))
        {
            Alert(GameActivity.RefusalMessage(game, activeProc),
                AgentEventCodes.PullBlockedRunning, AgentEventSeverity.Error, game.GameId);
            return false;
        }

        // A pull is the direction that DELETES. Whatever this path points at now is what a
        // force-pull is about to replace, so it is checked here and again below. WA-02.
        if (RefuseUnsafePath(game, "pull")) return false;

        var archive = TempArchive(game.GameId, "pull");
        AgentStateLock crossProcess;
        try
        {
            // Fails closed, and this is the direction where that matters most: proceeding unlocked
            // meant restoring over a save directory another process was mid-archive on. WA-07.
            crossProcess = AgentStateLock.ForGame(game.GameId, _config.StateDir, GameLockTimeout, ct);
        }
        catch (AgentStateLockException ex)
        {
            ReportContention(game, "pull", ex);
            return false;
        }

        using var _lock = crossProcess;
        // Begin here, not before the lock: the two early refusals above (running, unsafe path) never
        // attempted a pull at all, so there is nothing for the activity feed to show in progress.
        _activity?.Begin(game.Name, SyncPhase.Pulling);
        // LastSyncedHash gates the un-pushed-changes check below, so a stale one is not merely
        // untidy: it makes a legitimate pull look like it would overwrite local progress, and the
        // pull is refused. Refresh before either is read.
        _config.RefreshGameSyncState(game);
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
                game.ConsecutiveConflicts = 0;
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

            // Re-checked here and not only at entry: the download above can take minutes on a large
            // save, and the user is perfectly capable of starting the game during it. This is the
            // check that sits closest to the destructive write, so it is the one that must be last.
            if (GameActivity.IsActive(game, out var lateProc))
            {
                Alert(GameActivity.RefusalMessage(game, lateProc),
                    AgentEventCodes.PullBlockedRunning, AgentEventSeverity.Error, game.GameId);
                return false;
            }

            // Re-canonicalized here for the same reason the activity check is: the download took
            // time, and a reparse point can be re-pointed during it. This check sits closest to the
            // delete-and-replace, so it is the last word.
            if (RefuseUnsafePath(game, "pull")) return false;

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
            game.ConsecutiveConflicts = 0;
            _config.SaveGameSyncState(game, touchSyncTime: true);
            _log($"[{game.Name}] restored latest save from server.");
            _health?.MarkSynced(game.GameId);
            return true;
        }
        catch (AgentStateLockException ex)
        {
            // The per-game lock is held above, but the sync-state write takes the separate 'config'
            // lock and can be contended on its own. Same answer either way: report it, change nothing.
            ReportContention(game, "pull", ex);
            return false;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            _activity?.End();
        }
    }

    /// <summary>
    /// Pull then push every game, skipping the pull (not the push) for whatever is currently running
    /// — the same "sync all" both the tray menu and the agent UI's Overview button offer, kept in one
    /// place so the two cannot drift. A running game still gets pushed: that is the normal folder-
    /// watch behaviour and costs the user nothing, it only forgoes restoring a save out from under it.
    /// </summary>
    public async Task<string> SyncAllAsync(IReadOnlyList<TrackedGame> games, CancellationToken ct = default)
    {
        var skipped = new List<string>();
        foreach (var g in games)
        {
            if (GameActivity.IsActive(g)) skipped.Add(g.Name);
            else await PullAsync(g, ct: ct);
            await PushAsync(g, ct: ct);
        }
        return skipped.Count == 0
            ? "Sync all complete."
            : $"Sync all complete. Not pulled (still running): {string.Join(", ", skipped)}.";
    }

    /// <summary>
    /// True if the game's save path is one nothing may ever archive or replace, having reported it.
    /// Loud on purpose: this is either a serious misconfiguration or a hostile server path, and on a
    /// headless box the console is the only place anyone would find out.
    /// </summary>
    /// <summary>
    /// True if this engine has been retired, having logged why. Not an Alert: retirement is a
    /// deliberate act by the user (they changed the server), so it is not a fault to toast at them —
    /// but it must be traceable, because the visible symptom is "my push did nothing".
    /// </summary>
    private bool RefuseIfRetired(TrackedGame game, string verb)
    {
        if (!IsRetired) return false;
        _log($"[{game.Name}] {verb} skipped: this connection to {_origin} has been retired. " +
             "The current connection handles it.");
        return true;
    }

    /// <summary>Ties an operation's lifetime to the engine's, so retirement aborts work in flight.</summary>
    private CancellationTokenSource LinkRetirement(CancellationToken ct) =>
        CancellationTokenSource.CreateLinkedTokenSource(ct, _retired.Token);

    /// <summary>
    /// How long to wait for another process to finish syncing this game, sized to what that process
    /// could legitimately be doing rather than to a round number.
    /// <para>
    /// The old 30 seconds was shorter than a single normal operation: a settle gate alone may hold
    /// the lock for <c>SettleMaxWaitSeconds</c> (120 by default) and the upload's HTTP timeout is 10
    /// minutes. So a perfectly healthy exit-push routinely outlasted the wait, and the second
    /// process concluded the first was stuck and barged in — which is exactly the overlap the lock
    /// exists to prevent. WA-07.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <c>SAVELOCKER_SYNC_LOCK_SECONDS</c> overrides it for tests only — the real policy is ~13
    /// minutes, and a suite cannot wait that out twice. Sibling of
    /// <c>SAVELOCKER_LEASE_RENEW_SECONDS</c>; both are listed in Gotchas.md.
    /// </remarks>
    private TimeSpan GameLockTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("SAVELOCKER_SYNC_LOCK_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(Math.Clamp(s, 1, 3600))
            : TimeSpan.FromSeconds(_config.SettleMaxWaitSeconds) // the settle gate
              + TimeSpan.FromMinutes(10)                          // the ApiClient HTTP timeout
              + TimeSpan.FromMinutes(1);                          // archive + hash of a large save

    /// <summary>
    /// Report that another process owns this game, as the user-facing failure it is. Nothing was
    /// read or written, which is the whole point: the alternative was two processes archiving and
    /// restoring the same directory at once.
    /// </summary>
    private void ReportContention(TrackedGame game, string verb, AgentStateLockException ex)
    {
        Alert($"[{game.Name}] {verb} skipped: another SaveLocker process is syncing this game. " +
              $"Nothing was changed. ({ex.Message})",
            AgentEventCodes.SyncBusy, AgentEventSeverity.Warning, game.GameId);
    }

    private bool RefuseUnsafePath(TrackedGame game, string verb)
    {
        var check = SavePathGuard.Check(game.SaveDirectory, _config.StateDir);
        if (check.Ok) return false;

        Alert($"[{game.Name}] REFUSED {verb}: {check.Reason} " +
              $"(mapped to '{game.SaveDirectory}')",
            AgentEventCodes.UnsafeSavePath, AgentEventSeverity.Error, game.GameId);
        return true;
    }

    private static bool HasLocalData(string dir) =>
        Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// Game launch: take the lease (warn if held elsewhere), and pull the latest save only if the
    /// caller is a genuine pre-launch boundary.
    /// Returns (Granted: false, HolderMachineName) if another machine holds the lease.
    /// <para>
    /// <paramref name="preLaunch"/> is the whole distinction between the two hosts, and it is not a
    /// tuning knob. Linux's <c>savelocker run -- %command%</c> runs <i>instead of</i> the game and
    /// starts it itself, so it can restore with certainty that nothing has the save open: true.
    /// Windows only has <see cref="ProcessWatcher"/>, which notices a game up to a poll interval
    /// after it started and after it opened its saves: false. Calling this "pre-launch" on Windows
    /// was the bug — the restore landed under a live process and the game overwrote it at exit.
    /// See Decisions.md.
    /// </para>
    /// </summary>
    public async Task<(bool Granted, string? HolderMachineName)> OnGameLaunchAsync(
        TrackedGame game, bool preLaunch, CancellationToken ct = default)
    {
        if (RefuseIfRetired(game, "launch handling")) return (false, null);
        using var linked = LinkRetirement(ct);
        ct = linked.Token;

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

        if (preLaunch)
            await PullAsync(game, force: false, ct);
        else
            // Not an alert: this is the designed Windows behaviour, not a failure, and toasting it
            // on every launch would train the user to ignore toasts. The lease is still held and the
            // exit push still runs, so the fleet stays correct — only the launch pull is absent.
            _log($"[{game.Name}] running — lease taken, launch pull skipped " +
                 "(the game already has its saves open; pull before launching instead).");

        return (true, null);
    }

    /// <summary>Post-exit: push the final save and release the lease.</summary>
    public async Task OnGameExitAsync(TrackedGame game, CancellationToken ct = default)
    {
        // The renewer stops even on a retired engine — the timer belongs to this object, and
        // leaving it running is the exact failure WA-06 describes.
        StopLeaseRenewer(game.GameId);
        if (RefuseIfRetired(game, "exit handling")) return;

        try
        {
            await PushAsync(game, force: false, settle: true, ct: ct);
        }
        finally
        {
            // In a finally, because a push that throws must not also leak the lease. It used to sit
            // after the push, so any failure there — an unreachable server, an unparseable response,
            // a cancelled upload — left the game checked out to this machine until the lease
            // expired, locking the rest of the fleet out of a game nobody was playing.
            //
            // Released through the SAME client that acquired it. _api is bound to the origin this
            // engine was built for, so even if the user changed servers mid-session the release
            // lands where the lease actually lives; releasing against the new server would free
            // nothing and leave the old one held.
            try { await _api.ReleaseLeaseAsync(game.GameId); }
            catch (Exception ex) { _log($"[{game.Name}] lease release on {_origin} failed: {ex.Message}"); }
        }
    }

    private void StartLeaseRenewer(TrackedGame game)
    {
        StopLeaseRenewer(game.GameId);
        if (IsRetired) return;

        var timer = new System.Threading.Timer(
            async _ =>
            {
                // Checked before the request AND after it. A timer callback can already be running
                // when the engine is retired — disposing the timer does not recall a callback in
                // flight — so without the second check a retired engine renews a lease on a server
                // the agent has stopped using, and reports success for it. WA-06.
                if (IsRetired) return;
                try
                {
                    var ok = await _api.RenewLeaseAsync(game.GameId);
                    if (IsRetired) return;
                    _log(ok
                        ? $"[{game.Name}] lease renewed."
                        : $"[{game.Name}] lease renewal failed — lease may have been force-released.");
                }
                catch (Exception ex)
                {
                    if (!IsRetired) _log($"[{game.Name}] lease renewal error: {ex.Message}");
                }
            },
            null, _leaseRenewInterval, _leaseRenewInterval);

        lock (_leaseTimers)
        {
            // Retired between the check above and here: the timer must not be left running, and
            // nothing else will ever come along to stop it.
            if (IsRetired) { timer.Dispose(); return; }
            _leaseTimers[game.GameId] = timer;
        }
    }

    private void StopLeaseRenewer(Guid gameId)
    {
        System.Threading.Timer? timer;
        lock (_leaseTimers) { _leaseTimers.TryGetValue(gameId, out timer); _leaseTimers.Remove(gameId); }
        timer?.Dispose();
    }

    /// <summary>Game ids this engine is currently renewing a lease for.</summary>
    private Guid[] HeldLeases()
    {
        lock (_leaseTimers) return _leaseTimers.Keys.ToArray();
    }

    /// <summary>
    /// Retire this engine: stop every lease renewer, then <b>release the leases it holds against the
    /// server that issued them</b> before the caller moves on to a new connection.
    ///
    /// <para>
    /// Replacing the engine used to simply drop the old one on the floor. Its lease timers were
    /// rooted by the runtime timer queue, so they kept renewing against the old server indefinitely
    /// — while the game's exit ran through the *new* engine and released against the *new* server,
    /// which had never issued anything. The old server's lease was then held forever by a machine
    /// that had stopped talking to it, and every other machine was locked out of that game. WA-06.
    /// </para>
    ///
    /// <para>
    /// Releasing is best-effort by design: the old server may be exactly what became unreachable and
    /// prompted the change. What must be guaranteed is that nothing renews afterwards, so an
    /// undeliverable release leaves a lease that <i>expires</i> rather than one held in perpetuity.
    /// </para>
    /// </summary>
    public async Task RetireAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var held = HeldLeases();
        foreach (var id in held) StopLeaseRenewer(id);
        _retired.Cancel();

        foreach (var id in held)
        {
            try
            {
                await _api.ReleaseLeaseAsync(id);
                _log($"Released the lease held on {_origin} before switching connection.");
            }
            catch (Exception ex)
            {
                _log($"Could not release a lease on {_origin} ({ex.Message}). " +
                     "It is no longer being renewed, so it will expire on its own.");
            }
        }

        Cleanup();
    }

    public async ValueTask DisposeAsync() => await RetireAsync();

    /// <summary>
    /// Synchronous retirement for a caller that cannot await — it stops renewals but cannot release,
    /// so any held lease expires instead. Prefer <see cref="RetireAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var id in HeldLeases()) StopLeaseRenewer(id);
        _retired.Cancel();
        Cleanup();
    }

    private void Cleanup()
    {
        lock (_leaseTimers)
        {
            foreach (var t in _leaseTimers.Values) t.Dispose();
            _leaseTimers.Clear();
        }
        _retired.Dispose();
    }
}
