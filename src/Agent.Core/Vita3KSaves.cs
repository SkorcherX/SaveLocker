namespace SaveLocker.Agent;

/// <summary>
/// Vita3K (PS Vita), tasks/emulator-saves Phase 12 (bonus). A Vita game saves into one folder named by its title ID,
/// <c>ux0/user/&lt;user&gt;/savedata/PCSE00120/</c> (D4 shape 2, <see cref="TitleFolders"/>, scope <c>PCSE00120/**</c>). Redump has
/// no Vita set, so the name is the <c>TITLE</c> in the save's own <c>sce_sys/param.sfo</c> when it has one (the same on
/// every machine), else in the installed game's <c>ux0/app/&lt;ID&gt;/sce_sys/param.sfo</c> (only where the game is
/// installed — D1 joins the server's game by its folders whatever the name), else the ID. The region is the ID's
/// prefix's (<see cref="ConsoleTitles.PrefixRegion"/>). Vita3K has no states to sync (D3).
/// <para>
/// Where: EmuDeck sets <c>pref-path</c> to <c>Emulation/storage/Vita3K/</c> and links <c>Emulation/saves/Vita3K/saves</c>
/// with its <c>ux0/user/00/savedata</c> (both OSes; seen on the Deck and the maintainer's PC 2026-10-11, neither with a
/// save yet). Otherwise <c>config.yml</c>'s <c>pref-path</c> — <c>~/.config/Vita3K/config.yml</c>, or beside the exe
/// (EmuDeck for Windows' <c>%APPDATA%\EmuDeck\Emulators\Vita3K</c>) — defaulting to SDL's per-user folder,
/// <c>~/.local/share/Vita3K/Vita3K</c> or <c>%APPDATA%\Vita3K\Vita3K</c>.
/// </para>
/// </summary>
public static class Vita3KSaves
{
    public const string EmulatorName = "Vita3K";
    public const string System = "psvita";

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(Folders(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? Configs() : Array.Empty<(string, string)>()));

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<Vita3KFolders> folders)
    {
        var found = new List<(ScanCandidate, DateTime)>();
        foreach (var setup in folders)
        {
            var realDir = EmuDeckRoots.RealPath(setup.Saves);
            foreach (var game in TitleFolders.Find(setup.Saves, name => TitleFolders.SonyId(name) is { } id && id.Length == name.Length ? id : null))
            {
                var title = ParamSfo.Title(Path.Combine(game.Dirs[0].FullName, "sce_sys", "param.sfo"))
                            ?? (setup.Apps is { } apps ? ParamSfo.Title(Path.Combine(apps, game.Id, "sce_sys", "param.sfo")) : null);
                found.Add((new ScanCandidate(
                    Name: title ?? game.Id,
                    SuggestedSaveDir: realDir,
                    Source: ScanSource.Emulator,
                    HasSteamCloud: false,
                    EmulatorName: EmulatorName,
                    EmulatorSystem: System,
                    EmulatorRom: game.Id,
                    ViaEmuDeck: setup.EmuDeck,
                    IncludeGlobs: TitleFolders.Scope(game.Id, severalFolders: false),
                    Release: ConsoleTitles.Release(System, [game.Id])), game.WrittenUtc));
            }
        }
        return TitleFolders.Newest(found);
    }

    /// <summary>Every Vita3K user's <c>savedata</c> folder that exists (and its <c>app</c> folder), EmuDeck's first.
    /// <paramref name="configs"/>: (a <c>config.yml</c>, the <c>pref-path</c> to use when it names none).</summary>
    public static IReadOnlyList<Vita3KFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<(string ConfigYml, string DefaultPref)> configs)
    {
        var candidates = new List<Vita3KFolders>();
        foreach (var root in emuDeckRoots)
        {
            var ux0 = Path.Combine(root, "storage", "Vita3K", "ux0");
            candidates.Add(new Vita3KFolders(Path.Combine(root, "saves", "Vita3K", "saves"), Path.Combine(ux0, "app"), EmuDeck: true));
            candidates.AddRange(UsersIn(ux0, emuDeck: true));
        }
        foreach (var (configYml, defaultPref) in configs)
        {
            string? pref = null;
            try
            {
                if (File.Exists(configYml))
                    pref = Rpcs3Saves.ReadYamlMap(File.ReadAllText(configYml)).GetValueOrDefault("pref-path");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var ux0 = Path.Combine(EmulatorPaths.IniFolder(pref, Path.GetDirectoryName(configYml)!) ?? defaultPref, "ux0");
            candidates.AddRange(UsersIn(ux0, emuDeck: false));
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<Vita3KFolders>();
        foreach (var c in candidates)
        {
            bool exists;
            try { exists = Directory.Exists(c.Saves); } catch { exists = false; }
            var real = EmuDeckRoots.RealPath(c.Saves);
            if (!exists || !seen.Add(real)) continue;
            // The Deck's ~/Emulation holds the links and the SD card's Emulation the storage, so the app folder beside
            // the real savedata is the one to read there.
            found.Add(c with { Saves = real, Apps = c.Apps is { } a && Directory.Exists(a) ? a : AppsBeside(real) ?? c.Apps });
        }
        return found;
    }

    // EmuDeck links only user 00's savedata; a user made later in Vita3K (01, 02…) saves beside it.
    private static IEnumerable<Vita3KFolders> UsersIn(string ux0, bool emuDeck) =>
        RomSaves.SafeSubdirs(Path.Combine(ux0, "user")).Order(StringComparer.Ordinal)
            .Select(user => new Vita3KFolders(Path.Combine(user, "savedata"), Path.Combine(ux0, "app"), emuDeck));

    /// <summary><c>ux0/app</c> for a <c>ux0/user/&lt;user&gt;/savedata</c> folder, else null.</summary>
    private static string? AppsBeside(string savedata)
    {
        var user = Path.GetDirectoryName(Path.GetDirectoryName(savedata));
        var ux0 = Path.GetDirectoryName(user);
        return Path.GetFileName(savedata) == "savedata" && Path.GetFileName(user) == "user" && ux0 is not null
            ? Path.Combine(ux0, "app") : null;
    }

    /// <summary>Where a standalone Vita3K keeps <c>config.yml</c>, and its default <c>pref-path</c>, per platform.</summary>
    public static IReadOnlyList<(string ConfigYml, string DefaultPref)> Configs() => OperatingSystem.IsWindows()
        ?
        [
            (Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "Vita3K", "config.yml"), Path.Combine(EmulatorPaths.AppData, "Vita3K", "Vita3K")),
            (Path.Combine(EmulatorPaths.AppData, "Vita3K", "Vita3K", "config.yml"), Path.Combine(EmulatorPaths.AppData, "Vita3K", "Vita3K")),
        ]
        : [(Path.Combine(EmulatorPaths.XdgConfig, "Vita3K", "config.yml"), Path.Combine(EmulatorPaths.XdgData, "Vita3K", "Vita3K"))];
}

/// <summary>One Vita3K setup: its <c>savedata</c> folder, and the <c>app</c> folder installed games' titles are read from.</summary>
public sealed record Vita3KFolders(string Saves, string? Apps, bool EmuDeck = false);
