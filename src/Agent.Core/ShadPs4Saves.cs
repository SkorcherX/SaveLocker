namespace SaveLocker.Agent;

/// <summary>
/// shadPS4 (PS4), tasks/emulator-saves Phase 13 (bonus). A PS4 game saves into a folder named by its title ID inside a
/// user's save folder, one subfolder per save: <c>CUSA00552/SPRJ0005/</c> (D4 shape 2, <see cref="TitleFolders"/>,
/// scope <c>CUSA00552/**</c>). Every save carries <c>sce_sys/param.sfo</c>, written by shadPS4 itself, whose
/// <c>MAINTITLE</c> names the game — inside the save, so the same on every machine; else the ID. A <c>CUSA</c> ID is no
/// region's, and Redump has no PS4 set, so the release has none. shadPS4 has no states to sync (D3).
/// <para>
/// <b>Two layouts.</b> Older builds write <c>savedata/&lt;user id&gt;/&lt;CUSA…&gt;/</c> in the user folder; current ones
/// <c>home/&lt;user id&gt;/savedata/&lt;CUSA…&gt;/</c> (<c>save_instance.cpp</c> <c>MakeTitleSavePath</c>). Both are read,
/// and the game's folder is the per-user one, so a machine on either layout joins the same game; one game in both
/// (an upgraded install) is the copy written last.
/// </para>
/// <para>
/// Where: EmuDeck links <c>Emulation/saves/shadps4/saves</c> with the old <c>savedata</c> — <c>~/.local/share/shadPS4/savedata</c>
/// on SteamOS (the Deck, 2026-10-11, AppImage, no save yet), <c>%APPDATA%\EmuDeck\Emulators\ShadPS4-qt\user\savedata</c>
/// on Windows (the maintainer's PC, 2026-10-11). The user folder itself: <c>user/</c> beside the exe when it exists,
/// else <c>$XDG_DATA_HOME/shadPS4</c> or <c>%APPDATA%\shadPS4</c> (<c>path_util.cpp</c>).
/// </para>
/// </summary>
public static class ShadPs4Saves
{
    public const string EmulatorName = "shadPS4";
    public const string System = "ps4";

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(Folders(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? UserDirs() : Array.Empty<string>()));

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<RomSaveFolders> folders)
    {
        var found = new List<(ScanCandidate, DateTime)>();
        foreach (var setup in folders)
        {
            var realDir = EmuDeckRoots.RealPath(setup.Saves);
            foreach (var game in TitleFolders.Find(setup.Saves, TitleId))
            {
                var title = RomSaves.SafeSubdirs(game.Dirs[0].FullName).Order(StringComparer.Ordinal)
                    .Select(save => ParamSfo.Title(Path.Combine(save, "sce_sys", "param.sfo"))).FirstOrDefault(t => t is not null);
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

    /// <summary>A folder named by a title ID and nothing else (<c>CUSA00552</c>).</summary>
    private static string? TitleId(string name) => TitleFolders.SonyId(name) is { } id && id.Length == name.Length ? id : null;

    /// <summary>
    /// Every user's save folder that exists, in either layout, EmuDeck's first: <c>savedata/&lt;user&gt;</c> under
    /// EmuDeck's link and each user folder's <c>savedata</c>, <c>home/&lt;user&gt;/savedata</c> under each user folder.
    /// </summary>
    public static IReadOnlyList<RomSaveFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<string> userDirs)
    {
        var candidates = new List<RomSaveFolders>();
        foreach (var root in emuDeckRoots)
            candidates.AddRange(UsersIn(Path.Combine(root, "saves", "shadps4", "saves"), emuDeck: true));
        foreach (var user in userDirs)
        {
            candidates.AddRange(UsersIn(Path.Combine(user, "savedata"), emuDeck: false));
            foreach (var home in RomSaves.SafeSubdirs(Path.Combine(user, "home")))
                candidates.Add(new RomSaveFolders(Path.Combine(home, "savedata"), null));
        }
        return RomSaves.Existing(candidates);
    }

    // The old layout's per-user folders: a title ID directly under savedata is no user's.
    private static IEnumerable<RomSaveFolders> UsersIn(string savedata, bool emuDeck) =>
        RomSaves.SafeSubdirs(savedata)
            .Where(d => TitleId(Path.GetFileName(d)) is null)
            .Select(d => new RomSaveFolders(d, null, EmuDeck: emuDeck));

    /// <summary>The user folders a standalone shadPS4 keeps its saves in, per platform.</summary>
    public static IReadOnlyList<string> UserDirs() => OperatingSystem.IsWindows()
        ? [Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "ShadPS4-qt", "user"), Path.Combine(EmulatorPaths.AppData, "shadPS4")]
        : [Path.Combine(EmulatorPaths.XdgData, "shadPS4")];
}
