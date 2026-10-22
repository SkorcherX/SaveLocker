using System.Collections.Frozen;
using System.IO.Compression;
using System.Text.RegularExpressions;
using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// A disc game's title from the serial its save is filed under (<c>BASLUS-21287…</c> → SLUS-21287 → "Prince of
/// Persia - The Two Thrones", USA), from Redump's dats as libretro-database ships them (CC BY-SA 4.0,
/// <c>ConsoleTitles/NOTICE.md</c>; rebuilt by <c>ConsoleTitles/Update-ConsoleTitles.ps1</c>). What a game writes
/// into its own save is no title to go by: PS2 saves carry the series on the first line of <c>icon.sys</c>, so
/// Warrior Within and The Two Thrones were both "Prince of Persia". The region is not in the title but beside it
/// (<see cref="GameReleaseDto"/>): each region's disc has its own serial and reads only its own saves, so a USA
/// and a European save are two games of the same name, told apart by their release.
/// </summary>
public static partial class ConsoleTitles
{
    private static readonly Lazy<FrozenDictionary<string, ConsoleTitle>> Table = new(Load);

    /// <summary>
    /// What Redump calls <paramref name="serial"/>'s disc on <paramref name="system"/> (<c>psx</c>, <c>ps2</c>,
    /// <c>ps3</c>, <c>psp</c>: <c>SLUS-21287</c>, <c>SLUS21287</c> or the disc's <c>SLUS_212.87</c>; <c>gc</c>, <c>wii</c>: the
    /// game ID, of which the four-letter game code counts — <c>GALE01</c>, <c>R3ME</c>), or null for one Redump has
    /// no disc of.
    /// </summary>
    public static ConsoleTitle? For(string system, string? serial) =>
        Key(system, serial) is { } key ? Table.Value.GetValueOrDefault($"{system}\t{key}") : null;

    /// <summary>
    /// The release a save of <paramref name="ids"/> (its game IDs, first the one it is named by) belongs to: the
    /// first ID Redump knows gives the region and languages. A PSP, PS3 or Vita game Redump has no disc of (a PSN
    /// title, every Vita game) takes its region from its ID's prefix (<see cref="PrefixRegion"/>). Null for a system
    /// that is no disc console's.
    /// </summary>
    public static GameReleaseDto? Release(string system, IEnumerable<string?> ids)
    {
        if (ConsoleName(system) is not { } console) return null;
        var list = ids.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var known = list.Select(id => For(system, id)).FirstOrDefault(t => t is not null);
        return new GameReleaseDto(console,
            Region: known?.Region ?? PrefixRegion(system, list.FirstOrDefault()),
            GameIds: list.Length == 0 ? null : list,
            Languages: known is { Languages.Length: > 0 } ? known.Languages : null,
            NamedBy: known is null ? null : "Redump");
    }

    /// <summary>The console's name as people say it, for the systems this table covers.</summary>
    public static string? ConsoleName(string system) => system switch
    {
        "psx" => "PlayStation",
        "ps2" => "PlayStation 2",
        "ps3" => "PlayStation 3",
        "ps4" => "PlayStation 4",
        "psp" => "PlayStation Portable",
        "psvita" => "PlayStation Vita",
        "gc" => "GameCube",
        "wii" => "Wii",
        _ => null,
    };

    /// <summary>
    /// The region a PSP, PS3 or Vita game ID's prefix names, in Redump's words: the third letter of a PSP or PS3 ID
    /// (<c>ULUS</c>, <c>BLES</c>, <c>NPJB</c>), the fourth of a Vita one (<c>PCSE</c> USA, <c>PCSB</c> Europe). Null for
    /// any other system — a PS4 <c>CUSA</c> ID is every region's — or a prefix that says none.
    /// </summary>
    public static string? PrefixRegion(string system, string? id)
    {
        if (id is not { Length: >= 4 }) return null;
        var letter = system switch
        {
            "psp" or "ps3" => char.ToUpperInvariant(id[2]),
            "psvita" when id.StartsWith("PCS", StringComparison.OrdinalIgnoreCase) => char.ToUpperInvariant(id[3]) switch
            {
                'A' or 'E' => 'U',
                'B' or 'F' => 'E',
                'C' or 'G' => 'J',
                'D' or 'H' => 'A',
                _ => '\0',
            },
            _ => '\0',
        };
        return letter switch
        {
            'U' => "USA",
            'E' => "Europe",
            'J' => "Japan",
            'A' or 'H' => "Asia",
            'K' => "Korea",
            _ => null,
        };
    }

    private static string? Key(string system, string? serial)
    {
        if (string.IsNullOrEmpty(serial)) return null;
        return system is "gc" or "wii"
            ? serial.Length >= 4 ? serial[..4].ToUpperInvariant() : null
            : SonySerial().Match(serial) is { Success: true } m ? $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value}{m.Groups[3].Value}" : null;
    }

    private static FrozenDictionary<string, ConsoleTitle> Load()
    {
        using var stream = typeof(ConsoleTitles).Assembly.GetManifestResourceStream("SaveLocker.Agent.console-titles.tsv.gz");
        if (stream is null) return FrozenDictionary<string, ConsoleTitle>.Empty;
        using var reader = new StreamReader(new GZipStream(stream, CompressionMode.Decompress));
        var titles = new Dictionary<string, ConsoleTitle>(StringComparer.Ordinal);
        // system, serial, title, region, languages
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split('\t');
            if (f.Length < 3) continue;
            titles[$"{f[0]}\t{f[1]}"] = new ConsoleTitle(f[2],
                f.Length > 3 && f[3].Length > 0 ? f[3] : null,
                f.Length > 4 && f[4].Length > 0 ? f[4].Split(',') : []);
        }
        return titles.ToFrozenDictionary(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^([A-Za-z]{4})[-_ ]?(\d{3})\.?(\d{2})")]
    private static partial Regex SonySerial();
}

/// <summary>One Redump disc: its <see cref="Title"/> without region or languages ("Prince of Persia - Warrior
/// Within"), its <see cref="Region"/> ("Europe, Australia"), null when Redump gives none, and its
/// <see cref="Languages"/> ("En", "Fr"), empty when Redump lists none.</summary>
public sealed record ConsoleTitle(string Title, string? Region, string[] Languages);
