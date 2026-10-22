using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Xenia (Xbox 360), tasks/emulator-saves Phase 5b. Xenia keeps every content package in its <c>content</c> folder, one per
/// title ID, by type: <c>00000001</c> saved games, <c>00000002</c> DLC, <c>000B0000</c> title updates, and a
/// <c>Headers/&lt;type&gt;/&lt;package&gt;.header</c> beside each. A game's saves are its <c>00000001</c> packages and their
/// headers, so its scope is <c>&lt;title ID&gt;/00000001/**</c> and <c>&lt;title ID&gt;/Headers/00000001/**</c> (D4 shape 2) — never
/// the DLC and updates beside them. Xenia has no states to sync (D3).
/// <para>
/// Two layouts, both seen on the Deck 2026-10-12 (and the newer one on the maintainer's PC, with a real Forza Horizon save):
/// Xenia Canary keeps each profile's saves in <c>content/&lt;profile XUID&gt;/&lt;title ID&gt;/</c>, so the game's folder is the
/// profile's; older builds wrote <c>content/&lt;title ID&gt;/</c>, and the folder is <c>content</c> itself. The scope is the same
/// in both, so a game saved on either joins one server game (D1), whatever the XUID. A profile's save wins over an old-layout
/// one of the same game; a profile named by its folder (<c>content/User/&lt;title ID&gt;/</c>, Canary builds of 2023) is read too.
/// <c>FFFE07D1</c> is the dashboard's profile data, no game's save.
/// </para>
/// <para>
/// <c>content/0000000000000000/</c> holds content no profile owns: DLC, and saves some games make for the device rather than
/// a player (Rock Band's calibration, Bullet Soul's options and high scores — xenia-canary#743). Every Xenia game declares it as
/// a second folder, key <see cref="DeviceKey"/>, scoped like its profile folder, so those saves sync and DLC never does; a game
/// with only such saves is listed too.
/// </para>
/// <para>
/// Name: a save's header names the save ("MaroLFC Car Setup Files"), not the game, so the name comes from the title ID
/// (<see cref="ConsoleTitles"/>: Redump's disc serials — <c>MS-2505</c> is title ID <c>4D5309C9</c>, Forza Horizon — and
/// No-Intro's Arcade titles), else <c>Xbox 360 &lt;title ID&gt;</c>. An Xbox 360 title ID is every region's, so there is no region.
/// </para>
/// <para>
/// Where: EmuDeck links <c>Emulation/saves/xenia/saves</c> with Xenia's <c>content</c> — <c>Emulation/roms/xbox360/content</c>
/// on SteamOS (Xenia Canary under Proton, portable), the portable <c>%APPDATA%\EmuDeck\Emulators\xenia\content</c> on Windows,
/// where the emulator's folder is the link. A standalone Xenia: its storage root's <c>content</c>, or the config's
/// <c>content_root</c> — the exe's folder with <c>portable.txt</c>, else <c>Documents\Xenia</c>, or <c>~/.local/share/Xenia</c>
/// for a native Linux build (<c>xenia_main.cc</c>).
/// </para>
/// </summary>
public static class XeniaSaves
{
    public const string EmulatorName = "Xenia";
    public const string System = "xbox360";
    /// <summary>The content type of a saved game.</summary>
    public const string SavedGame = "00000001";

    public static IReadOnlyList<ScanCandidate> Scan() =>
        Scan(Folders(EmuDeckRoots.Find(), EmulatorPaths.Standalone ? StorageRoots() : Array.Empty<string>()));

