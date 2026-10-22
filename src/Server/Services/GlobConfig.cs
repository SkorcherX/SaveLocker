using SaveLocker.Shared;

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
    public static string? Join(IEnumerable<string?> patterns)
    {
        var cleaned = patterns.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).ToArray();
        return cleaned.Length > 0 ? string.Join('\n', cleaned) : null;
    }

    /// <summary>Most per-game patterns one save accepts. Every push evaluates all of them against
    /// every file, and the console's dry run does the same against a whole archive.</summary>
    public const int MaxPatterns = 100;

    /// <summary>Longest a single pattern may be (a Windows MAX_PATH, which no real save path exceeds).</summary>
    public const int MaxPatternLength = 260;

    /// <summary>
    /// Null when <paramref name="patterns"/> may be stored or previewed, else the reason they may
    /// not. Storage is newline-separated, so a control character inside a pattern would silently
    /// split it in two; an unmatchable shape would throw inside the agent's hash of the save folder.
    /// </summary>
    public static string? Validate(IEnumerable<string>? patterns, string scope = "per game")
    {
        if (patterns is null) return "A list of patterns is required.";
        var cleaned = patterns.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).ToList();
        if (cleaned.Count > MaxPatterns)
            return $"At most {MaxPatterns} exclude patterns are allowed {scope} (got {cleaned.Count}).";
        foreach (var p in cleaned)
        {
            if (p.Length > MaxPatternLength)
                return $"An exclude pattern may be at most {MaxPatternLength} characters.";
            if (p.Any(char.IsControl))
                return "Exclude patterns cannot contain control characters or line breaks.";
            if (SaveArchive.ValidateExcludeGlob(p) is { } why) return why;
        }
        return null;
    }

    /// <summary>
    /// Null when <paramref name="patterns"/> may be stored as a save folder's include scope, else why
    /// not. Same storage limits as excludes, plus one rule of its own: an include may not climb out of
    /// the save folder (<c>..</c>) or be absolute — it names files INSIDE the folder every machine maps.
    /// </summary>
    public static string? ValidateIncludes(IEnumerable<string?>? patterns)
    {
        if (patterns is null) return null;
        var cleaned = patterns.Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).ToList();
        if (cleaned.Count > MaxPatterns)
            return $"At most {MaxPatterns} include patterns are allowed per save folder (got {cleaned.Count}).";
        foreach (var p in cleaned)
        {
            if (p.Length > MaxPatternLength)
                return $"An include pattern may be at most {MaxPatternLength} characters.";
            if (p.Any(char.IsControl))
                return "Include patterns cannot contain control characters or line breaks.";
            if (p.StartsWith('/') || p.StartsWith('\\') || p.Contains(':') ||
                p.Replace('\\', '/').Split('/').Contains(".."))
                return $"Include pattern '{p}' must be a path inside the save folder.";
            if (SaveArchive.ValidateIncludeGlob(p) is { } why) return why;
        }
        return null;
    }

    /// <summary>
    /// Null when <paramref name="paths"/> may be stored as a game's extra save folders, else why not:
    /// each key a valid, unique slug (<see cref="SaveRoot.ValidateExtraKey"/>) and each scope valid.
    /// </summary>
    public static string? ValidateExtraPaths(IEnumerable<SavePathDto>? paths)
    {
        if (paths is null) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paths)
        {
            if (p is null) return "A save folder entry is empty.";
            // Judged as stored: the key is trimmed before it is saved, so two spellings that trim alike
            // are one key.
            var key = p.Key?.Trim();
            if (SaveRoot.ValidateExtraKey(key) is { } why) return why;
            if (!keys.Add(key!)) return $"Save folder key '{key}' is used twice.";
            if (p.Label is { Length: > 100 }) return "A save folder label may be at most 100 characters.";
            if (p.Template is { Length: > MaxPatternLength }) return $"A save folder template may be at most {MaxPatternLength} characters.";
            if (ValidateIncludes(p.IncludeGlobs) is { } inc) return inc;
        }
        return keys.Count > MaxExtraPaths ? $"At most {MaxExtraPaths} extra save folders are allowed per game." : null;
    }

    /// <summary>Most extra save folders one game may have — every push walks all of them.</summary>
    public const int MaxExtraPaths = 16;

    /// <summary>Global defaults plus a game's own patterns, de-duplicated.</summary>
    public static string[] Effective(IConfiguration cfg, string? perGameRaw) =>
        GlobalDefaults(cfg)
            .Concat(Parse(perGameRaw))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
