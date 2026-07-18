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
    private readonly string _stateDir;
    private readonly object _lock = new();
    private readonly State _state = new();
    /// <summary>Games this process saw sync cleanly — their stale events must not come back from disk.</summary>
    private readonly HashSet<Guid> _resolvedThisSession = new();
    /// <summary>Resolutions this process has already delivered, so the merge does not re-send them forever.</summary>
    private readonly HashSet<Guid> _sentResolvedThisSession = new();
    /// <summary>
    /// Events this process has delivered. Without this the merge re-adopts them straight back off
    /// disk — the copy another process wrote is still sitting there — and every heartbeat re-sends
    /// a fault that was resolved hours ago.
    /// </summary>
    private readonly List<(string Code, Guid? GameId, DateTime OccurredAt)> _sentEvents = new();

    private bool AlreadySent(Pending candidate) =>
        _sentEvents.Any(s => s.Code == candidate.Code && s.GameId == candidate.GameId &&
                             s.OccurredAt >= candidate.OccurredAt);

    /// <summary>
    /// The event store that belongs to this config — beside its config file, not in the machine
    /// default. See <see cref="OfflineQueue.For"/>: a process that resolves a different path keeps
    /// a private set of events nobody delivers.
    /// </summary>
    public static HealthReporter For(AgentConfig config) =>
        new(Path.Combine(config.StateDir, "health-events.json"));

    public HealthReporter(string? path = null)
    {
        _path = path ?? Path.Combine(AgentConfig.DefaultDir, "health-events.json");
        _stateDir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
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
            _resolvedThisSession.Add(gameId);
            _sentResolvedThisSession.Remove(gameId);
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
            // Adopt anything another process left behind. The launch wrapper records an event and
            // exits; on a Deck the daemon's heartbeat is the only thing that will ever deliver it.
            var disk = ReadDisk();
            foreach (var d in disk.Events)
            {
                if (_resolvedThisSession.Contains(d.GameId ?? Guid.Empty)) continue;
                if (AlreadySent(d)) continue;
                if (_state.Events.Any(e => e.Code == d.Code && e.GameId == d.GameId)) continue;
                _state.Events.Add(d);
            }
            foreach (var id in disk.ResolvedGameIds)
                if (!_state.ResolvedGameIds.Contains(id)) _state.ResolvedGameIds.Add(id);

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
            {
                _state.Events.RemoveAll(e => e.Code == sent.Code && e.GameId == sent.GameId &&
                                             e.OccurredAt <= sent.OccurredAt);
                _sentEvents.Add((sent.Code, sent.GameId, sent.OccurredAt));
            }
            foreach (var id in resolved)
            {
                _state.ResolvedGameIds.Remove(id);
                _sentResolvedThisSession.Add(id);
            }
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

    /// <summary>
    /// Merge over what is on disk, then write atomically.
    ///
    /// The launch wrapper writes here and exits; the daemon is what actually delivers on the next
    /// heartbeat. A whole-state overwrite meant whichever process persisted last erased the other's
    /// pending events — losing exactly the reports this file exists to preserve, since the file is
    /// only load-bearing when the server was unreachable.
    /// </summary>
    private void Persist()
    {
        try
        {
            using var guard = AgentStateLock.Acquire("health-events", _stateDir);

            var disk = ReadDisk();

            // Ours wins per (code, game) — we just observed it — but an event only the other process
            // recorded is kept. Events we deliberately dropped (MarkSynced) stay dropped: this
            // process just proved that game is healthy, which is newer information than the disk's.
            var merged = disk.Events
                .Where(d => !_resolvedThisSession.Contains(d.GameId ?? Guid.Empty))
                .Where(d => !AlreadySent(d))
                .Where(d => !_state.Events.Any(e => e.Code == d.Code && e.GameId == d.GameId))
                .Concat(_state.Events)
                .ToList();

            var mergedResolved = disk.ResolvedGameIds
                .Concat(_state.ResolvedGameIds)
                .Distinct()
                .Where(id => !_sentResolvedThisSession.Contains(id))
                .ToList();

            AtomicFile.WriteAllText(_path,
                JsonSerializer.Serialize(new State { Events = merged, ResolvedGameIds = mergedResolved }, JsonOpts),
                restrictPermissions: true);
        }
        catch { /* best-effort; the next write catches up */ }
    }

    private State ReadDisk()
    {
        try
        {
            if (!File.Exists(_path)) return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(_path), JsonOpts) ?? new State();
        }
        catch { return new State(); }
    }
}
