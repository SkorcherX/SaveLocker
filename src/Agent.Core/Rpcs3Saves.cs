namespace SaveLocker.Agent;

/// <summary>
/// RPCS3 (PS3), tasks/emulator-saves Phase 5a. A PS3 game saves into <c>dev_hdd0/home/&lt;user&gt;/savedata</c>, a folder
/// per save named from its game ID (<c>BCUS98132-GAMEAUTOSAVE</c> on the maintainer's Deck, 2026-10-11), each with a
/// <c>PARAM.SFO</c> — PPSSPP's shape and scope (<see cref="TitleFolders"/>, <c>BCUS98132*/**</c>). Named by its ID
/// (<see cref="ConsoleTitles"/>, Redump's PS3 set: "Heavenly Sword"), else by the <c>TITLE</c> in its save's
/// <see cref="ParamSfo"/> — a PSN game (<c>NPUB…</c>). RPCS3 has no states to sync (D3); its <c>trophy</c> folder,
/// which EmuDeck links too, is not synced.
/// <para>
/// Where: EmuDeck links <c>Emulation/saves/rpcs3/saves</c> with <c>Emulation/storage/rpcs3/dev_hdd0/home/00000001/savedata</c>
/// on both OSes (on the Deck the link is the <c>saves</c> side, on the maintainer's PC the <c>savedata</c> side).
/// Otherwise RPCS3's <c>vfs.yml</c> says where <c>dev_hdd0</c> is (default <c>$(EmulatorDir)dev_hdd0/</c>): in
/// <c>~/.config/rpcs3</c> or the Flatpak's <c>…/net.rpcs3.RPCS3/config/rpcs3</c>, or in EmuDeck for Windows'
/// <c>%APPDATA%\EmuDeck\Emulators\RPCS3\config</c>, where <c>$(EmulatorDir)</c> is the folder of <c>rpcs3.exe</c>.
/// Every user's <c>savedata</c> is read.
/// </para>
/// </summary>
public static class Rpcs3Saves
{
    public const string EmulatorName = "RPCS3";
    public const string System = "ps3";

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(Folders(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? EmulatorDirs() : Array.Empty<string>()));

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<RomSaveFolders> folders)
    {
        var found = new List<(ScanCandidate, DateTime)>();
        foreach (var setup in folders)
        {
            var realDir = EmuDeckRoots.RealPath(setup.Saves);
            foreach (var game in TitleFolders.Find(setup.Saves, TitleFolders.SonyId))
            {
                var sfoTitle = game.Dirs.Select(d => ParamSfo.Title(Path.Combine(d.FullName, "PARAM.SFO"))).FirstOrDefault(t => t is not null);
                found.Add((new ScanCandidate(
                    Name: ConsoleTitles.For(System, game.Id)?.Title ?? sfoTitle ?? game.Id,
                    SuggestedSaveDir: realDir,
                    Source: ScanSource.Emulator,
                    HasSteamCloud: false,
                    EmulatorName: EmulatorName,
                    EmulatorSystem: System,
                    EmulatorRom: game.Id,
                    ViaEmuDeck: setup.EmuDeck,
                    IncludeGlobs: TitleFolders.Scope(game.Id, severalFolders: true),
                    Release: ConsoleTitles.Release(System, [game.Id])), game.WrittenUtc));
            }
        }
        return TitleFolders.Newest(found);
    }

    /// <summary>Every RPCS3 setup's <c>savedata</c> folders that exist, EmuDeck's first. <paramref name="emulatorDirs"/>:
    /// the folders a standalone RPCS3 keeps <c>vfs.yml</c> in (or its <c>config</c> folder).</summary>
    public static IReadOnlyList<RomSaveFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<string> emulatorDirs)
    {
        var candidates = new List<RomSaveFolders>();
        foreach (var root in emuDeckRoots)
            candidates.Add(new RomSaveFolders(Path.Combine(root, "saves", "rpcs3", "saves"), null, EmuDeck: true));
        foreach (var dir in emulatorDirs)
            if (DevHdd0(dir) is { } hdd)
                foreach (var user in RomSaves.SafeSubdirs(Path.Combine(hdd, "home")))
                    candidates.Add(new RomSaveFolders(Path.Combine(user, "savedata"), null));
        return RomSaves.Existing(candidates);
    }

    /// <summary>
    /// The <c>dev_hdd0</c> folder <paramref name="emulatorDir"/>'s <c>vfs.yml</c> names — read from
    /// <c>config/vfs.yml</c> (current Windows builds) or <c>vfs.yml</c> — with <c>$(EmulatorDir)</c> filled in: its own
    /// value when set, else <paramref name="emulatorDir"/>. Null when there is no <c>vfs.yml</c>: RPCS3 never ran there.
    /// </summary>
    public static string? DevHdd0(string emulatorDir)
    {
        var vfs = new[] { Path.Combine(emulatorDir, "config", "vfs.yml"), Path.Combine(emulatorDir, "vfs.yml") }.FirstOrDefault(File.Exists);
        if (vfs is null) return null;
        Dictionary<string, string> map;
        try { map = ReadYamlMap(File.ReadAllText(vfs)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        var emulator = map.GetValueOrDefault("$(EmulatorDir)") is { Length: > 0 } e ? e : emulatorDir + Path.DirectorySeparatorChar;
        var hdd = map.GetValueOrDefault("/dev_hdd0/") is { Length: > 0 } h ? h : "$(EmulatorDir)dev_hdd0/";
        return EmulatorPaths.IniFolder(hdd.Replace("$(EmulatorDir)", emulator), emulatorDir);
    }

    /// <summary>A flat YAML map (<c>key: value</c>, values plain or quoted) — all <c>vfs.yml</c> is.</summary>
    public static Dictionary<string, string> ReadYamlMap(string yaml)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#') || line == "---") continue;
            // A key is "/dev_hdd0/" or "$(EmulatorDir)": the separator is the first ": " (or a trailing ':').
            var sep = line.IndexOf(": ", StringComparison.Ordinal);
            var key = sep >= 0 ? line[..sep] : line.EndsWith(':') ? line[..^1] : null;
            if (key is null) continue;
            var value = sep >= 0 ? line[(sep + 2)..].Trim() : "";
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            map[key.Trim().Trim('"', '\'')] = value;
        }
        return map;
    }

    /// <summary>The folders a standalone RPCS3 keeps its <c>vfs.yml</c> in, per platform.</summary>
    public static IReadOnlyList<string> EmulatorDirs() => OperatingSystem.IsWindows()
        ? [Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "RPCS3")]
        : [Path.Combine(EmulatorPaths.XdgConfig, "rpcs3"), Path.Combine(EmulatorPaths.Flatpak("net.rpcs3.RPCS3"), "config", "rpcs3")];
}
