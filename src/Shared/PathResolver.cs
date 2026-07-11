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
            ["<winPublic>"] = Special(Environment.SpecialFolder.CommonDocuments),
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
