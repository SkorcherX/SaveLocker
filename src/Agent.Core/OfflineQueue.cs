using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveLocker.Agent;

/// <summary>
/// Durable store for save pushes that failed due to network unavailability.
/// Persisted as JSON so entries survive agent restarts.
/// One entry per game (deduped by GameId); force=true is sticky.
/// </summary>
public sealed class OfflineQueue
{
    public sealed class Entry
    {
        public Guid GameId { get; set; }
        public string GameName { get; set; } = "";
        public bool Force { get; set; }
        public DateTimeOffset QueuedAt { get; set; }
        public int RetryCount { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly string _stateDir;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    /// <summary>Games this process has drained, so the merge does not resurrect them from a stale disk copy.</summary>
    private readonly HashSet<Guid> _removedThisSession = new();

    /// <summary>
    /// The queue that belongs to this config. State must live beside the config file it belongs to,
    /// not in the machine default: with <c>--config</c> the two diverge, and every process that
    /// disagreed about the path would silently keep its own private queue while believing it shared
    /// one. That also lets two "machines" run side by side in the tests.
    /// </summary>
    public static OfflineQueue For(AgentConfig config) =>
        new(Path.Combine(config.StateDir, "offline-queue.json"));

    public OfflineQueue(string? path = null)
    {
        _path = path ?? Path.Combine(AgentConfig.DefaultDir, "offline-queue.json");
        _stateDir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Load();
    }

    public bool IsEmpty { get { lock (_lock) return _entries.Count == 0; } }

    /// <summary>
    /// Add or update a pending push for <paramref name="gameId"/>.
    /// If the game is already queued, force=true is preserved but retry count is kept.
    /// </summary>
    public void Enqueue(Guid gameId, string gameName, bool force)
    {
        lock (_lock)
        {
            _removedThisSession.Remove(gameId);
            if (_entries.TryGetValue(gameId, out var existing))
                existing.Force |= force;
            else
                _entries[gameId] = new Entry
                {
                    GameId = gameId,
                    GameName = gameName,
                    Force = force,
                    QueuedAt = DateTimeOffset.UtcNow,
                };
            Persist();
        }
    }

    public void Remove(Guid gameId)
    {
        lock (_lock)
        {
            _removedThisSession.Add(gameId);
            if (_entries.Remove(gameId))
                Persist();
        }
    }

    public void RecordAttempt(Guid gameId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(gameId, out var e)) return;
            e.RetryCount++;
            e.LastAttemptAt = DateTimeOffset.UtcNow;
            Persist();
        }
    }

    /// <summary>
    /// A snapshot of all pending entries (safe to iterate without holding the lock).
    ///
    /// Re-reads the file first, because the entry that most needs draining is usually not ours: the
    /// launch wrapper queues a push when the server is unreachable and then exits, and the daemon's
    /// drainer is the only thing still running to retry it. Without this refresh the daemon would
    /// never see it until the next restart.
    /// </summary>
    public IReadOnlyList<Entry> GetAll()
    {
        lock (_lock)
        {
            foreach (var e in ReadDisk())
                if (!_entries.ContainsKey(e.GameId) && !_removedThisSession.Contains(e.GameId))
                    _entries[e.GameId] = e;
            return _entries.Values.ToList();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), JsonOpts) ?? [];
            foreach (var e in list)
                _entries[e.GameId] = e;
        }
        catch { /* corrupt queue — start fresh */ }
    }

    /// <summary>
    /// Merge this process's view over whatever is on disk, then write it atomically.
    ///
    /// The daemon and the launch wrapper each hold their own queue, loaded at startup. A plain
    /// whole-collection write means the last one to persist erases every entry the other queued —
    /// and this queue exists precisely for the case where the server is unreachable, so dropping an
    /// entry silently loses a save that was never uploaded. Re-reading under the lock keeps both
    /// processes' entries.
    /// </summary>
    private void Persist()
    {
        try
        {
            using var guard = AgentStateLock.Acquire("offline-queue", _stateDir);

            var merged = new Dictionary<Guid, Entry>();
            foreach (var e in ReadDisk()) merged[e.GameId] = e;

            // Ours wins on conflict — we just acted on it — but an entry only the other process
            // knows about survives, and a removal we made is honoured rather than resurrected.
            foreach (var e in _entries.Values) merged[e.GameId] = e;
            foreach (var id in merged.Keys.ToList())
                if (!_entries.ContainsKey(id) && _removedThisSession.Contains(id)) merged.Remove(id);

            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(merged.Values.ToList(), JsonOpts),
                restrictPermissions: true);
        }
        catch { /* best-effort; next successful write will catch up */ }
    }

    private List<Entry> ReadDisk()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), JsonOpts) ?? [];
        }
        catch { return []; }
    }
}
