namespace SaveLocker.Agent;

/// <summary>
/// One Add-games candidate per RetroArch save file. Discovery starts from the SAVE, not from a
/// library: under EmuDeck the ROMs are launched by ES-DE or Steam ROM Manager shortcuts, which hand
/// RetroArch a path directly, so its own playlists are usually empty — while a game with nothing
/// saved has nothing to sync anyway.
/// <para>
/// Every ROM's save shares one folder (<c>&lt;rom&gt;.srm</c> side by side, or one folder per core
/// with "Sort saves by core"). The candidate's folder is the one the file is actually in, and its
/// <see cref="ScanCandidate.IncludeGlobs"/> name just that ROM's files, so the game's archive holds
/// <c>&lt;rom&gt;.srm</c> at its root whichever layout each machine uses.
/// </para>
/// <para>
/// Save states are the game's second folder (<see cref="StatesKey"/>), scoped the same way to
/// <c>&lt;rom&gt;.state*</c>. Always declared, even before the first state exists, so every machine
/// defines the game with the same folders (tasks/emulator-saves → <i>Save states — decided</i>).
/// </para>
/// </summary>
public static class RetroArchSaves
{
    public const string EmulatorName = "RetroArch";

    /// <summary>The save-states folder's key on the server — the same on every machine.</summary>
    public const string StatesKey = "states";

    /// <summary>The candidates on this machine. Never throws for a missing or unreadable folder.</summary>
    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(RetroArchConfig.Folders(), EmuDeckRoots.Find());

    /// <summary>The same, from explicit folders — what the tests drive.</summary>
    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<RetroArchFolders> folders, IEnumerable<string> emuDeckRoots)
    {
        var systems = RomSystems(emuDeckRoots);
        var found = new List<(ScanCandidate Candidate, DateTime WrittenUtc)>();

        foreach (var f in folders)
        {
            // The folder itself (sorting off), then one level of per-core folders (sorting on).
            found.AddRange(SavesIn(f.Saves, core: null, f.States, f.EmuDeck, systems));
            foreach (var sub in SafeSubdirs(f.Saves))
                found.AddRange(SavesIn(sub, core: Path.GetFileName(sub), f.States, f.EmuDeck, systems));
        }

        // The same ROM's save under two cores is one game: the most recently written copy is the one being
        // played. Two DIFFERENT ROMs that clean to one title (two regions, or Tetris on two consoles) stay
        // two candidates — the enroller gives the second a name of its own (Enroller.NamesFor).
        return found
            .GroupBy(f => f.Candidate.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.WrittenUtc).First().Candidate)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The include patterns that make one ROM's files a game: its SRAM save and, for cartridges with
    /// a clock (Pokémon Gold/Silver/Crystal, Boktai), the <c>.rtc</c> beside it. Anchored at the save
    /// folder's root, so a same-named file in some other core's folder is never picked up.
    /// </summary>
    public static IReadOnlyList<string> IncludeGlobsFor(string romBaseName) =>
        new[] { romBaseName + ".srm", romBaseName + ".rtc" };

    /// <summary>One ROM's save states: <c>&lt;rom&gt;.state</c>, the numbered slots, <c>.state.auto</c> and
    /// each one's <c>.png</c> thumbnail.</summary>
    public static IReadOnlyList<string> StateGlobsFor(string romBaseName) =>
        new[] { romBaseName + ".state*" };

    /// <summary>
    /// The folder holding this ROM's states. "Sort save states by core" is a separate setting from
    /// sorting saves, so the states may sit in a core folder while the save does not, or the reverse:
    /// whichever folder already has them wins, then the save's own core folder, then the states root.
    /// </summary>
    public static string StatesDirFor(string statesRoot, string? core, string romBase)
    {
        var coreDir = core is null ? null : Path.Combine(statesRoot, core);
        var lookIn = new List<string>();
        if (coreDir is not null) lookIn.Add(coreDir);
        lookIn.Add(statesRoot);
        lookIn.AddRange(SafeSubdirs(statesRoot).Where(d => coreDir is null || !PathsEqual(d, coreDir)));
        foreach (var dir in lookIn)
            if (HasStates(dir, romBase)) return dir;
        return coreDir is not null && Directory.Exists(coreDir) ? coreDir : statesRoot;
    }

    private static bool HasStates(string dir, string romBase)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, romBase + ".state*").Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static IEnumerable<(ScanCandidate, DateTime)> SavesIn(
        string dir, string? core, string statesRoot, bool viaEmuDeck, IReadOnlyDictionary<string, string> systems)
    {
        FileInfo[] files;
        try { files = new DirectoryInfo(dir).GetFiles("*.srm"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        var realDir = EmuDeckRoots.RealPath(dir);
        foreach (var file in files)
        {
            // A link is never archived (SaveArchive skips them), so a linked save could never sync.
            if (file.LinkTarget is not null || file.Length == 0) continue;
            var romBase = Path.GetFileNameWithoutExtension(file.Name);
            // Both are legal in a Linux file name: '*' is the glob wildcard, so the name cannot be scoped,
            // and the server refuses ':' in an include (it reads as a Windows drive) — which would fail
            // the whole enrollment batch, not just this game.
            if (romBase.Length == 0 || romBase.Contains('*') || romBase.Contains(':')) continue;

            yield return (new ScanCandidate(
                Name: RomNames.CleanTitle(romBase),
                SuggestedSaveDir: realDir,
                Source: ScanSource.Emulator,
                HasSteamCloud: false,
                EmulatorName: EmulatorName,
                EmulatorSystem: systems.GetValueOrDefault(romBase),
                EmulatorCore: core,
                EmulatorRom: romBase,
                ViaEmuDeck: viaEmuDeck,
                IncludeGlobs: IncludeGlobsFor(romBase),
                ExtraSaveDirs: new[]
                {
                    new DeclaredSavePath(StatesKey, EmuDeckRoots.RealPath(StatesDirFor(statesRoot, core, romBase)),
                        StateGlobsFor(romBase)),
                }), file.LastWriteTimeUtc);
        }
    }

    /// <summary>
    /// ROM file name (no extension) → the <c>Emulation/roms/&lt;system&gt;</c> folder it is in. RetroArch
    /// names a save after its ROM, so this is how a save learns which console it belongs to.
    /// </summary>
    private static IReadOnlyDictionary<string, string> RomSystems(IEnumerable<string> emuDeckRoots)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in emuDeckRoots)
        {
            foreach (var systemDir in SafeSubdirs(Path.Combine(root, "roms")))
            {
                string[] roms;
                try { roms = Directory.GetFiles(systemDir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                var system = Path.GetFileName(systemDir);
                foreach (var rom in roms)
                    map.TryAdd(Path.GetFileNameWithoutExtension(rom), system);
            }
        }
        return map;
    }

    private static string[] SafeSubdirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