    public static IReadOnlyList<ScanCandidate> Scan(IEnumerable<XeniaFolders> folders)
    {
        var found = new List<(ScanCandidate Candidate, DateTime Written, int Rank)>();
        foreach (var setup in folders)
        {
            // Where a profile's saves are, best first: an XUID folder (Canary), a profile named by its folder
            // (content/User, Canary builds of 2023), then content itself (older builds, no profile).
            var subdirs = RomSaves.SafeSubdirs(setup.Content).Order(StringComparer.Ordinal).ToList();
            var homes = subdirs.Where(d => Xuid(Path.GetFileName(d)) is not null).Select(d => (Dir: d, Rank: 0))
                .Concat(subdirs.Where(d => IsNamedProfile(Path.GetFileName(d))).Select(d => (Dir: d, Rank: 1)))
                .Append((Dir: setup.Content, Rank: 2))
                .ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, rank) in homes)
                foreach (var (id, written) in SavedTitles(dir))
                {
                    seen.Add(id);
                    found.Add((Candidate(setup, dir, id), written, rank));
                }
            // A game that saved only into the device's own folder (Rock Band's calibration, Bullet Soul's options) is a
            // game too. Its saves are its device folder; its profile folder is the newest profile here, declared so a
            // later profile save joins it.
            var home = homes.Where(h => h.Rank < 2).OrderByDescending(h => LastWrite(h.Dir)).Select(h => h.Dir).FirstOrDefault() ?? setup.Content;
            foreach (var (id, written) in SavedTitles(Path.Combine(setup.Content, NoProfile)))
                if (!seen.Contains(id))
                    found.Add((Candidate(setup, home, id), written, home == setup.Content ? 2 : 0));
        }
        // A game saved under a profile is that game; an old-layout copy of it is an earlier one.
        return TitleFolders.Newest(found
            .GroupBy(f => f.Candidate.EmulatorRom!, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Where(f => f.Rank == g.Min(x => x.Rank)))
            .Select(f => (f.Candidate, f.Written)));
    }

    private static ScanCandidate Candidate(XeniaFolders setup, string dir, string id) => new(
        Name: ConsoleTitles.For(System, id)?.Title ?? $"Xbox 360 {id}",
        SuggestedSaveDir: EmuDeckRoots.RealPath(dir),
        Source: ScanSource.Emulator,
        HasSteamCloud: false,
        EmulatorName: EmulatorName,
        EmulatorSystem: System,
        EmulatorRom: id,
        ViaEmuDeck: setup.EmuDeck,
        IncludeGlobs: ScopeFor(id),
        // Every Xenia game under a profile declares the device's folder, even before it exists, so every machine's game
        // has the same folders and a device save made on one reaches the others. A game in the layout from before
        // profiles does not: its folder is content itself, the device's folder would sit inside it (one folder of a game
        // never may), and those builds kept device saves with the rest.
        ExtraSaveDirs: SamePath(dir, setup.Content)
            ? []
            : [new DeclaredSavePath(DeviceKey, Path.Combine(setup.Content, NoProfile), ScopeFor(id))],
        Release: ConsoleTitles.Release(System, [id]));

    // The title IDs with a saved game in a folder of title folders, and when each was last written.
    private static IEnumerable<(string Id, DateTime Written)> SavedTitles(string dir) =>
        TitleFolders.Find(dir, TitleId)
            .Select(g => (g.Id, Written: LastSave(g.Dirs[0])))
            .Where(g => g.Written is not null)
            .Select(g => (g.Id, g.Written!.Value));

    /// <summary>The key of a Xenia game's second folder: the saves no profile owns, <c>content/0000000000000000</c>.</summary>
    public const string DeviceKey = "device";

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    /// <summary>The XUID folder of content no profile owns.</summary>
    public const string NoProfile = "0000000000000000";

    /// <summary>A profile folder named by the profile (<c>content/User</c>, Canary's multiple-profile layout of 2023,
    /// PR #118) — any folder that is neither an XUID nor a title ID.</summary>
    public static bool IsNamedProfile(string name) =>
        name.Length > 0 && !(name.All(char.IsAsciiHexDigit) && name.Length is 8 or 16);

    private static DateTime LastWrite(string dir)
    {
        try { return new DirectoryInfo(dir).LastWriteTimeUtc; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    /// <summary>One game's saves and their headers, nothing else of its content.</summary>
    public static IReadOnlyList<string> ScopeFor(string titleId) =>
        [$"{titleId}/{SavedGame}/**", $"{titleId}/Headers/{SavedGame}/**"];

    /// <summary>A folder's name as an Xbox 360 title ID (<c>4D5309C9</c>), else null — the dashboard's <c>FFFE07D1</c> and
    /// other <c>FFFE</c> system titles are no game's.</summary>
    public static string? TitleId(string name) =>
        name.Length == 8 && name.All(char.IsAsciiHexDigit) && !name.StartsWith("FFFE", StringComparison.OrdinalIgnoreCase)
            ? name.ToUpperInvariant() : null;

    /// <summary>A folder's name as a profile's XUID (16 hex digits, not all zeros: that one is content no profile owns), else null.</summary>
    public static string? Xuid(string name) =>
        name.Length == 16 && name.All(char.IsAsciiHexDigit) && name.Any(c => c != '0') ? name.ToUpperInvariant() : null;

    // When the game's newest save file was written, or null when it has no saved game with any bytes in it.
    private static DateTime? LastSave(DirectoryInfo title)
    {
        try
        {
            var saves = new DirectoryInfo(Path.Combine(title.FullName, SavedGame));
            if (!saves.Exists || saves.LinkTarget is not null) return null;
            var files = saves.EnumerateFiles("*", SearchOption.AllDirectories).Where(f => f.Length > 0).ToList();
            return files.Count == 0 ? null : files.Max(f => f.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every Xenia <c>content</c> folder that exists, EmuDeck's first. <paramref name="storageRoots"/>: standalone
    /// installs' storage roots, whose <c>xenia-canary.config.toml</c> or <c>xenia.config.toml</c> may move <c>content</c>.</summary>
    public static IReadOnlyList<XeniaFolders> Folders(IEnumerable<string> emuDeckRoots, IEnumerable<string> storageRoots)
    {
        var candidates = new List<XeniaFolders>();
        foreach (var root in emuDeckRoots)
            candidates.Add(new XeniaFolders(Path.Combine(root, "saves", "xenia", "saves"), EmuDeck: true));
        foreach (var root in storageRoots)
        {
            var configured = new[] { "xenia-canary.config.toml", "xenia.config.toml" }
                .Select(f => ContentRoot(Path.Combine(root, f)))
                .FirstOrDefault(v => v is not null);
            candidates.Add(new XeniaFolders(EmulatorPaths.IniFolder(configured, root) ?? Path.Combine(root, "content")));
        }

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var found = new List<XeniaFolders>();
        foreach (var c in candidates)
        {
            bool exists;
            try { exists = Directory.Exists(c.Content); } catch { exists = false; }
            var real = EmuDeckRoots.RealPath(c.Content);
            if (exists && seen.Add(real)) found.Add(c with { Content = real });
        }
        return found;
    }

    /// <summary>A Xenia config's <c>content_root = "…"</c>, or null when it is empty or missing.</summary>
    public static string? ContentRoot(string configToml)
    {
        try
        {
            if (!File.Exists(configToml)) return null;
            foreach (var raw in File.ReadLines(configToml))
            {
                var line = raw.Trim();
                if (!line.StartsWith("content_root", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0 || line[..eq].Trim() != "content_root") continue;
                var value = line[(eq + 1)..].Trim();
                var quote = value.Length > 0 ? value[0] : '\0';
                if (quote is not ('"' or '\'')) return null;
                var end = value.IndexOf(quote, 1);
                var path = end > 0 ? value[1..end] : null;
                return string.IsNullOrWhiteSpace(path) ? null : quote == '"' ? path.Replace(@"\\", @"\") : path;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Where a standalone Xenia keeps its storage root, per platform; EmuDeck for Windows' copy is portable.</summary>
    public static IReadOnlyList<string> StorageRoots() => OperatingSystem.IsWindows()
        ?
        [
            Path.Combine(EmulatorPaths.AppData, "EmuDeck", "Emulators", "xenia"),
            Path.Combine(EmulatorPaths.Documents, "Xenia"),
        ]
        : [Path.Combine(EmulatorPaths.XdgData, "Xenia")];
}

/// <summary>One Xenia setup's <c>content</c> folder.</summary>
public sealed record XeniaFolders(string Content, bool EmuDeck = false);
