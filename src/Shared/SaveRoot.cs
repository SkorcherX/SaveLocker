using System.Text.RegularExpressions;

namespace SaveLocker.Shared;

/// <summary>
/// One folder of a game's saves on this machine. A game has exactly one primary root
/// (<see cref="PrimaryKey"/>), whose files sit at the archive root exactly as a single-folder game's
/// always have, and any number of extra roots, each stored in the archive under its own key
/// (tasks/multiple-save-paths/plan.md §1). The key is what every machine agrees on; the folder is
/// wherever that machine keeps it, so a restore matches roots by key, never by position or path.
/// </summary>
/// <param name="IncludeGlobs">When non-empty, only the files of <paramref name="Directory"/> matching one
/// of these (relative to it, anchored at it) belong to this root — one game's saves inside a folder
/// other games share. Null or empty: the whole folder.</param>
public sealed partial record SaveRoot(string Key, string Directory, IReadOnlyList<string>? IncludeGlobs = null)
{
    public const string PrimaryKey = "main";

    public bool IsPrimary => Key == PrimaryKey;

    public bool HasIncludeScope => IncludeGlobs?.Any(g => !string.IsNullOrWhiteSpace(g)) == true;

    public static SaveRoot Primary(string directory, IEnumerable<string>? includeGlobs = null) =>
        new(PrimaryKey, directory, includeGlobs?.ToList());

    /// <summary>
    /// Null when <paramref name="key"/> can name an extra save folder, else why not. Keys become an
    /// archive path segment on every machine and are never renamed, so they are kept to a short,
    /// lower-case slug that means the same thing on every filesystem.
    /// </summary>
    public static string? ValidateExtraKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "A save folder needs a key.";
        if (key == PrimaryKey) return $"'{PrimaryKey}' is the game's primary save folder, not an extra one.";
        return KeyShape().IsMatch(key)
            ? null
            : $"Save folder key '{key}' must be 1-32 lower-case letters, digits or '-', starting with a letter or digit.";
    }

    // \z, not $: .NET's $ also matches just before a trailing newline, which would let "states\n" through.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex KeyShape();
}
