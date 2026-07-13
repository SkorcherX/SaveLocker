using System.Diagnostics;

namespace SaveLocker.Agent;

/// <summary>
/// Debounced filesystem watcher: coalesces a burst of save-file writes into a
/// single "changed" callback after the directory has been quiet for a delay.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly System.Timers.Timer _debounce;
    private readonly Action _onSettled;

    public FolderWatcher(string directory, Action onSettled, double debounceMs = 5000)
    {
        _onSettled = onSettled;
        _debounce = new System.Timers.Timer(debounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) => _onSettled();

        _fsw = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                           | NotifyFilters.DirectoryName | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _fsw.Changed += Bump;
        _fsw.Created += Bump;
        _fsw.Deleted += Bump;
        _fsw.Renamed += Bump;
    }

    private void Bump(object sender, FileSystemEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _fsw.Dispose();
        _debounce.Dispose();
    }
}

/// <summary>
/// Polls running processes and raises launch/exit events for a set of tracked
/// process names. Used to drive pre-launch pull and post-exit push.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> _watchedByGame;
    private readonly Dictionary<string, bool> _running = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? GameLaunched; // game name
    public event Action<string>? GameExited;   // game name

    /// <param name="watchedByGame">Returns a map of game-name -> process names (no .exe).</param>
    public ProcessWatcher(Func<IReadOnlyDictionary<string, IReadOnlyList<string>>> watchedByGame, double pollMs = 4000)
    {
        _watchedByGame = watchedByGame;
        _timer = new System.Timers.Timer(pollMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    private void Poll()
    {
        var active = new HashSet<string>(
            Process.GetProcesses().Select(p => SafeName(p)).Where(n => n is not null)!,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (game, procs) in _watchedByGame())
        {
            var isRunning = procs.Any(active.Contains);
            var wasRunning = _running.TryGetValue(game, out var w) && w;
            _running[game] = isRunning;

            if (isRunning && !wasRunning) GameLaunched?.Invoke(game);
            else if (!isRunning && wasRunning) GameExited?.Invoke(game);
        }
    }

    private static string? SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return null; }
    }

    public void Dispose() => _timer.Dispose();
}
