namespace SaveLocker.Agent;

/// <summary>
/// A cross-process mutex, held for as long as the returned handle is alive.
///
/// The agent is not one process. Autorun keeps the daemon running while Steam starts a *second*
/// process — <c>savelocker run -- %command%</c> — and both of them sync the same games, write the
/// same <c>config.json</c>, and drain the same queue and health files. The in-process locks that
/// were here before (a <c>SemaphoreSlim</c>, a <c>lock</c> statement) do nothing across that
/// boundary.
///
/// Implemented with a lock file opened <see cref="FileShare.None"/>, which .NET maps to a real
/// advisory <c>flock</c> on Unix and to share-mode denial on Windows.
///
/// <para><b>Take the in-process lock as well, not instead.</b> A <c>flock</c> is owned by the
/// process, so two threads in the SAME process both "acquire" it and neither blocks. This type
/// therefore guards process-vs-process only; SyncEngine still holds its per-game semaphore for
/// thread-vs-thread.</para>
/// </summary>
public sealed class AgentStateLock : IDisposable
{
    private readonly FileStream? _stream;
    private readonly string _name;

    private AgentStateLock(FileStream? stream, string name)
    {
        _stream = stream;
        _name = name;
    }

    /// <summary>
    /// Block until the named lock is ours, or until <paramref name="timeout"/> expires.
    ///
    /// A timeout does NOT throw: it returns an unheld handle and the caller proceeds. That is the
    /// deliberate trade — this agent exists to protect saves, and a lock file left behind by a
    /// crashed process must never be able to stop a game syncing forever. Contention here is
    /// measured in the seconds an upload takes, so a timeout means something is genuinely wrong,
    /// and it is logged.
    /// </summary>
    public static AgentStateLock Acquire(string name, string stateDir, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var dir = Path.Combine(stateDir, "locks");
        var path = Path.Combine(dir, $"{name}.lock");

        try { Directory.CreateDirectory(dir); }
        catch (Exception ex)
        {
            AgentLogger.Log($"lock '{name}': cannot create {dir} ({ex.Message}) — proceeding unlocked.");
            return new AgentStateLock(null, name);
        }

        var deadline = DateTime.UtcNow + limit;
        var delay = 15;
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new AgentStateLock(stream, name);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    AgentLogger.Log(
                        $"lock '{name}': still held after {limit.TotalSeconds:0}s — proceeding without it. " +
                        "Another agent process may be stuck.");
                    return new AgentStateLock(null, name);
                }
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 250);
            }
            catch (UnauthorizedAccessException ex)
            {
                AgentLogger.Log($"lock '{name}': {ex.Message} — proceeding unlocked.");
                return new AgentStateLock(null, name);
            }
        }
    }

    /// <summary>The per-game sync lock. Push and pull for one game are mutually exclusive fleet-wide on this box.</summary>
    public static AgentStateLock ForGame(Guid gameId, string stateDir, TimeSpan? timeout = null) =>
        Acquire($"game-{gameId:N}", stateDir, timeout);

    public void Dispose() => _stream?.Dispose();
}
