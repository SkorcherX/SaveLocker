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
