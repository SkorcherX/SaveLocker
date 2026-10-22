using System.Diagnostics;

namespace SaveLocker.Agent;

/// <summary>
/// Debounced filesystem watcher: coalesces a burst of save-file writes into a
/// single "changed" callback after the directory has been quiet for a delay.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _fsws = new();
    private readonly System.Timers.Timer _debounce;
    private readonly Action _onSettled;

    public FolderWatcher(string directory, Action onSettled, double debounceMs = 5000)
        : this(new[] { directory }, onSettled, debounceMs) { }

    /// <summary>
    /// Watch every folder of one game behind ONE delay, so a save that writes two folders (an
    /// emulator's save and its save state) settles into a single push rather than two. Folders that
    /// do not exist are skipped — nothing can watch them until something creates them, which is why
    /// the hosts rebuild their watchers after a pull does.
    /// </summary>
    public FolderWatcher(IEnumerable<string> directories, Action onSettled, double debounceMs = 5000)
    {
        _onSettled = onSettled;
        _debounce = new System.Timers.Timer(debounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) => _onSettled();

        foreach (var directory in directories.Where(Directory.Exists))
        {
            var fsw = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                               | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            fsw.Changed += Bump;
            fsw.Created += Bump;
            fsw.Deleted += Bump;
            fsw.Renamed += Bump;
            _fsws.Add(fsw);
        }
    }

    private void Bump(object sender, FileSystemEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        foreach (var fsw in _fsws) fsw.Dispose();
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
    // The first poll only records what is already running. Without it, starting the Agent while a
    // game is open looks exactly like that game launching, and the launch handler fires a lease
    // acquisition and (before WA-01) a restore for a game that has been playing for an hour.
    private bool _baselined;
    // Process.GetProcesses() on a busy box can outlast the 4 s tick, and System.Timers.Timer runs
    // each tick on its own thread-pool thread: two polls then interleave over _running and the
    // launch/exit events fire twice, or invert. One poll at a time; a tick that arrives while one is
    // in flight is dropped, since the next one four seconds later reads the same OS state anyway.
    private int _polling;
    private volatile bool _disposed;

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
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try { PollCore(); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void PollCore()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            // Every Process from GetProcesses holds a native handle. Polling every four seconds for
            // the life of the tray leaked one per process per tick until the GC got round to them.
            try { if (SafeName(p) is { } n) active.Add(n); }
            finally { p.Dispose(); }
        }

        foreach (var (game, procs) in _watchedByGame())
        {
            var isRunning = procs.Any(active.Contains);
            var wasRunning = _running.TryGetValue(game, out var w) && w;
            _running[game] = isRunning;

            if (!_baselined) continue;
            if (isRunning && !wasRunning) GameLaunched?.Invoke(game);
            else if (!isRunning && wasRunning) GameExited?.Invoke(game);
        }

        _baselined = true;
    }

    private static string? SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
