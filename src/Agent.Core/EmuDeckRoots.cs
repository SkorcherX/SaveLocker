namespace SaveLocker.Agent;

/// <summary>
/// Finds EmuDeck's <c>Emulation</c> folder — the root of its <c>{bios,roms,saves,storage}/…</c>
/// layout, which EmuDeck documents as identical on SteamOS and on Windows. Only the root-finding is
/// per-OS; every path below it is shared, so one reader serves both scanners.
/// <para>
/// EmuDeck records the root in its own settings script (<c>emulationPath=</c> on Linux,
/// <c>$emulationPath=</c> on Windows); the usual default locations are tried as well, so an install
/// whose settings file moved is still found.
/// </para>
/// </summary>
public static class EmuDeckRoots
{
    /// <summary>
    /// Points the scan at ONE <c>Emulation</c> folder and nothing else — no settings file, no default
    /// location, no standalone emulator config. The test rig sets it so a test agent scans a fixture
    /// tree and never the developer's real emulator saves (the same exclusive-override shape as
    /// <c>SAVELOCKER_PLAYNITE_PATH</c>).
    /// </summary>
    public const string OverrideVariable = "SAVELOCKER_EMUDECK_PATH";

    /// <summary>The override, when set.</summary>
    public static string? Override =>
        Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } p ? p.Trim() : null;

    /// <summary>Every existing <c>Emulation</c> folder on this machine, real paths, no duplicates.</summary>
    public static IReadOnlyList<string> Find()
    {
        if (Override is { } forced)
            return Directory.Exists(forced) ? new[] { RealPath(forced) } : Array.Empty<string>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var guesses = new List<string?>();

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            guesses.Add(ReadSetting(Path.Combine(home, "EmuDeck", "settings.ps1"), "$emulationPath"));
            guesses.Add(ReadSetting(Path.Combine(appData, "EmuDeck", "settings.ps1"), "$emulationPath"));
            guesses.Add(Path.Combine(home, "Emulation"));
            foreach (var drive in SafeDrives())
                guesses.Add(Path.Combine(drive, "Emulation"));
        }
        else
        {
            guesses.Add(ReadSetting(Path.Combine(home, ".config", "EmuDeck", "settings.sh"), "emulationPath"));
            guesses.Add(ReadSetting(Path.Combine(home, "emudeck", "settings.sh"), "emulationPath"));
            guesses.Add(Path.Combine(home, "Emulation"));
            // An SD card: /run/media/<label> on older SteamOS, /run/media/<user>/<label> on newer.
            foreach (var dir in SafeSubdirs("/run/media"))
            {
                guesses.Add(Path.Combine(dir, "Emulation"));
                foreach (var sub in SafeSubdirs(dir)) guesses.Add(Path.Combine(sub, "Emulation"));
            }
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<string>();
        foreach (var g in guesses)
        {
            if (string.IsNullOrWhiteSpace(g) || !SafeDirectoryExists(g)) continue;
            var real = RealPath(g);
            if (seen.Add(real)) found.Add(real);
        }
        return found;
    }

    /// <summary>
    /// The value of <paramref name="key"/> in an EmuDeck settings script (<c>key=value</c>,
    /// <c>key="value"</c> or <c>$key = "value"</c>), or null. Reads nothing else from the script and
    /// executes nothing.
    /// </summary>
    public static string? ReadSetting(string scriptPath, string key)
    {
        try
        {
            if (!File.Exists(scriptPath)) return null;
            foreach (var raw in File.ReadLines(scriptPath))
            {
                var line = raw.Trim();
                if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = line[key.Length..].TrimStart();
                if (!rest.StartsWith('=')) continue;
                var value = rest[1..].Trim().Trim('"', '\'').Trim();
                return value.Length == 0 ? null : value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// <paramref name="path"/> with a symlink at ANY component resolved, not just the last one.
    /// EmuDeck's <c>Emulation/saves/&lt;emulator&gt;/…</c> entries are links into each emulator's own
    /// folders, and EmuDeck's own docs warn that backing up the link instead of its target loses the
    /// data — so a save folder is always recorded by where its bytes really live.
    /// </summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new DirectoryInfo(current);
                if (info.LinkTarget is not null &&
                    info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Path.GetFullPath(target.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return current;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static IEnumerable<string> SafeSubdirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeDrives()
    {
        try
        {
            // Fixed and removable only: probing a disconnected network drive can hang for seconds.
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }
}
