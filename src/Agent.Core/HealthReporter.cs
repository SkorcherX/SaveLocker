using System.Text.Json;
using System.Text.Json.Serialization;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// The agent's half of health reporting (Decisions.md §2: <b>the console is the Deck's UI</b>).
/// A headless spoke cannot raise a toast, so the failures it would have toasted are sent to the
/// server instead and surfaced on the dashboard.
/// <para>
/// Pending events are <b>persisted</b>, for the reason that makes this feature necessary at all:
/// the most important thing to report — "I could not reach the server" — happens precisely when
/// reporting is impossible. It is written to disk, survives a restart, and goes out on the first
/// heartbeat after contact is regained.
/// </para>
/// Events coalesce on (code, gameId) while pending, so a fault that recurs before the next
/// heartbeat sends one event, not fifty. The server deduplicates again on its side.
/// </summary>
public sealed class HealthReporter
{
    private sealed class Pending
    {
        public string Code { get; set; } = "";
        public AgentEventSeverity Severity { get; set; }
        public string Message { get; set; } = "";
        public Guid? GameId { get; set; }
        public DateTime OccurredAt { get; set; }
    }

    private sealed class State
    {
        public List<Pending> Events { get; set; } = new();
        /// <summary>Games that synced cleanly since the last heartbeat — they close their own events.</summary>
        public List<Guid> ResolvedGameIds { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly State _state = new();

    public HealthReporter(string? path = null)
    {
        _path = path ?? Path.Combine(AgentConfig.DefaultDir, "health-events.json");
        Load();
    }

    /// <summary>Record a condition for the next heartbeat. Coalesces on (code, game).</summary>
    public void Report(string code, AgentEventSeverity severity, string message, Guid? gameId = null)
    {
        lock (_lock)
        {
            var existing = _state.Events.FirstOrDefault(e => e.Code == code && e.GameId == gameId);
            if (existing is not null)
            {
                existing.Message = message;
                existing.Severity = severity;
                existing.OccurredAt = DateTime.UtcNow;
            }
            else
            {
                _state.Events.Add(new Pending
                {
                    Code = code,
                    Severity = severity,
                    Message = message,
                    GameId = gameId,
                    OccurredAt = DateTime.UtcNow
                });
            }
            Persist();
        }
    }

    /// <summary>
    /// This game just synced cleanly. Its open events close on the next heartbeat — a machine that
    /// recovers on its own must not leave a stale alarm on the console. Any of its still-pending
    /// events are dropped too: they describe a state that is no longer true.
    /// </summary>
    public void MarkSynced(Guid gameId)
    {
        lock (_lock)
        {
            _state.Events.RemoveAll(e => e.GameId == gameId);
            if (!_state.ResolvedGameIds.Contains(gameId))
                _state.ResolvedGameIds.Add(gameId);
            Persist();
        }
    }

    /// <summary>
    /// Send the heartbeat. On success the pending set is cleared; on failure it is kept, because a
    /// failed report is exactly the situation the persistence exists for. Never throws — health
    /// reporting must not be able to break syncing.
    /// </summary>
    public async Task<bool> SendAsync(
        ApiClient api, AgentConfig config, OfflineQueue? offlineQueue, CancellationToken ct = default)
    {
        Pending[] events;
        Guid[] resolved;
        lock (_lock)
        {
            events = _state.Events.ToArray();
            resolved = _state.ResolvedGameIds.ToArray();
        }

        var beat = new AgentHeartbeat(
            AgentVersion: UpdateChecker.CurrentVersion.ToString(),
            Platform: OperatingSystem.IsWindows() ? "Windows" : "Linux",
            LastSyncTime: config.LastSyncTime,
            TrackedGames: config.Games.Count,
            UnmappedGames: config.Games.Count(g => string.IsNullOrWhiteSpace(g.SaveDirectory)),
            OfflineQueueDepth: offlineQueue?.GetAll().Count ?? 0,
            Events: events.Select(e => new AgentEventReport(
                e.Code, e.Severity, e.Message, e.GameId, e.OccurredAt)).ToArray(),
            ResolvedGameIds: resolved);

        try
        {
            await api.ReportHealthAsync(beat, ct);
        }
        catch (Exception ex)
        {
            AgentLogger.LogException("HealthReporter.Send", ex);
            return false;
        }

        // Clear only what was actually sent — a fault reported while the request was in flight must
        // survive to the next beat rather than being silently dropped here.
        lock (_lock)
        {
            foreach (var sent in events)
                _state.Events.RemoveAll(e => e.Code == sent.Code && e.GameId == sent.GameId &&
                                             e.OccurredAt <= sent.OccurredAt);
            foreach (var id in resolved) _state.ResolvedGameIds.Remove(id);
            Persist();
        }
        return true;
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOpts);
            if (loaded is null) return;
            _state.Events = loaded.Events ?? new();
            _state.ResolvedGameIds = loaded.ResolvedGameIds ?? new();
        }
        catch { /* corrupt — start fresh; health must never block startup */ }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_state, JsonOpts));
        }
        catch { /* best-effort; the next write catches up */ }
    }
}
