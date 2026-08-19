namespace SaveLocker.Agent;

/// <summary>What a sync attempt is doing right now, for a UI to show rather than a log file.</summary>
public enum SyncPhase
{
    Idle,
    Pulling,
    /// <summary>Waiting for the game to finish writing before a push archives the folder.</summary>
    Settling,
    Pushing
}

/// <summary>A moment-in-time read of the current sync attempt, or all-default fields when nothing
/// is running.</summary>
public sealed record SyncActivitySnapshot(
    string? GameName, SyncPhase Phase, long BytesDone, long BytesTotal, DateTime? StartedAtUtc);

public sealed record ActivityLogEntry(DateTime TimestampUtc, string Message);

/// <summary>
/// What the agent UI's Overview page shows at the bottom: what is syncing right now (with byte
/// progress for a push, since that is the direction slow enough to want one — see
/// <see cref="ApiClient.UploadAsync"/>'s chunk loop, which is what actually reports it) and a short
/// rolling history of what just happened.
/// <para>
/// One instance is owned by the host (the tray, the daemon) and outlives any single
/// <see cref="SyncEngine"/> — settings changes rebuild the engine, but the activity feed a user is
/// watching should not reset just because the server URL changed underneath it.
/// </para>
/// <para>
/// Thread-safe: <see cref="SyncEngine"/> writes from watcher, poller and drainer threads, and the
/// agent API server reads from a Kestrel request thread, all concurrently.
/// </para>
/// </summary>
public sealed class SyncActivityTracker
{
    private const int MaxRecent = 50;

    private readonly object _lock = new();
    private string? _gameName;
    private SyncPhase _phase = SyncPhase.Idle;
    private long _bytesDone;
    private long _bytesTotal;
    private DateTime? _startedAtUtc;
    private readonly LinkedList<ActivityLogEntry> _recent = new();

    /// <summary>Start (or restart) tracking one game's sync attempt.</summary>
    public void Begin(string gameName, SyncPhase phase)
    {
        lock (_lock)
        {
            _gameName = gameName;
            _phase = phase;
            _bytesDone = 0;
            _bytesTotal = 0;
            _startedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Move to a later phase of the same attempt (e.g. Settling -> Pushing) without
    /// resetting the started-at time or the game name.</summary>
    public void SetPhase(SyncPhase phase)
    {
        lock (_lock) { _phase = phase; }
    }

    public void Progress(long bytesDone, long bytesTotal)
    {
        lock (_lock) { _bytesDone = bytesDone; _bytesTotal = bytesTotal; }
    }

    /// <summary>Back to idle. Safe to call even when nothing was ever begun.</summary>
    public void End()
    {
        lock (_lock)
        {
            _gameName = null;
            _phase = SyncPhase.Idle;
            _bytesDone = 0;
            _bytesTotal = 0;
            _startedAtUtc = null;
        }
    }

    /// <summary>
    /// Append one line to the rolling history. Takes the message only — callers already have their
    /// own <c>Action&lt;string&gt; log</c> for the file log (<see cref="AgentLogger"/> or a CLI's
    /// console write), and this is deliberately not that: duplicating file-logging here would mean
    /// two places deciding what "logged" means. A host composes both into one delegate instead.
    /// </summary>
    public void Record(string message)
    {
        lock (_lock)
        {
            _recent.AddLast(new ActivityLogEntry(DateTime.UtcNow, message));
            while (_recent.Count > MaxRecent) _recent.RemoveFirst();
        }
    }

    public SyncActivitySnapshot Current()
    {
        lock (_lock) return new SyncActivitySnapshot(_gameName, _phase, _bytesDone, _bytesTotal, _startedAtUtc);
    }

    /// <summary>Newest first — what a UI wants to render top-to-bottom without reversing it itself.</summary>
    public IReadOnlyList<ActivityLogEntry> Recent()
    {
        lock (_lock) return _recent.Reverse().ToList();
    }
}
