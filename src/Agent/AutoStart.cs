using Microsoft.Win32;

namespace LocalGameSync.Agent;

/// <summary>
/// "Start with Windows" via the per-user Run registry key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). Per-user means no admin
/// rights are needed; the tray agent launches automatically when the user logs in.
/// The stored command points at the current executable (the apphost exe).
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SaveLocker";

    /// <summary>True when a login entry exists for this agent.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    /// <summary>Add or remove the login entry. No-op (returns false) if the exe path is unknown.</summary>
    public static bool SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return false;

        if (enabled)
        {
            var exe = LauncherPath();
            if (string.IsNullOrEmpty(exe)) return false;
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        return true;
    }

    /// <summary>
    /// The standalone launcher to register. When running as the published exe
    /// (single-file or apphost) <see cref="Environment.ProcessPath"/> is already the
    /// agent exe. Only the dev path "dotnet Agent.dll" is wrong — there ProcessPath is
    /// dotnet.exe with no dll argument, useless for auto-start — so map it to the
    /// apphost exe next to the app.
    /// </summary>
    private static string? LauncherPath()
    {
        var proc = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(proc) &&
            string.Equals(Path.GetFileName(proc), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var apphost = Path.Combine(AppContext.BaseDirectory, "LocalGameSync.Agent.exe");
            if (File.Exists(apphost)) return apphost;
        }
        return proc;
    }
}
