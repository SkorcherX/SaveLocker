namespace SaveLocker.Agent;

/// <summary>
/// ScummVM (tasks/emulator-saves Phase 10). Each game is a <c>[target]</c> section of <c>scummvm.ini</c>; its saves
/// are files named after the target in the save folder every game shares — <c>&lt;target&gt;.s##</c> or
/// <c>&lt;target&gt;.###</c> (<c>MetaEngine::getSavegameFile</c>), so the scope is <c>&lt;target&gt;.*</c>
/// (<c>monkey.*</c> never matches <c>monkey2.s01</c>). A few engines name their files after themselves instead
/// (<see cref="FixedPrefixes"/>). Only targets with a save file are candidates; no save states (D3).
/// <para>
/// Where: <c>savepath=</c> in the target's own section, else in <c>[scummvm]</c>, else the default — EmuDeck sets
/// the global one to <c>Emulation/saves/scummvm/saves</c> on both OSes. Config: <c>%APPDATA%\ScummVM\scummvm.ini</c>,
/// <c>~/.config/scummvm/scummvm.ini</c> (older builds <c>~/.scummvmrc</c>), the Flatpak's
/// <c>~/.var/app/org.scummvm.ScummVM/config/scummvm/scummvm.ini</c>. Default saves: <c>%APPDATA%\ScummVM\Saved
/// games</c>, <c>~/.local/share/scummvm/saves</c>, the Flatpak's <c>…/data/scummvm/saves</c>.
/// </para>
/// </summary>
public static class ScummVmSaves
{
    public const string EmulatorName = "ScummVM";

