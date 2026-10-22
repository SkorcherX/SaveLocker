namespace SaveLocker.Agent.Linux;

/// <summary>
/// Locates Steam on a Linux box. There is no registry here, so we probe the known install
/// layouts: the native package, the Flatpak sandbox, and the Deck's own path.
/// </summary>
public static class SteamRoots
{
    /// <summary>
    /// Every Steam root that actually exists, de-duplicated by real path. <c>~/.steam/steam</c> and
    /// <c>~/.local/share/Steam</c> are usually symlinks to the same directory, so they are resolved
    /// before de-duplication — otherwise every shortcut is discovered two or three times.
    /// </summary>
    public static IReadOnlyList<string> Find()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new[]
        {
            Path.Combine(home, ".steam", "steam"),               // native (symlink)
            Path.Combine(home, ".steam", "root"),                // native (symlink)
            Path.Combine(home, ".local", "share", "Steam"),      // native (real)
            // Flatpak sandboxes the whole tree under the app's data dir.
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".steam", "steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<string>();

        foreach (var c in candidates)
        {
            if (!Directory.Exists(c)) continue;
            var real = RealPath(c);
            // A Steam root always has userdata/; without it we've matched an empty stub.
            if (!Directory.Exists(Path.Combine(real, "userdata"))) continue;
            if (seen.Add(real)) roots.Add(real);
        }

        return roots;
    }

    /// <summary>
    /// Extra roots the agent UI's path browser may descend into, beyond <c>$HOME</c>.
    /// <para>
    /// The Steam roots are usually inside the home directory already; <b>removable media is not</b>.
    /// A Deck's SD card mounts under <c>/run/media</c>, and a game installed there keeps its
    /// portable saves there, so without these entries the browser cannot reach a save the user can
    /// plainly see in the file manager.
    /// </para>
    /// </summary>
    public static IEnumerable<string> BrowseRoots()
    {
        foreach (var root in Find()) yield return root;

        // SteamOS mounts cards at /run/media/<user>/<label>; older images used /run/media/<label>
        // directly. Yielding the parent covers both, and lets the user pick the card by name.
        var user = Environment.UserName;
        foreach (var media in new[] { $"/run/media/{user}", "/run/media", "/media" })
            if (Directory.Exists(media)) yield return media;
    }

    /// <summary>
    /// Every Steam library root reachable from <paramref name="steamRoot"/>: the root itself plus
    /// each <c>libraryfolders.vdf</c> entry. A Deck's SD card is a library like any other, so this
    /// is what makes an installed game on the card visible at all.
    /// <para>
    /// Unlike a non-Steam shortcut's prefix (see <see cref="CompatDataPath"/>), an installed game's
    /// <c>compatdata</c> lives in the library the game is installed in — so the two must be walked
    /// together, per library.
    /// </para>
    /// </summary>
    public static IEnumerable<string> LibraryPaths(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { steamRoot };
        yield return steamRoot;

        var folders = LibraryFolders(steamRoot);
        if (folders is null) yield break;

        foreach (var lib in folders.Children)
        {
            var path = lib.String("path");
            // A library on an SD card that is not currently inserted is skipped, not fatal.
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && seen.Add(path))
                yield return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// Every library <c>libraryfolders.vdf</c> names, whether or not it currently exists —
    /// unlike <see cref="LibraryPaths"/>, which silently skips a library that is not mounted right
    /// now (an SD card pulled or swapped for another). Nothing here is wrong to skip — a scan that
    /// errored out over a missing card would be worse — but "this library is not currently visible"
    /// and "you have no such library" look identical from a scan alone, and Steam itself remembers a
    /// library across a card swap in exactly this file. <c>doctor</c> is where the difference gets
    /// said out loud: found on a real Deck (2026-08-19) with three SD-card libraries configured and
    /// only one inserted, where a genuinely Steam-installed game on an absent card had no way to
    /// explain why it was invisible.
    /// </summary>
    public static IEnumerable<string> AllConfiguredLibraryPaths(string steamRoot)
    {
        var folders = LibraryFolders(steamRoot);
        if (folders is null) yield break;

        foreach (var lib in folders.Children)
        {
            var path = lib.String("path");
            if (!string.IsNullOrEmpty(path))
                yield return path;
        }
    }

    /// <summary>
    /// Every AppID Steam's own <c>libraryfolders.vdf</c> currently believes is installed, across
    /// every configured library — mounted or not. Steam persists each library's own <c>apps</c>
    /// block (<c>{ appid: size-on-disk }</c>) in this ONE file, on the always-reachable main root,
    /// specifically so it survives that library's own media being absent (an SD card pulled or
    /// swapped) — which is exactly what makes it usable when the library it names cannot itself be
    /// read.
    /// <para>
    /// This is the one signal that can tell "genuinely installed right now, on media I cannot see"
    /// apart from "was installed once, uninstalled since, orphaned data left behind" — a MoonDeck
    /// shortcut's own resolved compatdata prefix cannot: Steam does not clean up compatdata on
    /// uninstall (see <c>Decisions.md</c> → Linux discovery), so a real prefix full of real save
    /// data proves nothing about whether the game is still installed anywhere at all. Found on a
    /// real Deck (2026-08-19): Cyberpunk 2077, genuinely installed via Steam on a card labelled
    /// "SD2" that was not currently inserted, was still named in this file's own <c>apps</c> block.
    /// </para>
    /// </summary>
    public static IReadOnlySet<string> AllKnownInstalledAppIds(string steamRoot)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var folders = LibraryFolders(steamRoot);
        if (folders is null) return ids;

        foreach (var lib in folders.Children)
        {
            var apps = lib.Object("apps");
            if (apps is null) continue;
            foreach (var appId in apps.Items.Keys)
                ids.Add(appId);
        }
        return ids;
    }

    /// <summary>The parsed <c>libraryfolders</c> object from a Steam root's own VDF, or null.</summary>
    private static SteamVdf.VdfObject? LibraryFolders(string steamRoot)
    {
        var file = new[]
        {
            Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
        }.FirstOrDefault(File.Exists);
        if (file is null) return null;

        return ParseVdf(file)?.Object("libraryfolders");
    }

    /// <summary>Read and parse a text VDF, or null if it is unreadable or malformed.</summary>
    public static SteamVdf.VdfObject? ParseVdf(string path)
    {
        try { return SteamTextVdf.Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                      or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The compatdata directory for a shortcut's AppID under a Steam root, or null.</summary>
    /// <remarks>
    /// Non-Steam shortcuts always keep their prefix in the <b>main</b> Steam root, even when the
    /// game itself lives on an SD card — so library-folder scanning is not needed here.
    /// </remarks>
    public static string? CompatDataPath(string steamRoot, string appId)
    {
        var path = Path.Combine(steamRoot, "steamapps", "compatdata", appId);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Resolve symlinks so the same Steam root is not counted twice.</summary>
    private static string RealPath(string path)
    {
        try
        {
            var resolved = Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            return Path.GetFullPath(resolved ?? path);
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }
}
