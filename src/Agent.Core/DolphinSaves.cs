using System.Buffers.Binary;
using System.Text;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Dolphin (GameCube and Wii), tasks/emulator-saves Phase 2.
/// <para>
/// <b>GameCube.</b> Dolphin's default Slot A is a GCI folder (<c>MAIN_SLOT_A</c> = <c>MemoryCardFolder</c>;
/// <c>GC/USA/Card A</c> on the maintainer's Deck): one <c>.gci</c> file per save, named
/// <c>&lt;maker&gt;-&lt;gamecode&gt;-&lt;file&gt;.gci</c> (<c>GCMemcardDirectory</c>). A game is its game ID's files in one
/// card folder (scope <c>01-GM8E-*.gci</c>), named by its game code (<see cref="ConsoleTitles"/>, its region beside
/// the name: GALE is Melee's USA disc, GALP the European), else by the comment the game wrote into the save. The raw card
/// (<c>GC/MemoryCardA.USA.raw</c>, Slot A = Memory Card) holds every game: listed as shared
/// (<see cref="MemoryCards.DolphinFix"/>).
/// </para>
/// <para>
/// <b>Wii.</b> Dolphin keeps the Wii's NAND as folders, a save in <c>Wii/title/&lt;type&gt;/&lt;id&gt;/data</c> — per game
/// already (D4 shape 2). The primary folder is <c>Wii/title</c>, scoped to <c>00010000/52334d45/data/**</c>; only a
/// title whose <c>data</c> holds the <c>banner.bin</c> every Wii save carries is a game (system titles and channels
/// have none). A disc's is named by its game ID (<see cref="ConsoleTitles"/>), else — a WiiWare title, a channel —
/// by the title in the banner (read on the Deck 2026-10-07: "Metroid Prime Trilogy", "Metroid: Other M").
/// </para>
/// <para>
/// States are <c>StateSaves/&lt;game ID&gt;.s01</c>…<c>.s10</c> (<c>R3OE01.s01</c>, 64 MB, on the Deck); the global
/// undo state <c>lastState.sav</c> is no game's.
/// </para>
/// <para>
/// Where: EmuDeck links <c>Emulation/saves/dolphin/{GC,Wii}</c> and the states folder — <c>StateSaves</c> on SteamOS,
/// <c>states</c> on Windows — to the emulator's own user folder (<c>Dolphin_setupSaves</c>, both OSes). Otherwise the
/// user folder itself: <c>~/.local/share/dolphin-emu</c>, the older <c>~/.dolphin-emu</c>, the Flatpak's
/// <c>…/org.DolphinEmu.dolphin-emu/data/dolphin-emu</c>, <c>Documents\Dolphin Emulator</c>,
/// <c>%APPDATA%\Dolphin Emulator</c>, or EmuDeck for Windows' portable <c>Emulators\Dolphin-x64\User</c>. A
/// custom GCI or NAND folder set in <c>Dolphin.ini</c> is not read.
/// </para>
/// </summary>
public static class DolphinSaves
{
    public const string EmulatorName = "Dolphin";

    /// <summary>PrimeHack (Phase 4), the Dolphin fork for Metroid Prime Trilogy: the same saves, folders and states as
    /// Dolphin under its own user folder — and its own games: a save never joins one another emulator made
    /// (<see cref="Enroller.SameEmulator(GameDto, ScanCandidate)"/>).</summary>
    public const string PrimeHackName = "PrimeHack";

