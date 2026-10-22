using System.Buffers.Binary;

namespace SaveLocker.Agent;

/// <summary>
/// PCSX2 (PS2), tasks/emulator-saves Phase 2. A memory card is either a FILE (8–64 MB, what PCSX2 creates and what
/// EmuDeck leaves it with — the maintainer's Deck has one 64 MB <c>PS2MC-1.ps2</c>) or a FOLDER card: a folder
/// named like the card holding <see cref="MemoryCards.FolderCardSuperblock"/> and one subfolder per save,
/// <c>BASLUS-21050…</c>. Only a folder card can be split per game; a file card is listed as shared, with how to
/// convert it (<see cref="MemoryCards.Pcsx2Fix"/>).
/// <para>
/// A game is every save folder on one card carrying its product code (scope <c>BASLUS-21050*/**</c>, the save's
/// <c>_pcsx2_index</c> included). Its folder is the card itself, never the folder of cards: the card's name is each
/// machine's own (<c>PS2MC-1.ps2</c> on a Deck, <c>MC1.ps2</c> in EmuDeck for Windows), so in the scope it kept one
/// game's saves on two machines apart. Named by its serial (<see cref="ConsoleTitles"/>), with its region beside
/// the name (<see cref="ScanCandidate.Release"/>), as a game reads only its own serial's saves — else by the title the game wrote into the first one's <c>icon.sys</c>,
/// which can be just the series (both Prince of Persia sequels say "Prince of Persia"). Either is the same on
/// every machine. PCSX2's own per-game filter matches the same
/// code (<c>FolderMemoryCard::AddFolder</c>), and it indexes a save folder copied in from outside, so a pull is
/// all it takes. States are <c>SLUS-21050 (CRC).NN.p2s</c> (<c>VMManager::GetSaveStateFileName</c>); the
/// <c>.p2s.backup</c> copy PCSX2 keeps of each slot is left out (maintainer's choice, 2026-10-07: a game's
/// states would otherwise count twice towards the upload cap).
/// </para>
/// <para>
/// Where: EmuDeck sets <c>[Folders] MemoryCards</c>/<c>Savestates</c> to <c>Emulation/saves/pcsx2/{saves,states}</c>
/// on both OSes (<c>PCSX2QT_setupConfig</c>). Otherwise PCSX2's own <c>inis/PCSX2.ini</c> in its data folder:
/// <c>~/.config/PCSX2</c> (AppImage), the Flatpak's <c>…/net.pcsx2.PCSX2/config/PCSX2</c>, <c>Documents\PCSX2</c>,
/// or EmuDeck for Windows' portable <c>%APPDATA%\EmuDeck\Emulators\PCSX2-Qt</c>; default folders <c>memcards</c>
/// and <c>sstates</c> in it.
/// </para>
/// </summary>
public static class Pcsx2Saves
{
    public const string EmulatorName = "PCSX2";
    public const string System = "ps2";

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(Folders(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? DataRoots() : Array.Empty<string>()));

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<RomSaveFolders> folders)
    {
        var found = new List<(ScanCandidate Candidate, DateTime WrittenUtc)>();
        var shared = new List<ScanCandidate>();
        foreach (var setup in folders)
        {
            foreach (var entry in SafeEntries(setup.Saves))
            {
                if (entry is FileInfo file)
                {
                    if (MemoryCards.Shared(file, EmulatorName) is { } card)
                        shared.Add(MemoryCards.Candidate(card, setup.Saves, System, setup.EmuDeck));
                    continue;
                }
                if (entry is DirectoryInfo dir && dir.LinkTarget is null && MemoryCards.IsFolderCard(dir.FullName))
                    found.AddRange(GamesOn(dir, setup));
            }
        }

        // One game on two cards or in two setups: the copy most recently written is the one being played.
        return found
            .GroupBy(f => f.Candidate.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(f => f.WrittenUtc).First().Candidate)
            .Concat(shared)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<(ScanCandidate, DateTime)> GamesOn(DirectoryInfo card, RomSaveFolders setup)
    {
        DirectoryInfo[] saves;
        try { saves = card.GetDirectories(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        var games = saves
            .Where(s => s.LinkTarget is null)
            .Select(s => (Save: s, Match: MemoryCards.ConsoleSaveName().Match(s.Name)))
            // System data (BADATA-SYSTEM, BWNETCNF) carries no product code and belongs to no one game.
            .Where(x => x.Match.Success)
            .GroupBy(x => x.Match.Value, StringComparer.OrdinalIgnoreCase);
        var realDir = Path.Combine(EmuDeckRoots.RealPath(setup.Saves), card.Name);
        foreach (var game in games)
        {
            var dirs = game.Select(x => x.Save).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            if (!dirs.Any(HasFiles)) continue;
            var prefix = game.Key;
            var serial = MemoryCards.Ps1ProductCode(prefix)!;
            var title = dirs.Select(d => IconSysTitle(Path.Combine(d.FullName, "icon.sys"))).FirstOrDefault(t => t is not null);
            DeclaredSavePath[]? states = setup.States is { } s
                ? [new DeclaredSavePath(RomSaves.StatesKey, EmuDeckRoots.RealPath(s), StateGlobsFor(serial))]
                : null;
            yield return (new ScanCandidate(
                Name: ConsoleTitles.For(System, serial)?.Title ?? title ?? serial,
                SuggestedSaveDir: realDir,
                Source: ScanSource.Emulator,
                HasSteamCloud: false,
                EmulatorName: EmulatorName,
                EmulatorSystem: System,
                EmulatorRom: prefix,
                ViaEmuDeck: setup.EmuDeck,
                IncludeGlobs: [$"{prefix}*/**"],
                ExtraSaveDirs: states,
                Release: ConsoleTitles.Release(System, [serial])), dirs.Max(LastWrite));
        }
    }

    /// <summary>One game's states: every slot and the resume state, not the <c>.p2s.backup</c> copies.</summary>
    public static IReadOnlyList<string> StateGlobsFor(string serial) => [$"{serial} (*.p2s"];

    /// <summary>
    /// The title a PS2 game wrote into its save's <c>icon.sys</c> (<c>PS2D</c>, 964 bytes): 68 bytes of Shift-JIS at
    /// 0xC0, usually full-width, split into two lines at the byte offset stored at 0x06 — the first is the game,
    /// the second what the save is. Null when there is none to read.
    /// </summary>
    public static string? IconSysTitle(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 0x104 || bytes[0] != 'P' || bytes[1] != 'S' || bytes[2] != '2' || bytes[3] != 'D') return null;
            var title = bytes.AsSpan(0xC0, 68);
            var lineBreak = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
            var first = lineBreak is > 0 and < 68 ? title[..lineBreak] : title;
            var text = ConsoleText.ShiftJis(first);
            if (text.Length == 0 && lineBreak is > 0 and < 68) text = ConsoleText.ShiftJis(title[lineBreak..]);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every PCSX2 setup's (memory cards, states) folders that exist, EmuDeck's first.</summary>
    public static IReadOnlyList<RomSaveFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<string> dataRoots)
    {
        var candidates = new List<RomSaveFolders>();
        foreach (var root in emuDeckRoots)
            candidates.Add(new RomSaveFolders(Path.Combine(root, "saves", "pcsx2", "saves"),
                Path.Combine(root, "saves", "pcsx2", "states"), EmuDeck: true));
        foreach (var data in dataRoots)
        {
            var ini = EmulatorPaths.ReadIni(Path.Combine(data, "inis", "PCSX2.ini"));
            if (ini.Count == 0) continue;
            var folders = ini.GetValueOrDefault("Folders");
            candidates.Add(new RomSaveFolders(
                EmulatorPaths.IniFolder(folders?.GetValueOrDefault("MemoryCards"), data) ?? Path.Combine(data, "memcards"),
                EmulatorPaths.IniFolder(folders?.GetValueOrDefault("Savestates"), data) ?? Path.Combine(data, "sstates")));
        }
        return RomSaves.Existing(candidates);
    }

    /// <summary>The folders a standalone PCSX2 keeps <c>inis/PCSX2.ini</c> in, per platform.</summary>
    public static IReadOnlyList<string> DataRoots() => OperatingSystem.IsWindows()
        ? [Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "PCSX2-Qt"), Path.Combine(EmulatorPaths.Documents, "PCSX2")]
        : [Path.Combine(EmulatorPaths.XdgConfig, "PCSX2"), Path.Combine(EmulatorPaths.Flatpak("net.pcsx2.PCSX2"), "config", "PCSX2")];

    private static IEnumerable<FileSystemInfo> SafeEntries(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFileSystemInfos() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool HasFiles(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Any(f => f.Length > 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static DateTime LastWrite(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(dir.LastWriteTimeUtc).Max(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}
