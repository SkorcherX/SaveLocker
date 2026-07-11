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
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Entry> _entries = new();

    public OfflineQueue(string? path = null)
    {
        _path = path ?? Path.Combine(AgentConfig.DefaultDir, "offline-queue.json");
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

    /// <summary>Returns a snapshot of all pending entries (safe to iterate without holding the lock).</summary>
    public IReadOnlyList<Entry> GetAll()
    {
        lock (_lock) return _entries.Values.ToList();
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

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries.Values.ToList(), JsonOpts));
        }
        catch { /* best-effort; next successful write will catch up */ }
    }
}