    /// <summary>The Wii title types a game's save is under: discs, WiiWare/channels, disc channels.</summary>
    public static readonly IReadOnlyList<string> WiiGameTypes = ["00010000", "00010001", "00010004"];

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(Folders(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? UserDirs() : Array.Empty<(string, string)>()));

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<DolphinFolders> setups)
    {
        var found = new List<(ScanCandidate Candidate, DateTime WrittenUtc)>();
        var shared = new List<ScanCandidate>();
        foreach (var setup in setups)
        {
            if (setup.Gc is { } gc)
            {
                foreach (var dir in new[] { gc }.Concat(RomSaves.SafeSubdirs(gc)))
                    foreach (var file in SafeFiles(dir, "*.raw"))
                        if (MemoryCards.Shared(file, setup.Emulator) is { } card)
                            shared.Add(MemoryCards.Candidate(card, dir, "gc", setup.EmuDeck));
                foreach (var region in RomSaves.SafeSubdirs(gc))
                    foreach (var cardDir in RomSaves.SafeSubdirs(region).Where(d => Path.GetFileName(d).StartsWith("Card ", StringComparison.OrdinalIgnoreCase)))
                        found.AddRange(GameCubeGames(setup, cardDir));
            }
            if (setup.Wii is { } wii)
                found.AddRange(WiiGames(setup, Path.Combine(wii, "title")));
        }

        // One game in two setups of the same emulator: the copy most recently written is the one being played.
        return found
            .GroupBy(f => (f.Candidate.EmulatorName, f.Candidate.EmulatorRom))
            .Select(g => g.OrderByDescending(f => f.WrittenUtc).First().Candidate)
            .Concat(shared)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<(ScanCandidate, DateTime)> GameCubeGames(DolphinFolders setup, string cardDir)
    {
        var saves = SafeFiles(cardDir, "*.gci")
            .Where(f => f.LinkTarget is null && string.Equals(f.Extension, ".gci", StringComparison.OrdinalIgnoreCase))
            .Select(f => (File: f, Header: ReadGci(f.FullName)))
            .Where(x => x.Header is { } h && x.File.Name.StartsWith($"{h.Maker}-{h.GameCode}-", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Header!.GameId, StringComparer.OrdinalIgnoreCase);
        var realDir = EmuDeckRoots.RealPath(cardDir);
        foreach (var game in saves)
        {
            var first = game.OrderBy(x => x.File.Name, StringComparer.Ordinal).First().Header!;
            var title = game.OrderBy(x => x.File.Name, StringComparer.Ordinal).Select(x => x.Header!.Title).FirstOrDefault(t => t is not null);
            yield return (Candidate(setup, ConsoleTitles.For("gc", first.GameCode)?.Title ?? title ?? first.GameId, first.GameId, "gc", realDir,
                [$"{first.Maker}-{first.GameCode}-*.gci"], first.GameId, ConsoleTitles.Release("gc", [first.GameId])),
                game.Max(x => x.File.LastWriteTimeUtc));
        }
    }

    private static IEnumerable<(ScanCandidate, DateTime)> WiiGames(DolphinFolders setup, string titleDir)
    {
        if (!Directory.Exists(titleDir)) yield break;
        var realDir = EmuDeckRoots.RealPath(titleDir);
        foreach (var type in WiiGameTypes)
        {
            foreach (var idDir in RomSaves.SafeSubdirs(Path.Combine(titleDir, type)))
            {
                var hex = Path.GetFileName(idDir);
                var data = Path.Combine(idDir, "data");
                var banner = Path.Combine(data, "banner.bin");
                if (hex.Length != 8 || !File.Exists(banner)) continue;
                var id = WiiGameId(hex);
                var disc = type == "00010000" ? ConsoleTitles.For("wii", id) : null;
                var release = ConsoleTitles.Release("wii", [id]);
                yield return (Candidate(setup, disc?.Title ?? BannerTitle(banner) ?? id ?? hex, id ?? hex, "wii", realDir,
                    [$"{type}/{hex}/data/**"], id, disc is null ? release! with { Region = null, Languages = null, NamedBy = null } : release),
                    LastWrite(data));
            }
        }
    }

    private static ScanCandidate Candidate(DolphinFolders setup, string name, string rom, string system, string dir,
        IReadOnlyList<string> scope, string? stateId, GameReleaseDto? release) =>
        new(name, dir, ScanSource.Emulator, HasSteamCloud: false,
            Release: release,
            EmulatorName: setup.Emulator,
            EmulatorSystem: system,
            EmulatorRom: rom,
            ViaEmuDeck: setup.EmuDeck,
            IncludeGlobs: scope,
            ExtraSaveDirs: setup.States is { } states && stateId is not null
                ? [new DeclaredSavePath(RomSaves.StatesKey, EmuDeckRoots.RealPath(states), StateGlobsFor(stateId))]
                : null);

    /// <summary>
    /// One game's states: <c>GM8E01.s01</c>…<c>.s10</c>. A Wii title folder names only the four-letter game ID —
    /// the two-letter maker code its states also carry is on the disc — so a Wii game's scope is <c>R3ME*.s*</c>.
    /// </summary>
    public static IReadOnlyList<string> StateGlobsFor(string gameId) =>
        [gameId.Length == 6 ? $"{gameId}.s*" : $"{gameId}*.s*"];

    /// <summary>The four-letter game ID a Wii title folder's hex name spells (<c>52334d45</c> → <c>R3ME</c>), or null
    /// when it is not letters and digits.</summary>
    public static string? WiiGameId(string hex)
    {
        try
        {
            var id = Encoding.ASCII.GetString(Convert.FromHexString(hex));
            return id.All(char.IsAsciiLetterOrDigit) ? id : null;
        }
        catch (FormatException) { return null; }
    }

    /// <summary>The title in a Wii save's <c>banner.bin</c> (<c>WIBN</c>): 32 UTF-16BE characters at 0x20, the subtitle
    /// after it at 0x60. Null when there is none.</summary>
    public static string? BannerTitle(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[0x60];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
            if (Encoding.ASCII.GetString(head, 0, 4) != "WIBN") return null;
            var title = Encoding.BigEndianUnicode.GetString(head, 0x20, 0x40);
            var end = title.IndexOf('\0');
            var text = ConsoleText.Tidy(end >= 0 ? title[..end] : title);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A GameCube save's header: its game code and maker (<c>GM8E</c> + <c>01</c>) and the game's name from its
    /// comment block.</summary>
    public sealed record GciHeader(string GameCode, string Maker, string? Title)
    {
        public string GameId => GameCode + Maker;
    }

    /// <summary>
    /// A <c>.gci</c> file's 64-byte directory entry (<c>DEntry</c>): game code at 0, maker code at 4, the comment's
    /// offset into the save data at 0x3C (big-endian); the comment is the game's name, 32 bytes, then 32 more on what
    /// the save is. Shift-JIS for a Japanese game (code ending in <c>J</c>), else Windows-1252. Null when the file
    /// is not a GCI save.
    /// </summary>
    public static GciHeader? ReadGci(string path)
    {
        try
        {
            // The header and the comment only: a save can be thousands of 8 KB blocks, and a scan reads every one.
            using var stream = File.OpenRead(path);
            if (stream.Length < 0x40 + 0x2000) return null;
            var entry = new byte[0x40];
            stream.ReadExactly(entry);
            var code = Encoding.ASCII.GetString(entry, 0, 4);
            var maker = Encoding.ASCII.GetString(entry, 4, 2);
            if (!code.All(char.IsAsciiLetterOrDigit) || !maker.All(char.IsAsciiLetterOrDigit)) return null;
            string? title = null;
            var comments = BinaryPrimitives.ReadUInt32BigEndian(entry.AsSpan(0x3C));
            if (comments != uint.MaxValue && 0x40L + comments + 32 <= stream.Length)
            {
                var raw = new byte[32];
                stream.Position = 0x40L + comments;
                stream.ReadExactly(raw);
                var text = code[3] == 'J' ? ConsoleText.ShiftJis(raw) : ConsoleText.Latin(raw);
                title = text.Length == 0 ? null : text;
            }
            return new GciHeader(code, maker, title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every setup's GameCube, Wii and states folders that exist, EmuDeck's first. <paramref name="userDirs"/>:
    /// (emulator, its user folder) pairs.</summary>
    public static IReadOnlyList<DolphinFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<(string Emulator, string UserDir)> userDirs)
    {
        var candidates = new List<DolphinFolders>();
        foreach (var root in emuDeckRoots)
            foreach (var (emulator, folder) in EmuDeckFolders)
            {
                var saves = Path.Combine(root, "saves", folder);
                // The same folder under two names: StateSaves on SteamOS, states on Windows.
                var states = new[] { "StateSaves", "states" }.Select(n => Path.Combine(saves, n)).FirstOrDefault(Directory.Exists)
                             ?? Path.Combine(saves, OperatingSystem.IsWindows() ? "states" : "StateSaves");
                candidates.Add(new DolphinFolders(emulator, Path.Combine(saves, "GC"), Path.Combine(saves, "Wii"), states, EmuDeck: true));
            }
        foreach (var (emulator, user) in userDirs)
            candidates.Add(new DolphinFolders(emulator, Path.Combine(user, "GC"), Path.Combine(user, "Wii"), Path.Combine(user, "StateSaves")));

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<DolphinFolders>();
        foreach (var c in candidates)
        {
            var gc = Existing(c.Gc);
            var wii = Existing(c.Wii);
            if (gc is null && wii is null) continue;
            if (!seen.Add(c.Emulator + "\0" + (gc ?? "") + "\0" + (wii ?? ""))) continue;
            found.Add(c with { Gc = gc, Wii = wii, States = c.States is null ? null : EmuDeckRoots.RealPath(c.States) });
        }
        return found;
    }

    /// <summary>EmuDeck's <c>Emulation/saves/&lt;folder&gt;</c> per emulator.</summary>
    private static readonly (string Emulator, string Folder)[] EmuDeckFolders = [(EmulatorName, "dolphin"), (PrimeHackName, "primehack")];

    /// <summary>The user folders a standalone install keeps <c>GC</c>, <c>Wii</c> and <c>StateSaves</c> in.</summary>
    public static IReadOnlyList<(string Emulator, string UserDir)> UserDirs() => OperatingSystem.IsWindows()
        ?
        [
            (EmulatorName, Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "Dolphin-x64", "User")),
            (EmulatorName, Path.Combine(EmulatorPaths.Documents, "Dolphin Emulator")),
            (EmulatorName, Path.Combine(EmulatorPaths.AppData, "Dolphin Emulator")),
            (PrimeHackName, Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "primehack", "User")),
        ]
        :
        [
            (EmulatorName, Path.Combine(EmulatorPaths.XdgData, "dolphin-emu")),
            (EmulatorName, Path.Combine(EmulatorPaths.Home, ".dolphin-emu")),
            (EmulatorName, Path.Combine(EmulatorPaths.Flatpak("org.DolphinEmu.dolphin-emu"), "data", "dolphin-emu")),
            (PrimeHackName, Path.Combine(EmulatorPaths.Flatpak("io.github.shiiion.primehack"), "data", "dolphin-emu")),
        ];

    private static string? Existing(string? dir)
    {
        try { return dir is not null && Directory.Exists(dir) ? EmuDeckRoots.RealPath(dir) : null; }
        catch { return null; }
    }

    private static FileInfo[] SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles(pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static DateTime LastWrite(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty().Max(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}

/// <summary>One Dolphin-family setup: which emulator, and its GameCube, Wii and states folders (any may be missing).</summary>
public sealed record DolphinFolders(string Emulator, string? Gc, string? Wii, string? States, bool EmuDeck = false);
