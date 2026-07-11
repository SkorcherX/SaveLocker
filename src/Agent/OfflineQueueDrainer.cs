namespace SaveLocker.Agent;

/// <summary>
/// Periodically retries pushes that were queued because the server was unreachable.
/// Runs on a background timer; safe to overlap — a second tick is skipped if a drain
/// is already in progress.
/// </summary>
public sealed class OfflineQueueDrainer : IDisposable
{
    private readonly OfflineQueue _queue;
    private readonly AgentConfig _config;
    private readonly Func<SyncEngine> _engine;
    private readonly Action<string> _log;
    private readonly System.Threading.Timer _timer;
    private int _draining;

    public OfflineQueueDrainer(
        OfflineQueue queue,
        AgentConfig config,
        Func<SyncEngine> engine,
        Action<string> log,
        TimeSpan? interval = null)
    {
        _queue = queue;
        _config = config;
        _engine = engine;
        _log = log;
        var period = interval ?? TimeSpan.FromSeconds(30);
        _timer = new System.Threading.Timer(OnTick, null, period, period);
    }

    private async void OnTick(object? _)
    {
        if (_queue.IsEmpty) return;
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        try { await DrainAsync(); }
        finally { Interlocked.Exchange(ref _draining, 0); }
    }

    private async Task DrainAsync()
    {
        var pending = _queue.GetAll();
        _log($"[OfflineQueue] {pending.Count} pending push(es) — attempting drain…");
        var engine = _engine();

        foreach (var entry in pending)
        {
            var game = _config.Games.FirstOrDefault(g => g.GameId == entry.GameId);
            if (game is null)
            {
                // Game was removed from config — nothing to push.
                _queue.Remove(entry.GameId);
                continue;
            }

            try
            {
                var result = await engine.PushAsync(game, entry.Force);
                if (result is not null)
                {
                    _queue.Remove(entry.GameId);
                    _log($"[OfflineQueue] {entry.GameName} drained successfully (retry #{entry.RetryCount + 1}).");
                }
                else
                {
                    // null means either save dir is gone or the server is still down.
                    // If the save directory no longer exists there's nothing to push.
                    if (!Directory.Exists(game.SaveDirectory))
                        _queue.Remove(entry.GameId);
                    else
                        _queue.RecordAttempt(entry.GameId);
                }
            }
            catch
            {
                _queue.RecordAttempt(entry.GameId);
            }
        }
    }

    public void Dispose() => _timer.Dispose();

}
