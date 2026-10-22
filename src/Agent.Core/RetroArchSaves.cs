namespace SaveLocker.Agent;

/// <summary>
/// One Add-games candidate per RetroArch save file — the first row of <see cref="RomSaves"/>. Discovery
/// starts from the SAVE, not from a library: under EmuDeck the ROMs are launched by ES-DE or Steam ROM
/// Manager shortcuts, which hand RetroArch a path directly, so its own playlists are usually empty — while
/// a game with nothing saved has nothing to sync anyway.
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
    public const string StatesKey = RomSaves.StatesKey;

    /// <summary>RetroArch as a row of the shared reader: <c>&lt;rom&gt;.srm</c> (+ <c>.rtc</c>), states
    /// <c>&lt;rom&gt;.state*</c>, one level of per-core folders.</summary>
    public static readonly RomSaveRules Rules = new(EmulatorName, ".srm",
        (rom, _) => IncludeGlobsFor(rom), StateGlobsFor, PerCoreFolders: true);

    /// <summary>The candidates on this machine. Never throws for a missing or unreadable folder.</summary>
    public static IReadOnlyList<ScanCandidate> Scan()
    {
        var roots = EmuDeckRoots.Find();
        return Scan(RetroArchConfig.Folders(), roots, GamelistXml.Find(roots));
    }

    /// <summary>The same, from explicit folders — what the tests drive. <paramref name="gamelists"/> names
    /// arcade ROMs (none when omitted).</summary>
    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<RetroArchFolders> folders, IEnumerable<string> emuDeckRoots,
        GamelistXml? gamelists = null) =>
        RomSaves.Scan(Rules, folders.Select(f => new RomSaveFolders(f.Saves, f.States, f.EmuDeck)), emuDeckRoots, gamelists,
            StatesDirFor);

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
        lookIn.AddRange(RomSaves.SafeSubdirs(statesRoot).Where(d => coreDir is null || !PathsEqual(d, coreDir)));
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
}
