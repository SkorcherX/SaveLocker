using System.Text.RegularExpressions;

namespace SaveLocker.Agent;

/// <summary>
/// D4's shape 2 (tasks/emulator-saves): a folder per game ID inside a folder every game shares — the PSP's and PS3's
/// <c>SAVEDATA</c> (<c>ULUS10041DATA00</c>, several per game), the Vita's and PS4's <c>savedata</c> (one per game),
/// and Group E's title-ID folders. A game is every subfolder whose name starts with its ID, scoped to them alone
/// (<see cref="Scope"/>), so its archive holds exactly that game's folders and a restore never touches the others'.
/// </summary>
public static partial class TitleFolders
{
    /// <summary>One game's save folders in a shared folder, and when any of them was last written.</summary>
    public sealed record Game(string Id, IReadOnlyList<DirectoryInfo> Dirs, DateTime WrittenUtc);

    /// <summary>
    /// <paramref name="dir"/>'s subfolders by the game ID <paramref name="idOf"/> reads from each name (null: no game's,
    /// like system data). A link, and a folder with no file in it that has any bytes, is no save.
    /// </summary>
    public static IReadOnlyList<Game> Find(string dir, Func<string, string?> idOf)
    {
        DirectoryInfo[] subdirs;
        try { subdirs = Directory.Exists(dir) ? new DirectoryInfo(dir).GetDirectories() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        return subdirs
            .Where(d => d.LinkTarget is null)
            .Select(d => (Dir: d, Id: idOf(d.Name)))
            .Where(x => x.Id is not null && HasFiles(x.Dir))
            .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var dirs = g.Select(x => x.Dir).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
                return new Game(g.Key, dirs, dirs.Max(LastWrite));
            })
            .OrderBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>One game's scope: <c>ULUS10041*/**</c> where a game owns several folders named from its ID,
    /// <c>PCSE00120/**</c> where it owns the one folder named by it.</summary>
    public static IReadOnlyList<string> Scope(string id, bool severalFolders) =>
        [severalFolders ? $"{id}*/**" : $"{id}/**"];

    /// <summary>
    /// The Sony game ID a save folder's name starts with — four letters, five digits: <c>ULUS10041</c> (PSP),
    /// <c>BLUS30443</c> and <c>NPUB30010</c> (PS3), <c>PCSE00120</c> (Vita), <c>CUSA00552</c> (PS4). Every ID is nine
    /// characters, so no game's ID is the start of another's and <c>ULUS10041*</c> takes only that game's folders.
    /// </summary>
    public static string? SonyId(string name) => SonyIdPattern().Match(name) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"^[A-Za-z]{4}\d{5}")]
    private static partial Regex SonyIdPattern();

    /// <summary>One game found in two setups of the same emulator is one row: the copy most recently written is the
    /// one being played.</summary>
    public static IReadOnlyList<ScanCandidate> Newest(IEnumerable<(ScanCandidate Candidate, DateTime WrittenUtc)> found) =>
        found
            .GroupBy(f => (f.Candidate.EmulatorName, f.Candidate.EmulatorRom))
            .Select(g => g.OrderByDescending(f => f.WrittenUtc).First().Candidate)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.EmulatorRom, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool HasFiles(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Any(f => f.Length > 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static DateTime LastWrite(DirectoryInfo dir)
    {
        try { return dir.EnumerateFiles("*", SearchOption.AllDirectories).Select(f => f.LastWriteTimeUtc).DefaultIfEmpty(dir.LastWriteTimeUtc).Max(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}
