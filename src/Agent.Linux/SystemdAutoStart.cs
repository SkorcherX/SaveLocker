using System.Diagnostics;

namespace SaveLocker.Agent.Linux;

/// <summary>
/// "Start on login" via a <c>systemd --user</c> unit — the Linux answer to the HKCU Run key.
///
/// The unit lives in the user's home, never <c>/usr</c>: SteamOS's rootfs is immutable and is
/// wiped on every update, so a system-wide install would silently vanish (Decisions.md §5).
/// </summary>
public sealed class SystemdAutoStart : IAutoStart
{
    private const string Unit = "savelocker.service";

    private static string UnitDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user");

    private static string UnitPath => Path.Combine(UnitDir, Unit);

    public bool IsEnabled()
    {
        if (!File.Exists(UnitPath)) return false;
        var (exit, output) = Run("systemctl", "--user", "is-enabled", Unit);
        return exit == 0 && output.Trim() == "enabled";
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(UnitDir);
                File.WriteAllText(UnitPath, UnitFile());
                Run("systemctl", "--user", "daemon-reload");
                var (exit, _) = Run("systemctl", "--user", "enable", "--now", Unit);
                return exit == 0;
            }

            Run("systemctl", "--user", "disable", "--now", Unit);
            return true;
        }
        catch (Exception ex)
        {
            AgentLogger.LogException("SystemdAutoStart.SetEnabled", ex);
            return false;
        }
    }

    private static string UnitFile()
    {
        var exe = Environment.ProcessPath ?? "savelocker";
        return $"""
        [Unit]
        Description=SaveLocker agent
        After=network-online.target

        [Service]
        Type=simple
        ExecStart={exe} daemon
        Restart=on-failure
        RestartSec=10

        [Install]
        WantedBy=default.target

        """;
    }

    /// <summary>Run systemctl and capture its output. Never throws — a box without systemd
    /// (a container, a minimal chroot) simply reports the toggle as unavailable.</summary>
    private static (int ExitCode, string Output) Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, "");
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout);
        }
        catch (Exception)
        {
            return (-1, "");
        }
    }
}
