using System.Buffers.Binary;
using System.Text;

namespace SaveLocker.Agent;

/// <summary>
/// Azahar (3DS; Citra's successor), tasks/emulator-saves Phase 15. A 3DS game saves into the emulated SD card's
/// <c>Nintendo 3DS/&lt;id0&gt;/&lt;id1&gt;/title/00040000/&lt;title ID low&gt;/data/</c> (D4 shape 2), so a game is that
/// folder's <c>data</c>, scope <c>&lt;low&gt;/data/**</c> — never <c>&lt;low&gt;/**</c>: the <c>content/</c> beside it holds a
/// CIA-installed game, gigabytes. <c>0004000e</c> (updates) and <c>0004008c</c> (DLC), both on the Deck, are no game's saves.
/// Extdata (<c>…/extdata/</c>, keyed by an ID only the game's own header names) is not synced; <c>add-path</c> covers it.
/// Old Citra folders are rows of this reader: a Citra save and an Azahar save of one game are one game (maintainer's
/// call, 2026-10-12).
/// <para>
/// States are <c>states/&lt;program ID, 16 hex, upper case&gt;.&lt;NN&gt;.cst</c>, or <c>.movie&lt;id&gt;.&lt;NN&gt;.cst</c>
/// while recording (<c>savestate.cpp</c>) → states scope <c>0004000000054000.*</c>.
/// </para>
/// <para>
/// Name: nothing in the save names the game. A ROM's NCCH header holds its program ID and product code in plain text
/// (<see cref="Ncch"/>), so a ROM on this machine — in <c>Emulation/roms/n3ds</c>, in Azahar's game folders, or the
/// installed game's own <c>content/*.app</c> — names it from No-Intro's 3DS sets (<see cref="ConsoleTitles"/>, region
/// beside the name), else by its cleaned file name; with no ROM, <c>3DS &lt;low&gt;</c>. Machine-local, which D1 makes safe.
/// </para>
/// <para>
/// Where: EmuDeck links <c>Emulation/saves/azahar/saves</c> with the SD card folder and <c>/states</c> with the states
/// folder — on the Deck <c>Emulation/storage/azahar/sdmc</c> (its <c>qt-config.ini</c> sets <c>sdmc_directory</c> there) and
/// <c>~/.local/share/azahar-emu/states</c> (AppImage); on Windows from the portable <c>%APPDATA%\EmuDeck\Emulators\azahar\user</c>,
/// whose <c>sdmc</c> and <c>states</c> are the links (both seen 2026-10-12; the Deck has three real saves). A standalone
/// install: <c>~/.local/share/azahar-emu</c> (config <c>~/.config/azahar-emu</c>), the Flatpak's, <c>%APPDATA%\Azahar</c>,
/// and Citra's <c>citra-emu</c> / <c>%APPDATA%\Citra</c>; <c>qt-config.ini</c>'s <c>sdmc_directory</c> wins when
/// <c>use_custom_storage</c> is on.
/// </para>
/// </summary>
public static class AzaharSaves
{
    public const string EmulatorName = "Azahar";
    public const string System = "n3ds";
    /// <summary>A 3DS game application's title ID high half: the folder every game's save is in.</summary>
    public const string GameTitles = "00040000";

