namespace SaveLocker.Agent;

/// <summary>
/// The reader every "save named after the ROM" emulator is a row of (tasks/emulator-saves D4, shape 1):
/// one folder every game shares, one file per game named after its ROM or arcade set (<c>Chrono Trigger
/// (USA).srm</c>, <c>Pokemon Platinum (USA).sav</c>, <c>scud.nv</c>), and optionally a states folder where
/// the same name prefixes each slot. One Emulator candidate per save file, scoped to that game's files, its
/// states as a second folder declared even before the first state exists (D3).
/// <para>
/// Discovery starts from the SAVE, not from a library: a game with nothing saved has nothing to sync, and
/// under EmuDeck the ROMs are launched by ES-DE or Steam ROM Manager, so no emulator library is reliable.
/// </para>
/// </summary>
public static class RomSaves
{
    /// <summary>The save-states folder's key on the server — the same on every machine and emulator.</summary>
    public const string StatesKey = "states";

    /// <summary>
    /// The candidates in <paramref name="folders"/> under <paramref name="rules"/>. Never throws for a missing
    /// or unreadable folder. <paramref name="statesDir"/> picks the states folder for one save (RetroArch's
    /// per-core sorting); by default it is the setup's states folder.
    /// </summary>
    public static IReadOnlyList<ScanCandidate> Scan(
        RomSaveRules rules, IEnumerable<RomSaveFolders> folders, IEnumerable<string> emuDeckRoots, GamelistXml? gamelists = null,
        Func<string, string?, string, string>? statesDir = null)
    {
        var systems = rules.System is null ? RomSystems(emuDeckRoots) : null;
        gamelists ??= GamelistXml.None;
        var found = new List<(ScanCandidate Candidate, DateTime WrittenUtc)>();

        foreach (var f in folders)
        {
            found.AddRange(SavesIn(rules, f, f.Saves, core: null, systems, gamelists, statesDir));
            // One level of per-core folders (RetroArch's "Sort saves by core").
            if (rules.PerCoreFolders)
                foreach (var sub in SafeSubdirs(f.Saves))
                    found.AddRange(SavesIn(rules, f, sub, core: Path.GetFileName(sub), systems, gamelists, statesDir));
        }

        // The same ROM's save in two places (two cores, two setups) is one game: the most recently written copy
        // is the one being played, and a played one always beats EmuDeck's untouched seed. Two DIFFERENT ROMs that
        // clean to one title stay two candidates — the enroller gives the second a name of its own (Enroller.NamesFor).
        return found
            .GroupBy(f => f.Candidate.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(f => f.Candidate.UntouchedSeed).ThenByDescending(f => f.WrittenUtc).First().Candidate)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<(ScanCandidate, DateTime)> SavesIn(
        RomSaveRules rules, RomSaveFolders setup, string dir, string? core, IReadOnlyDictionary<string, string>? systems,
        GamelistXml gamelists, Func<string, string?, string, string>? statesDir)
    {
        FileInfo[] files;
        try { files = new DirectoryInfo(dir).GetFiles("*" + rules.SaveExtension); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { yield break; }

        var realDir = EmuDeckRoots.RealPath(dir);
        foreach (var file in files)
        {
            // Windows matches "*.sav" against ".savx" too; the extension is compared exactly, ignoring case.
            if (!string.Equals(file.Extension, rules.SaveExtension, StringComparison.OrdinalIgnoreCase)) continue;
            // A link is never archived (SaveArchive skips them), so a linked save could never sync.
            if (file.LinkTarget is not null || file.Length == 0) continue;
            var romBase = Path.GetFileNameWithoutExtension(file.Name);
            // Both are legal in a Linux file name: '*' is the glob wildcard, so the name cannot be scoped,
            // and the server refuses ':' in an include (it reads as a Windows drive) — which would fail
            // the whole enrollment batch, not just this game.
            if (romBase.Length == 0 || romBase.Contains('*') || romBase.Contains(':')) continue;
            // A folder of ROMs (saves beside the ROM) holds other emulators' saves too: only this one's ROMs count.
            if (setup.RomExtensions is { Count: > 0 } romExts && !romExts.Any(e => File.Exists(Path.Combine(dir, romBase + e))))
                continue;

            var system = rules.System ?? systems!.GetValueOrDefault(romBase);
            DeclaredSavePath[]? extras = null;
            if (rules.StateGlobs is { } stateGlobs && setup.States is { } statesRoot)
            {
                var states = statesDir?.Invoke(statesRoot, core, romBase) ?? statesRoot;
                extras = new[] { new DeclaredSavePath(StatesKey, EmuDeckRoots.RealPath(states), stateGlobs(romBase)) };
            }

            yield return (new ScanCandidate(
                Name: RomNames.TitleFor(romBase, system, gamelists, rules.KnownTitle?.Invoke(romBase)),
                SuggestedSaveDir: realDir,
                Source: ScanSource.Emulator,
                HasSteamCloud: false,
                EmulatorName: rules.EmulatorName,
                EmulatorSystem: system,
                EmulatorCore: core,
                EmulatorRom: romBase,
                ViaEmuDeck: setup.EmuDeck,
                // The file's own extension, so the scope reads as the file it keeps (matching ignores case anyway).
                IncludeGlobs: rules.SaveGlobs(romBase, file.Extension),
                ExtraSaveDirs: extras,
                UntouchedSeed: rules.IsSeed?.Invoke(file) ?? false), file.LastWriteTimeUtc);
        }
    }

    /// <summary>
    /// ROM file name (no extension) → the <c>Emulation/roms/&lt;system&gt;</c> folder it is in. An emulator names
    /// a save after its ROM, so this is how a save learns which console it belongs to.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RomSystems(IEnumerable<string> emuDeckRoots)
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

    /// <summary>
    /// Is <paramref name="file"/> a copy EmuDeck put there at install, untouched since — byte-identical to one of
    /// <paramref name="seeds"/> (SHA-256, lower-case hex)? EmuDeck seeds some emulators' NVRAM folders with a file per
    /// common game (Model 2's <c>NVDATA</c>, Supermodel's <c>NVRAM</c>), so such a file does not mean the game was
    /// played; once the game writes its own it differs and becomes a candidate.
    /// </summary>
    public static bool IsUntouchedSeed(FileInfo file, IReadOnlySet<string> seeds)
    {
        // Every seed is under 256 KB; a bigger file is not one, and is never read just to find out.
        if (seeds.Count == 0 || file.Length > 1024 * 1024) return false;
        try
        {
            using var stream = file.OpenRead();
            return seeds.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The folder's subfolders, or none when it is missing or unreadable.</summary>
    public static string[] SafeSubdirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// <paramref name="candidates"/> without a repeated save folder (by real path), keeping the first: two
    /// config files can name the same folder, and EmuDeck's rows come first so they keep their EmuDeck tag.
    /// Folders that do not exist are dropped; a states folder that does not exist yet is kept.
    /// </summary>
    public static IReadOnlyList<RomSaveFolders> Existing(IEnumerable<RomSaveFolders> candidates)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<RomSaveFolders>();
        foreach (var c in candidates)
        {
            bool exists;
            try { exists = Directory.Exists(c.Saves); } catch { exists = false; }
            if (!exists) continue;
            var real = EmuDeckRoots.RealPath(c.Saves);
            if (seen.Add(real)) found.Add(c with { Saves = real, States = c.States is null ? null : EmuDeckRoots.RealPath(c.States) });
        }
        return found;
    }
}

/// <summary>
/// One emulator's save naming, for <see cref="RomSaves"/>. <paramref name="SaveExtension"/> finds the saves
/// (<c>.srm</c>); <paramref name="SaveGlobs"/> scopes one game's files from its ROM name and the save's own
/// extension; <paramref name="StateGlobs"/>, when the emulator has states, its state files.
/// <paramref name="System"/> is fixed for a one-console emulator, else read from <c>Emulation/roms</c>.
/// <paramref name="IsSeed"/> marks a save file the user never made (EmuDeck's preinstalled NVRAM, <see cref="ScanCandidate.UntouchedSeed"/>);
/// <paramref name="KnownTitle"/> names an arcade set from the emulator's own table.
/// </summary>
public sealed record RomSaveRules(
    string EmulatorName,
    string SaveExtension,
    Func<string, string, IReadOnlyList<string>> SaveGlobs,
    Func<string, IReadOnlyList<string>>? StateGlobs = null,
    string? System = null,
    bool PerCoreFolders = false,
    Func<FileInfo, bool>? IsSeed = null,
    Func<string, string?>? KnownTitle = null);

/// <summary>One emulator setup's folders: where it writes saves and where it writes states (which may not
/// exist yet, or be null for an emulator without any). <paramref name="EmuDeck"/>: found through EmuDeck.
/// <paramref name="RomExtensions"/>: the saves folder is a ROM folder (saves kept beside the ROM), so a save is
/// this emulator's only when a ROM of that name and one of these extensions sits beside it.</summary>
public sealed record RomSaveFolders(string Saves, string? States, bool EmuDeck = false, IReadOnlyList<string>? RomExtensions = null);