    /// <summary>
    /// Engines whose save files carry a fixed name whatever the target is called (each engine's
    /// <c>metaengine.cpp</c>, read 2026-10-07): Beneath a Steel Sky writes <c>SKY-VM.###</c>, Broken Sword 1
    /// <c>sword1.###</c>, Flight of the Amazon Queen <c>queen.s##</c>, Lure of the Temptress <c>lure.###</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> FixedPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["sky"] = "SKY-VM", ["sword1"] = "sword1", ["queen"] = "queen", ["lure"] = "lure",
    };

    /// <summary>
    /// The fixed file prefix of a target's engine, or null. <c>engineid</c> names the engine; a config written
    /// before ScummVM recorded it has only <c>gameid</c>, which for these engines is the engine's name or that
    /// name plus a variant (<c>sword1mac</c>, <c>sword1psxdemo</c>).
    /// </summary>
    public static string? FixedPrefix(IReadOnlyDictionary<string, string> keys)
    {
        if (keys.GetValueOrDefault("engineid") is { Length: > 0 } engine) return FixedPrefixes.GetValueOrDefault(engine);
        if (keys.GetValueOrDefault("gameid") is not { Length: > 0 } game) return null;
        return FixedPrefixes.GetValueOrDefault(game) ??
               FixedPrefixes.Where(p => game.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase) && game.Length > p.Key.Length &&
                                        VariantSuffixes.Any(v => game[p.Key.Length..].StartsWith(v, StringComparison.OrdinalIgnoreCase)))
                   .Select(p => p.Value).FirstOrDefault();
    }

    /// <summary>What ScummVM appends to these engines' game ids for another release (Broken Sword 1: <c>sword1mac</c>,
    /// <c>sword1psx</c>, <c>sword1demo</c>, <c>sword1macdemo</c>, <c>sword1psxdemo</c>).</summary>
    private static readonly string[] VariantSuffixes = ["demo", "mac", "psx"];

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(EmulatorPaths.Standalone ? Configs() : Array.Empty<ScummVmConfig>(), EmuDeckRoots.Find());

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<ScummVmConfig> configs, IEnumerable<string> emuDeckRoots)
    {
        var emuDeckSaves = emuDeckRoots.Select(r => SafeReal(Path.Combine(r, "saves", "scummvm", "saves"))).ToHashSet(Comparer);
        var found = new List<ScanCandidate>();
        var seen = new HashSet<string>(Comparer);
        foreach (var config in configs)
        {
            var sections = ParseIni(SafeRead(config.IniPath));
            var global = sections.GetValueOrDefault("scummvm");
            foreach (var (target, keys) in sections)
            {
                // A game's section names its game; [scummvm], [keymapper], [cloud]… do not.
                if (!keys.ContainsKey("gameid") && !keys.ContainsKey("engineid")) continue;
                var dir = Dir(keys.GetValueOrDefault("savepath")) ?? Dir(global?.GetValueOrDefault("savepath")) ?? config.DefaultSaves;
                var prefix = FixedPrefix(keys) ?? target;
                if (prefix.Length == 0 || prefix.Contains('*') || prefix.Contains(':')) continue;
                if (!HasSaves(dir, prefix)) continue;
                var real = EmuDeckRoots.RealPath(dir);
                // Two targets of one fixed-name engine, or one target in two configs, are one game.
                if (!seen.Add(real + "\0" + prefix)) continue;
                found.Add(new ScanCandidate(
                    Name: keys.GetValueOrDefault("description") is { Length: > 0 } d ? RomNames.CleanTitle(d) : target,
                    SuggestedSaveDir: real,
                    Source: ScanSource.Emulator,
                    HasSteamCloud: false,
                    EmulatorName: EmulatorName,
                    EmulatorSystem: "scummvm",
                    EmulatorRom: target,
                    ViaEmuDeck: emuDeckSaves.Contains(real),
                    IncludeGlobs: new[] { prefix + ".*" }));
            }
        }
        return found.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.EmulatorRom, StringComparer.Ordinal).ToList();
    }

    /// <summary>This machine's ScummVM configs, each with the save folder it uses when none is set.</summary>
    public static IReadOnlyList<ScummVmConfig> Configs()
    {
        var home = EmulatorPaths.Home;
        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(EmulatorPaths.AppData, "ScummVM");
            return Existing(new ScummVmConfig(Path.Combine(root, "scummvm.ini"), Path.Combine(root, "Saved games")));
        }
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c && EmulatorPaths.HomeOverride is null
            ? c : Path.Combine(home, ".config");
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d && EmulatorPaths.HomeOverride is null
            ? d : Path.Combine(home, ".local", "share");
        var defaultSaves = Path.Combine(xdgData, "scummvm", "saves");
        var flatpak = Path.Combine(home, ".var", "app", "org.scummvm.ScummVM");
        return Existing(
            new ScummVmConfig(Path.Combine(xdgConfig, "scummvm", "scummvm.ini"), defaultSaves),
            new ScummVmConfig(Path.Combine(home, ".scummvmrc"), defaultSaves),
            new ScummVmConfig(Path.Combine(flatpak, "config", "scummvm", "scummvm.ini"), Path.Combine(flatpak, "data", "scummvm", "saves")));
    }

    private static IReadOnlyList<ScummVmConfig> Existing(params ScummVmConfig[] configs) =>
        configs.Where(c => { try { return File.Exists(c.IniPath); } catch { return false; } }).ToList();

    /// <summary>Section → key → value, from <c>[section]</c> headers and <c>key=value</c> lines. Keys are matched
    /// ignoring case, as ScummVM's own config manager does; the last occurrence wins.</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseIni(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out section))
                    result[name] = section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var eq = line.IndexOf('=');
            if (section is null || eq <= 0) continue;
            section[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private static bool HasSaves(string dir, string prefix)
    {
        try
        {
            return Directory.Exists(dir) && new DirectoryInfo(dir).EnumerateFiles(prefix + ".*")
                .Any(f => f.LinkTarget is null && f.Length > 0 &&
                          f.Name.StartsWith(prefix + ".", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static string? Dir(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (v == "~" || v.StartsWith("~/")) v = Path.Combine(EmulatorPaths.Home, v.Length > 2 ? v[2..] : "");
        try { return Path.IsPathRooted(v) ? Path.GetFullPath(v) : null; }
        catch { return null; }
    }

    private static string SafeReal(string path)
    {
        try { return EmuDeckRoots.RealPath(path); }
        catch { return path; }
    }

    private static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}

/// <summary>One <c>scummvm.ini</c> and the save folder its games use when it names none.</summary>
public sealed record ScummVmConfig(string IniPath, string DefaultSaves);