    public static IReadOnlyList<ScanCandidate> Scan()
    {
        var roots = EmuDeckRoots.Find();
        return Scan(Folders(roots, EmulatorPaths.Standalone ? Setups() : Array.Empty<AzaharSetup>()));
    }

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<AzaharFolders> folders)
    {
        var setups = folders.ToList();
        var roms = RomIndex(setups.SelectMany(s => s.RomDirs).Distinct());
        var found = new List<(ScanCandidate, DateTime)>();
        foreach (var setup in setups)
            foreach (var titleDir in TitleDirs(setup.Sdmc))
                foreach (var game in TitleFolders.Find(titleDir, CemuSaves.TitleIdLow))
                {
                    if (!HasSaveData(game.Dirs[0])) continue;
                    var programId = GameTitles + game.Id.ToUpperInvariant();
                    var rom = roms.GetValueOrDefault(programId) ?? Installed(game.Dirs[0]);
                    var known = rom?.ProductCode is { } code ? ConsoleTitles.For(System, code) : null;
                    DeclaredSavePath[]? states = setup.States is { } s
                        ? [new DeclaredSavePath(RomSaves.StatesKey, EmuDeckRoots.RealPath(s), StateGlobsFor(programId))]
                        : null;
                    found.Add((new ScanCandidate(
                        Name: known?.Title ?? (rom?.FileName is { } file ? RomNames.CleanTitle(file) : $"3DS {game.Id.ToUpperInvariant()}"),
                        SuggestedSaveDir: EmuDeckRoots.RealPath(titleDir),
                        Source: ScanSource.Emulator,
                        HasSteamCloud: false,
                        EmulatorName: EmulatorName,
                        EmulatorSystem: System,
                        EmulatorRom: programId,
                        ViaEmuDeck: setup.EmuDeck,
                        IncludeGlobs: [$"{game.Id}/data/**"],
                        ExtraSaveDirs: states,
                        Release: ConsoleTitles.Release(System, [rom?.ProductCode, programId])), game.WrittenUtc));
                }
        return TitleFolders.Newest(found);
    }

    /// <summary>One game's states: every slot and every recording's, by the program ID Azahar names them with.</summary>
    public static IReadOnlyList<string> StateGlobsFor(string programId) => [$"{programId.ToUpperInvariant()}.*"];

    /// <summary>Every <c>Nintendo 3DS/&lt;id0&gt;/&lt;id1&gt;/title/00040000</c> folder on an SD card (Azahar's IDs are zeros,
    /// but a card copied from a console has its own).</summary>
    public static IEnumerable<string> TitleDirs(string sdmc) =>
        RomSaves.SafeSubdirs(Path.Combine(sdmc, "Nintendo 3DS"))
            .SelectMany(RomSaves.SafeSubdirs)
            .Select(id1 => Path.Combine(id1, "title", GameTitles))
            .Where(Directory.Exists)
            .Order(StringComparer.Ordinal);

    private static bool HasSaveData(DirectoryInfo game)
    {
        try
        {
            var data = new DirectoryInfo(Path.Combine(game.FullName, "data"));
            return data.Exists && data.LinkTarget is null && data.EnumerateFiles("*", SearchOption.AllDirectories).Any(f => f.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A ROM this machine has: its program ID, its product code and its file name (no extension).</summary>
    public sealed record Rom(string ProgramId, string? ProductCode, string? FileName);

    /// <summary>The ROMs in <paramref name="romDirs"/> (<c>.3ds</c>, <c>.cci</c>, <c>.cxi</c>, one level deep) by program ID.</summary>
    public static IReadOnlyDictionary<string, Rom> RomIndex(IEnumerable<string> romDirs)
    {
        var index = new Dictionary<string, Rom>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in romDirs)
        {
            IEnumerable<string> files;
            try { files = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).ToList() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is not (".3ds" or ".cci" or ".cxi")) continue;
                if (Ncch.Read(file) is { } h)
                    index.TryAdd(h.ProgramId, new Rom(h.ProgramId, h.ProductCode, Path.GetFileNameWithoutExtension(file)));
            }
        }
        return index;
    }

    /// <summary>A CIA-installed game's own header, from its first <c>content/*.app</c> — no file name to fall back on.</summary>
    private static Rom? Installed(DirectoryInfo game)
    {
        try
        {
            var app = new DirectoryInfo(Path.Combine(game.FullName, "content")) is { Exists: true } content
                ? content.EnumerateFiles("*.app").OrderBy(f => f.Name, StringComparer.Ordinal).FirstOrDefault()
                : null;
            return app is not null && Ncch.Read(app.FullName) is { } h ? new Rom(h.ProgramId, h.ProductCode, null) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Every Azahar setup's SD card folder that exists, EmuDeck's first, with its states folder and the ROM folders its
    /// games are named from: every EmuDeck root's <c>roms/n3ds</c> and <c>roms/3ds</c>, the <c>roms/n3ds</c> beside a real SD
    /// card folder under <c>Emulation/storage</c> (the Deck's links lead there), and each setup's <c>qt-config.ini</c> game folders.
    /// </summary>
    public static IReadOnlyList<AzaharFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<AzaharSetup> setups)
    {
        var rootList = emuDeckRoots.ToList();
        var romDirs = new List<string>();
        foreach (var root in rootList)
            romDirs.AddRange([Path.Combine(root, "roms", "n3ds"), Path.Combine(root, "roms", "3ds")]);

        var candidates = new List<AzaharFolders>();
        foreach (var root in rootList)
            candidates.Add(new AzaharFolders(Path.Combine(root, "saves", "azahar", "saves"),
                Path.Combine(root, "saves", "azahar", "states"), [], EmuDeck: true));
        foreach (var s in setups)
        {
            var ini = EmulatorPaths.ReadIni(Path.Combine(s.ConfigDir, "qt-config.ini"));
            string? Value(string key) => ini.Values.Select(k => k.GetValueOrDefault(key)).FirstOrDefault(v => v is not null);
            var custom = string.Equals(Value("use_custom_storage")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            var sdmc = (custom ? EmulatorPaths.IniFolder(Value("sdmc_directory"), s.DataDir) : null)?.TrimEnd('/', '\\') ?? Path.Combine(s.DataDir, "sdmc");
            var gameDirs = ini.Values.SelectMany(k => k)
                .Where(kv => kv.Key.StartsWith(@"Paths\gamedirs\", StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith(@"\path", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value.Trim())
                .Where(v => v is { Length: > 0 } and not ("INSTALLED" or "SYSTEM"))
                .ToList();
            candidates.Add(new AzaharFolders(sdmc, Path.Combine(s.DataDir, "states"), gameDirs));
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<AzaharFolders>();
        foreach (var c in candidates)
        {
            bool exists;
            try { exists = Directory.Exists(c.Sdmc); } catch { exists = false; }
            var real = EmuDeckRoots.RealPath(c.Sdmc);
            if (!exists || !seen.Add(real)) continue;
            romDirs.AddRange(c.RomDirs);
            if (StorageRoot(real) is { } emulation) romDirs.Add(Path.Combine(emulation, "roms", "n3ds"));
            found.Add(c with { Sdmc = real, States = c.States is null ? null : EmuDeckRoots.RealPath(c.States) });
        }
        var allRoms = romDirs.Distinct().ToList();
        return found.Select(f => f with { RomDirs = allRoms }).ToList();
    }

    /// <summary>The <c>Emulation</c> folder of an SD card folder at <c>Emulation/storage/azahar/sdmc</c>, else null.</summary>
    private static string? StorageRoot(string sdmc)
    {
        var azahar = Path.GetDirectoryName(sdmc.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var storage = Path.GetDirectoryName(azahar);
        return Path.GetFileName(storage) == "storage" ? Path.GetDirectoryName(storage) : null;
    }

    /// <summary>Where a standalone Azahar or Citra keeps <c>qt-config.ini</c> and its data (<c>sdmc</c>, <c>states</c>), per platform.</summary>
    public static IReadOnlyList<AzaharSetup> Setups()
    {
        if (OperatingSystem.IsWindows())
        {
            var portable = Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "azahar", "user");
            AzaharSetup Roaming(string name) =>
                new(Path.Combine(EmulatorPaths.AppData, name, "config"), Path.Combine(EmulatorPaths.AppData, name));
            return [new AzaharSetup(Path.Combine(portable, "config"), portable), Roaming("Azahar"), Roaming("Citra")];
        }
        AzaharSetup Xdg(string name) =>
            new(Path.Combine(EmulatorPaths.XdgConfig, name), Path.Combine(EmulatorPaths.XdgData, name));
        AzaharSetup Flatpak(string app, string name) =>
            new(Path.Combine(EmulatorPaths.Flatpak(app), "config", name), Path.Combine(EmulatorPaths.Flatpak(app), "data", name));
        return
        [
            Xdg("azahar-emu"),
            Flatpak("io.github.azahar.Azahar", "azahar-emu"),
            Xdg("citra-emu"),
            Flatpak("org.citra_emu.citra", "citra-emu"),
        ];
    }
}

/// <summary>One Azahar setup: its SD card folder, its states folder, and the ROM folders its games are named from.</summary>
public sealed record AzaharFolders(string Sdmc, string? States, IReadOnlyList<string> RomDirs, bool EmuDeck = false);

/// <summary>A standalone Azahar or Citra: the folder its <c>qt-config.ini</c> is in, and its data folder.</summary>
public sealed record AzaharSetup(string ConfigDir, string DataDir);

/// <summary>
/// The plain-text part of a 3DS ROM's NCCH header — never encrypted, even in an encrypted ROM. A <c>.3ds</c>/<c>.cci</c> is an
/// NCSD image (<c>NCSD</c> at 0x100) whose first partition's NCCH starts at the offset its partition table gives (media units
/// of 0x200 bytes, at 0x120); a <c>.cxi</c> or an installed <c>.app</c> is an NCCH from byte 0. In the NCCH, <c>NCCH</c> is at
/// 0x100, the program ID (little-endian) at 0x118 and the product code (<c>CTR-P-AREE</c>) at 0x150. Checked against the
/// Deck's ROMs, 2026-10-12 (Super Mario 3D Land: partition at 0x4000, program ID <c>0004000000054000</c>, its save folder).
/// </summary>
public static class Ncch
{
    public sealed record Header(string ProgramId, string? ProductCode);

    public static Header? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[0x200];
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
            var offset = 0L;
            if (Magic(head, "NCSD"))
            {
                offset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(0x120)) * 0x200L;
                stream.Position = offset;
                if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length) return null;
            }
            return Parse(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>An NCCH header's first 0x200 bytes → its program ID and product code, or null when it is not one.</summary>
    public static Header? Parse(ReadOnlySpan<byte> ncch)
    {
        if (ncch.Length < 0x160 || !Magic(ncch, "NCCH")) return null;
        var programId = BinaryPrimitives.ReadUInt64LittleEndian(ncch[0x118..]);
        if (programId == 0) return null;
        var code = Encoding.ASCII.GetString(ncch.Slice(0x150, 16)).TrimEnd('\0', ' ');
        return new Header(programId.ToString("X16"), code.Length > 0 && code.All(c => c is >= ' ' and <= '~') ? code : null);
    }

    private static bool Magic(ReadOnlySpan<byte> head, string magic) =>
        head.Slice(0x100, 4).SequenceEqual(Encoding.ASCII.GetBytes(magic));
}
