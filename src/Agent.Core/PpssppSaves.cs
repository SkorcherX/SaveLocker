namespace SaveLocker.Agent;

/// <summary>
/// PPSSPP (PSP), tasks/emulator-saves Phase 11. A PSP game saves into the memory stick's <c>PSP/SAVEDATA</c>, a
/// folder per save named from its game ID (<c>ULUS10041DATA00</c>, <c>ULUS10041SYSTEM</c>: a game often has several),
/// each with a <c>PARAM.SFO</c> (D4 shape 2, <see cref="TitleFolders"/>). A game is every folder of its ID, scope
/// <c>ULUS10041*/**</c>, named by its ID (<see cref="ConsoleTitles"/>, Redump's PSP set, the region beside the name),
/// else by the <c>TITLE</c> in its save's <see cref="ParamSfo"/> — a PSN game Redump has no disc of — which is inside the
/// save and so the same on every machine.
/// <para>
/// States are <c>PSP/PPSSPP_STATE/&lt;ID&gt;_&lt;version&gt;_&lt;slot&gt;.ppst</c>, a <c>.jpg</c> beside each, and the undo
/// state, all named from the game ID (<c>SaveState::GenerateFullDiscId</c>) → states scope <c>ULUS10041_*</c>. A game
/// with states but no save yet is a game too (<see cref="ScanCandidate.StatesOnly"/>).
/// </para>
/// <para>
/// Where: EmuDeck links <c>Emulation/saves/ppsspp/{saves,states}</c> with the emulator's <c>SAVEDATA</c> and
/// <c>PPSSPP_STATE</c> — the Flatpak's <c>~/.var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP</c> on SteamOS (the Deck,
/// 2026-10-11), the portable <c>%APPDATA%\EmuDeck\Emulators\PPSSPP\memstick\PSP</c> on Windows (the maintainer's PC,
/// 2026-10-11, where the emulator's folders are the links). A standalone install: <c>~/.config/ppsspp/PSP</c>, the
/// Flatpak's folder, or <c>Documents\PPSSPP\PSP</c> on Windows. A portable copy's <c>memstick</c> beside an exe
/// anywhere else is not looked for. RetroArch's PPSSPP core keeps PSP saves in its own folder, not read here.
/// </para>
/// </summary>
public static class PpssppSaves
{
    public const string EmulatorName = "PPSSPP";
    public const string System = "psp";

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(Folders(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? MemoryStickDirs() : Array.Empty<string>()));

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<RomSaveFolders> folders)
    {
        var setups = folders.ToList();
        var found = new List<(ScanCandidate, DateTime)>();
        foreach (var setup in setups)
            foreach (var game in TitleFolders.Find(setup.Saves, TitleFolders.SonyId))
            {
                var sfoTitle = game.Dirs.Select(d => ParamSfo.Title(Path.Combine(d.FullName, "PARAM.SFO"))).FirstOrDefault(t => t is not null);
                found.Add((Candidate(setup, game.Id, sfoTitle, statesOnly: false), game.WrittenUtc));
            }

        // A game played only from states so far, before its first in-game save: listed too, so its states sync and
        // its SAVEDATA folders join it once they exist. A save anywhere on this machine wins over its states.
        var saved = found.Select(f => f.Item1.EmulatorRom!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var setup in setups)
            foreach (var (id, written) in StateOnlyGames(setup.States))
                if (!saved.Contains(id))
                    found.Add((Candidate(setup, id, sfoTitle: null, statesOnly: true), written));
        return TitleFolders.Newest(found);
    }

    private static ScanCandidate Candidate(RomSaveFolders setup, string id, string? sfoTitle, bool statesOnly)
    {
        DeclaredSavePath[]? states = setup.States is { } s
            ? [new DeclaredSavePath(RomSaves.StatesKey, EmuDeckRoots.RealPath(s), StateGlobsFor(id))]
            : null;
        return new ScanCandidate(
            Name: ConsoleTitles.For(System, id)?.Title ?? sfoTitle ?? id,
            SuggestedSaveDir: EmuDeckRoots.RealPath(setup.Saves),
            Source: ScanSource.Emulator,
            HasSteamCloud: false,
            EmulatorName: EmulatorName,
            EmulatorSystem: System,
            EmulatorRom: id,
            ViaEmuDeck: setup.EmuDeck,
            IncludeGlobs: TitleFolders.Scope(id, severalFolders: true),
            ExtraSaveDirs: states,
            Release: ConsoleTitles.Release(System, [id]),
            StatesOnly: statesOnly);
    }

    /// <summary>The games with a non-empty state (<c>ULUS10041_1.00_0.ppst</c>) in a states folder, and when the newest
    /// was written. <c>load_undo.ppst</c> and other files no game's ID starts are skipped.</summary>
    public static IEnumerable<(string Id, DateTime WrittenUtc)> StateOnlyGames(string? statesDir)
    {
        FileInfo[] files;
        try { files = statesDir is not null && Directory.Exists(statesDir) ? new DirectoryInfo(statesDir).GetFiles("*.ppst") : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return files
            .Where(f => f.Length > 0 && f.LinkTarget is null)
            .Select(f => (File: f, Id: TitleFolders.SonyId(f.Name)))
            .Where(x => x.Id is not null && x.File.Name.Length > x.Id.Length && x.File.Name[x.Id.Length] == '_')
            .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Max(x => x.File.LastWriteTimeUtc)))
            .ToList();
    }

    /// <summary>One game's states: every slot, its thumbnail and the undo state.</summary>
    public static IReadOnlyList<string> StateGlobsFor(string gameId) => [$"{gameId}_*"];

    /// <summary>Every PPSSPP setup's (<c>SAVEDATA</c>, <c>PPSSPP_STATE</c>) folders that exist, EmuDeck's first.
    /// <paramref name="memoryStickDirs"/>: standalone installs' <c>PSP</c> folders.</summary>
    public static IReadOnlyList<RomSaveFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<string> memoryStickDirs)
    {
        var candidates = new List<RomSaveFolders>();
        foreach (var root in emuDeckRoots)
            candidates.Add(new RomSaveFolders(Path.Combine(root, "saves", "ppsspp", "saves"),
                Path.Combine(root, "saves", "ppsspp", "states"), EmuDeck: true));
        foreach (var psp in memoryStickDirs)
            candidates.Add(new RomSaveFolders(Path.Combine(psp, "SAVEDATA"), Path.Combine(psp, "PPSSPP_STATE")));
        return RomSaves.Existing(candidates);
    }

    /// <summary>The memory stick <c>PSP</c> folders a standalone PPSSPP uses, per platform.</summary>
    public static IReadOnlyList<string> MemoryStickDirs() => OperatingSystem.IsWindows()
        ?
        [
            Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "PPSSPP", "memstick", "PSP"),
            Path.Combine(EmulatorPaths.Documents, "PPSSPP", "PSP"),
        ]
        :
        [
            Path.Combine(EmulatorPaths.XdgConfig, "ppsspp", "PSP"),
            Path.Combine(EmulatorPaths.Flatpak("org.ppsspp.PPSSPP"), "config", "ppsspp", "PSP"),
        ];
}
