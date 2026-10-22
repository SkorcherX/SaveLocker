namespace SaveLocker.Agent;

/// <summary>
/// Where RetroArch keeps saves and save states on this machine: EmuDeck's documented
/// <c>Emulation/saves/retroarch/{saves,states}</c> first, then any standalone RetroArch's own
/// <c>savefile_directory</c>/<c>savestate_directory</c> from its <c>retroarch.cfg</c>.
/// </summary>
public static class RetroArchConfig
{
    /// <summary>Every RetroArch setup whose saves folder exists, real paths, no duplicate saves folder.</summary>
    public static IReadOnlyList<RetroArchFolders> Folders() =>
        Folders(EmuDeckRoots.Find(), EmuDeckRoots.Override is null ? ConfigRoots() : Array.Empty<string>());

    /// <summary>The same, from explicit inputs — what the tests drive.</summary>
    public static IReadOnlyList<RetroArchFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<string> configRoots)
    {
        var candidates = new List<RetroArchFolders>();
        foreach (var root in emuDeckRoots)
        {
            var retroArch = Path.Combine(root, "saves", "retroarch");
            candidates.Add(new RetroArchFolders(Path.Combine(retroArch, "saves"), Path.Combine(retroArch, "states"), EmuDeck: true));
        }

        foreach (var configRoot in configRoots)
        {
            var cfg = Path.Combine(configRoot, "retroarch.cfg");
            var settings = File.Exists(cfg) ? Parse(SafeRead(cfg)) : new Dictionary<string, string>();
            // RetroArch's own defaults when a key is unset or "default".
            var states = settings.TryGetValue("savestate_directory", out var st) && ExpandPath(st, configRoot) is { } s
                ? s : Path.Combine(configRoot, "states");
            // The default folder only when the config does not name another: one left over from before the
            // setting changed holds saves RetroArch no longer reads or writes.
            var saves = settings.TryGetValue("savefile_directory", out var dir) && ExpandPath(dir, configRoot) is { } expanded
                ? expanded : Path.Combine(configRoot, "saves");
            candidates.Add(new RetroArchFolders(saves, states));
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<RetroArchFolders>();
        foreach (var c in candidates)
        {
            bool exists;
            try { exists = Directory.Exists(c.Saves); } catch { exists = false; }
            if (!exists) continue;
            var real = EmuDeckRoots.RealPath(c.Saves);
            // A states folder that does not exist yet is still this setup's: RetroArch creates it on the
            // first state, and a pull creates it from another machine's.
            if (seen.Add(real)) found.Add(new RetroArchFolders(real, EmuDeckRoots.RealPath(c.States), c.EmuDeck));
        }
        return found;
    }

    /// <summary>Folders a standalone RetroArch keeps <c>retroarch.cfg</c> in, per platform.</summary>
    public static IReadOnlyList<string> ConfigRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new[]
            {
                Path.Combine(appData, "RetroArch"),
                // EmuDeck for Windows' own RetroArch (emudeck.github.io/emulators/windows/retroarch). Its
                // docs call Emulation\saves\retroarch\saves a "shortcut"; if that is a .lnk file rather
                // than a link, the EmuDeck path finds nothing and this is the only way in.
                Path.Combine(home, "emudeck", "EmulationStation-DE", "Emulators", "RetroArch"),
            };
        }
        return new[]
        {
            Path.Combine(home, ".config", "retroarch"),
            Path.Combine(home, ".var", "app", "org.libretro.RetroArch", "config", "retroarch"),
        };
    }

    /// <summary>
    /// <c>retroarch.cfg</c>'s flat <c>key = "value"</c> lines. Comments and malformed lines are
    /// skipped; the last occurrence of a key wins, as it does in RetroArch.
    /// </summary>
    public static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// A path as RetroArch writes it, made absolute: <c>~</c> is the home folder and a leading
    /// <c>:</c> is RetroArch's own folder (a portable Windows install writes <c>:\saves</c>). Empty and
    /// <c>"default"</c> mean "not set" — null, and the caller falls back to RetroArch's default.
    /// </summary>
    public static string? ExpandPath(string value, string configRoot)
    {
        var v = value.Trim();
        if (v.Length == 0 || v.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
        if (v == "~" || v.StartsWith("~/") || v.StartsWith("~\\"))
            v = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), v.Length > 2 ? v[2..] : "");
        else if (v.StartsWith(':'))
            v = Path.Combine(configRoot, v.TrimStart(':').TrimStart('/', '\\'));
        try { return Path.IsPathRooted(v) ? Path.GetFullPath(v) : null; }
        catch { return null; }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}

/// <summary>One RetroArch setup's folders: where it writes SRAM saves (<c>.srm</c>) and where it writes
/// save states (<c>.state*</c>). The states folder may not exist yet. <paramref name="EmuDeck"/>: found
/// through EmuDeck's <c>Emulation</c> folder.</summary>
public sealed record RetroArchFolders(string Saves, string States, bool EmuDeck = false);
