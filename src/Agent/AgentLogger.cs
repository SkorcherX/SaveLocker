namespace LocalGameSync.Agent;

/// <summary>
/// Thread-safe rolling log file at %ProgramData%\LocalGameSync\agent.log.
/// Rotates to agent.log.old when the file exceeds 1 MB so it never grows unbounded.
/// All writes are best-effort — logging failures are silently swallowed so they
/// can never crash the agent.
/// </summary>
public static class AgentLogger
{
    public static readonly string LogPath =
        Path.Combine(AgentConfig.DefaultDir, "agent.log");

    private static readonly string OldLogPath = LogPath + ".old";
    private static readonly object Lock = new();
    private const long MaxBytes = 1 * 1024 * 1024;

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(AgentConfig.DefaultDir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    if (File.Exists(OldLogPath)) File.Delete(OldLogPath);
                    File.Move(LogPath, OldLogPath);
                }
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { /* must never crash the agent */ }
        }
    }

    public static void LogException(string context, Exception ex) =>
        Log($"ERROR [{context}]:{Environment.NewLine}{ex}");
}
