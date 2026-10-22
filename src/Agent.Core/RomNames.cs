using System.Text.RegularExpressions;

namespace SaveLocker.Agent;

/// <summary>
/// A human title from a ROM or save file name. Emulator games have no store, no manifest entry and
/// usually no library file a scan can rely on, so the file name is the title. It is no longer the
/// identity: a save is matched to its server game by the files it names (Enroller.ServerNameFor, D1), so
/// a title only has to read well — and only the machine that enrolls a game first ever picks it.
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

    /// <summary>
    /// A save's title: the cleaned file name, except an arcade ROM's (<c>sf2</c>, <c>scud</c>), which is a set name
    /// and not a title — that takes <paramref name="known"/> (an emulator's own set table), else what ES-DE's
    /// gamelist calls it, cleaned the same way. Either may exist on one machine and not another; that is safe
    /// because an emulator game is found by its files, not its name (Enroller.ServerNameFor, D1).
    /// </summary>
    public static string TitleFor(string fileBaseName, string? system, GamelistXml gamelists, string? known = null) =>
        known is { Length: > 0 } ? known
        : gamelists.ArcadeTitle(system, fileBaseName) is { Length: > 0 } listed ? CleanTitle(listed)
        : CleanTitle(fileBaseName);

    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
