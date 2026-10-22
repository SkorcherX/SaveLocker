using System.Text.RegularExpressions;

namespace SaveLocker.Agent;

/// <summary>
/// A human title from a ROM or save file name. Emulator games have no store, no manifest entry and
/// usually no library file a scan can rely on, so the file name IS the identity — and it is the
/// server-side game name every machine matches on. That makes determinism the requirement, not
/// prettiness: the same file name must produce the same title on every machine, with no lookup that
/// one machine has and another lacks.
/// </summary>
public static partial class RomNames
{
    /// <summary>
    /// <c>"Chrono Trigger (USA) (Rev 1) [!]"</c> → <c>"Chrono Trigger"</c>: every parenthesised or
    /// bracketed tag dropped (No-Intro/Redump region, revision and dump-status tags), whitespace
    /// collapsed. A name that is nothing but tags is returned unchanged rather than emptied.
    /// </summary>
    public static string CleanTitle(string fileBaseName)
    {
        var cleaned = Whitespace().Replace(Tags().Replace(fileBaseName, " "), " ").Trim();
        return cleaned.Length == 0 ? fileBaseName.Trim() : cleaned;
    }

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
