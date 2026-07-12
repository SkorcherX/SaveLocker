namespace SaveLocker.Server.Services;

/// <summary>
/// Helpers for the save-file exclude globs. Per-game patterns are stored on the
/// <see cref="Data.Game"/> entity as newline-separated text; the global defaults come
/// from <c>Sync:DefaultExcludeGlobs</c> (or a built-in junk list). Agents receive the
/// <see cref="Effective"/> (global ∪ per-game) set and apply it when hashing/archiving.
/// </summary>
public static class GlobConfig
{
    private static readonly string[] BuiltInDefaults =
        { "*.tmp", "*.log", "*.bak", "Thumbs.db", "desktop.ini" };

    /// <summary>The global exclude defaults applied to every game.</summary>
    public static string[] GlobalDefaults(IConfiguration cfg)
    {
        var configured = cfg.GetSection("Sync:DefaultExcludeGlobs").Get<string[]>();
        var source = configured is { Length: > 0 } ? configured : BuiltInDefaults;
        return source.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    }

    /// <summary>Parse the newline-separated per-game patterns stored on the entity.</summary>
    public static string[] Parse(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    /// <summary>Join cleaned patterns back to newline-separated storage form (null if empty).</summary>
    public static string? Join(IEnumerable<string> patterns)
    {
        var cleaned = patterns.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        return cleaned.Length > 0 ? string.Join('\n', cleaned) : null;
    }

    /// <summary>Global defaults plus a game's own patterns, de-duplicated.</summary>
    public static string[] Effective(IConfiguration cfg, string? perGameRaw) =>
        GlobalDefaults(cfg)
            .Concat(Parse(perGameRaw))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
