namespace SaveLocker.Agent;

/// <summary>
/// melonDS (Nintendo DS), a row of <see cref="RomSaves"/> (tasks/emulator-saves Phase 8). Saves are
/// <c>&lt;rom&gt;.sav</c>, states <c>&lt;rom&gt;.ml1</c>…<c>.ml8</c> (melonDS <c>EmuInstance::getAssetPath</c> /
/// <c>getSavestateName</c>), each in its own folder or — with the path left empty — beside the ROM.
/// <para>
/// Where: EmuDeck sets <c>SaveFilePath</c>/<c>SavestatePath</c> to <c>Emulation/saves/melonds/{saves,states}</c>,
/// real folders, not links, on both OSes (<c>melonDS_setupSaves</c>). Otherwise melonDS's own config, in its
/// <c>portable/</c> folder, beside a portable Windows build's exe, or Qt's config folder + <c>melonDS</c>
/// (<c>~/.config/melonDS</c>, the Flatpak's <c>~/.var/app/net.kuribo64.melonDS/config/melonDS</c>). 1.0 reads
/// <c>melonDS.toml</c> (<c>[Instance0] SaveFilePath</c>) and falls back to the old <c>melonDS.ini</c>
/// (<c>SaveFilePath=</c>) only when there is no TOML (<c>Config::Load</c>) — EmuDeck writes both.
/// </para>
/// </summary>
public static class MelonDsSaves
{
    public const string EmulatorName = "melonDS";

    public static readonly RomSaveRules Rules = new(EmulatorName, ".sav",
        (rom, ext) => new[] { rom + ext }, rom => new[] { rom + ".ml*" }, System: "nds");

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? ConfigRoots() : Array.Empty<string>());

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<string> emuDeckRoots, IEnumerable<string> configRoots)
    {
        var roots = emuDeckRoots.ToList();
        return RomSaves.Scan(Rules, Folders(roots, configRoots), roots);
    }

    /// <summary>Every melonDS setup's (saves, states) folders that exist, EmuDeck's first.</summary>
    public static IReadOnlyList<RomSaveFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<string> configRoots)
    {
        var candidates = new List<RomSaveFolders>();
        foreach (var root in emuDeckRoots)
            candidates.Add(new RomSaveFolders(Path.Combine(root, "saves", "melonds", "saves"),
                Path.Combine(root, "saves", "melonds", "states"), EmuDeck: true));

        foreach (var dir in configRoots)
        {
            if (ReadConfig(dir) is not { } cfg) continue;
            // An empty path means beside the ROM: the last ROM folder the user opened is the one we can know.
            // Other emulators (mGBA, …) write .sav beside their ROMs too, so there only a DS ROM's save counts.
            var saves = Absolute(cfg.SaveFilePath, dir);
            var besideRom = saves is null;
            saves ??= Absolute(cfg.LastRomFolder, dir);
            if (saves is null) continue;
            var states = Absolute(cfg.SavestatePath, dir) ?? Absolute(cfg.LastRomFolder, dir) ?? saves;
            candidates.Add(new RomSaveFolders(saves, states, RomExtensions: besideRom ? RomExtensions : null));
        }
        return RomSaves.Existing(candidates);
    }

    /// <summary>What a DS ROM beside its save is called (melonDS opens these, and archives holding one).</summary>
    public static readonly IReadOnlyList<string> RomExtensions = [".nds", ".dsi", ".srl", ".ids", ".zip", ".7z", ".rar"];

    /// <summary>Folders a standalone melonDS keeps its config in, per platform: Qt's config folder
    /// (<c>$XDG_CONFIG_HOME</c>, else <c>~/.config</c>) + <c>melonDS</c>.</summary>
    public static IReadOnlyList<string> ConfigRoots()
    {
        var home = EmulatorPaths.Home;
        if (OperatingSystem.IsWindows())
            return new[]
            {
                // EmuDeck for Windows' portable copy (its configFile is beside the exe).
                Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "melonDS"),
                Path.Combine(EmulatorPaths.LocalAppData, "melonDS"),
                Path.Combine(EmulatorPaths.AppData, "melonDS"),
            };
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x && EmulatorPaths.HomeOverride is null
            ? x : Path.Combine(home, ".config");
        return new[]
        {
            Path.Combine(xdgConfig, "melonDS"),
            Path.Combine(home, ".var", "app", "net.kuribo64.melonDS", "config", "melonDS"),
        };
    }

    /// <summary>The settings this reader needs from one config folder, or null when it has no config.</summary>
    public sealed record Config(string? SaveFilePath, string? SavestatePath, string? LastRomFolder);

    public static Config? ReadConfig(string dir)
    {
        var toml = Path.Combine(dir, "melonDS.toml");
        if (File.Exists(toml))
        {
            var t = Toml(SafeRead(toml));
            return new Config(t.GetValueOrDefault("Instance0.SaveFilePath"), t.GetValueOrDefault("Instance0.SavestatePath"),
                t.GetValueOrDefault("LastROMFolder"));
        }
        var ini = Path.Combine(dir, "melonDS.ini");
        if (!File.Exists(ini)) return null;
        var i = Ini(SafeRead(ini));
        return new Config(i.GetValueOrDefault("SaveFilePath"), i.GetValueOrDefault("SavestatePath"), i.GetValueOrDefault("LastROMFolder"));
    }

    /// <summary>
    /// The string values of a TOML file as dotted keys (<c>Instance0.SaveFilePath</c>), from <c>[table]</c>
    /// headers and <c>key = "value"</c> / <c>key = 'value'</c> lines — all melonDS's own writer produces for a
    /// path. Arrays, inline tables and multi-line strings are skipped.
    /// </summary>
    public static Dictionary<string, string> Toml(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var table = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                table = line.Trim('[', ']').Trim().Replace("\"", "");
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().Trim('"');
            var value = line[(eq + 1)..].Trim();
            string? parsed = null;
            if (value.Length >= 2 && value[0] == '\'' && value.IndexOf('\'', 1) is var end and > 0)
                parsed = value[1..end];
            else if (value.Length >= 2 && value[0] == '"')
                parsed = BasicString(value);
            if (parsed is not null) result[table.Length == 0 ? key : table + "." + key] = parsed;
        }
        return result;
    }

    private static string? BasicString(string value)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '"') return sb.ToString();
            if (ch == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => next });
                continue;
            }
            sb.Append(ch);
        }
        return null;
    }

    /// <summary>melonDS's old <c>Key=value</c> lines (<c>%31[A-Za-z_0-9]=%[^\t\r\n]</c>).</summary>
    public static Dictionary<string, string> Ini(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    /// <summary>A path from the config made absolute, or null when unset. melonDS opens a relative one from its
    /// working folder, which for the portable builds that would write one is its own (config) folder.</summary>
    private static string? Absolute(string? value, string configDir)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(configDir, value)); }
        catch { return null; }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
}
