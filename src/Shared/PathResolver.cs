namespace SaveLocker.Shared;

/// <summary>
/// Expands Ludusavi manifest path placeholders (e.g. &lt;winAppData&gt;) into
/// concrete filesystem paths for the current machine, then trims the result at
/// the first wildcard segment so callers get a real directory to watch/archive.
/// </summary>
public sealed class PathResolver
{
    private readonly Dictionary<string, string> _tokens;

    public PathResolver(Dictionary<string, string> tokens) => _tokens = tokens;

    /// <summary>Build a resolver populated with this Windows user's known folders.</summary>
    public static PathResolver Windows()
    {
        string Env(string var) => Environment.GetEnvironmentVariable(var) ?? string.Empty;
        string Special(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        var appData = Special(Environment.SpecialFolder.ApplicationData);            // Roaming
        var localAppData = Special(Environment.SpecialFolder.LocalApplicationData);  // Local
        var localLow = Path.Combine(Env("USERPROFILE"), "AppData", "LocalLow");
        var documents = Special(Environment.SpecialFolder.MyDocuments);
        var home = Env("USERPROFILE");

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["<winAppData>"] = appData,
            ["<winLocalAppData>"] = localAppData,
            ["<winLocalAppDataLow>"] = localLow,
            ["<winDocuments>"] = documents,
            // %PUBLIC% — C:\Users\Public, the profile ROOT, not its Documents folder.
            //
            // This was SpecialFolder.CommonDocuments (C:\Users\Public\Documents), one level too
            // deep. Real manifest entries look like "<winPublic>/Documents/City Interactive/…", so
            // that produced "C:\Users\Public\Documents\Documents\…" — a path that does not exist,
            // silently costing detection for all 44 <winPublic> games on Windows.
            //
            // The Proton map below already had it right (drive_c/users/Public), so Windows and a
            // Deck disagreed by exactly one segment about where the same game's saves live. That is
            // the cross-machine root divergence that makes a restore nest a folder under itself and
            // delete the correctly-placed copy — arrived at with no user error at all.
            ["<winPublic>"] = Env("PUBLIC") is { Length: > 0 } pub
                ? pub
                : Path.GetDirectoryName(Special(Environment.SpecialFolder.CommonDocuments)) ?? "",
            ["<winProgramData>"] = Special(Environment.SpecialFolder.CommonApplicationData),
            ["<winDir>"] = Env("WINDIR"),
            ["<home>"] = home,
            ["<osUserName>"] = Env("USERNAME"),
            // Windows "Saved Games" has no SpecialFolder enum value.
            ["<winSavedGames>"] = Path.Combine(home, "Saved Games"),
        };

        return new PathResolver(tokens);
    }

    /// <summary>
    /// Build a resolver for a game running under Proton. The same Windows tokens resolve, but
    /// <b>inside the Wine prefix</b> rather than against this machine's folders — so unlike
    /// <see cref="Windows"/> this map is per-game, not per-machine.
    /// </summary>
    /// <param name="compatDataPath">
    /// The game's <c>compatdata/&lt;appid&gt;</c> directory — exactly what Steam passes as
    /// <c>STEAM_COMPAT_DATA_PATH</c>. The prefix is the <c>pfx</c> subdirectory of it.
    /// </param>
    public static PathResolver Proton(string compatDataPath)
    {
        // Proton runs every game as the fixed Wine user "steamuser".
        const string user = "steamuser";
        var driveC = Path.Combine(compatDataPath, "pfx", "drive_c");
        var userHome = Path.Combine(driveC, "users", user);
        var appData = Path.Combine(userHome, "AppData");

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["<winAppData>"] = Path.Combine(appData, "Roaming"),
            ["<winLocalAppData>"] = Path.Combine(appData, "Local"),
            ["<winLocalAppDataLow>"] = Path.Combine(appData, "LocalLow"),
            ["<winDocuments>"] = Path.Combine(userHome, "Documents"),
            ["<winPublic>"] = Path.Combine(driveC, "users", "Public"),
            ["<winProgramData>"] = Path.Combine(driveC, "ProgramData"),
            ["<winDir>"] = Path.Combine(driveC, "windows"),
            ["<home>"] = userHome,
            ["<osUserName>"] = user,
            ["<winSavedGames>"] = Path.Combine(userHome, "Saved Games"),
        };

        return new PathResolver(tokens);
    }

    /// <summary>
    /// Expand placeholders in a template and return the deepest directory prefix
    /// that precedes any wildcard. Returns null if a required token is unknown.
    /// </summary>
    public string? ResolveToDirectory(string template)
    {
        var expanded = template;
        foreach (var (token, value) in _tokens)
        {
            if (expanded.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(value)) return null; // unresolved on this machine
                expanded = expanded.Replace(token, value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Any leftover <...> placeholder means we can't resolve it here.
        if (expanded.Contains('<') && expanded.Contains('>'))
            return null;

        expanded = expanded.Replace('/', Path.DirectorySeparatorChar);

        // Trim at the first wildcard segment so we return a concrete directory.
        var segments = expanded.Split(Path.DirectorySeparatorChar);
        var kept = new List<string>();
        foreach (var seg in segments)
        {
            if (seg.Contains('*') || seg.Contains('?'))
                break;
            kept.Add(seg);
        }

        if (kept.Count == 0) return null;
        var path = string.Join(Path.DirectorySeparatorChar, kept);

        // If the template pointed at a specific file (last kept segment has an
        // extension and there were no wildcards after it), use its directory.
        if (kept.Count == segments.Length && Path.HasExtension(path) && !Directory.Exists(path))
            path = Path.GetDirectoryName(path) ?? path;

        return path;
    }
}
