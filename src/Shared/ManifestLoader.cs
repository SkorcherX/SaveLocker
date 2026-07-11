using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SaveLocker.Shared;

/// <summary>
/// Loads and queries the community Ludusavi manifest
/// (https://github.com/mtkennerly/ludusavi-manifest), which maps thousands of
/// games to their save-data locations. We use it as the data source for save
/// detection rather than maintaining our own database.
/// </summary>
public sealed class ManifestLoader
{
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";

    private readonly Dictionary<string, ManifestGame> _games;

    private ManifestLoader(Dictionary<string, ManifestGame> games) => _games = games;

    public int GameCount => _games.Count;

    public IEnumerable<string> GameNames => _games.Keys;

    /// <summary>Parse a manifest from YAML text.</summary>
    public static ManifestLoader Parse(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, ManifestGame>>(yaml)
                  ?? new Dictionary<string, ManifestGame>();

        // Case-insensitive lookup by game name. The community manifest contains
        // entries that differ only in case (e.g. "Afterlife" vs "afterlife"), so
        // add one-by-one and keep the first — the ctor overload would throw.
        var games = new Dictionary<string, ManifestGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, game) in raw)
            games.TryAdd(name, game);
        return new ManifestLoader(games);
    }

    /// <summary>Load from a local cached manifest file.</summary>
    public static ManifestLoader LoadFromFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Download the manifest and cache it to <paramref name="cachePath"/>.</summary>
    public static async Task<ManifestLoader> DownloadAsync(
        string cachePath,
        string url = DefaultManifestUrl,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        var client = http ?? new HttpClient();
        var yaml = await client.GetStringAsync(url, ct);
        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(cachePath, yaml, ct);
        return Parse(yaml);
    }

    public bool TryGetGame(string name, out ManifestGame game) =>
        _games.TryGetValue(name, out game!);

    /// <summary>
    /// Resolve the concrete, existing save directories for a game on this machine.
    /// Path templates with placeholders (e.g. &lt;winAppData&gt;/Celeste) are expanded
    /// and trimmed at the first wildcard so we return a directory to watch/archive.
    /// </summary>
    public IReadOnlyList<string> ResolveSaveDirectories(string gameName, PathResolver? resolver = null)
    {
        if (!TryGetGame(gameName, out var game) || game.Files is null)
            return Array.Empty<string>();

        resolver ??= PathResolver.Windows();
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in game.Files.Keys)
        {
            var dir = resolver.ResolveToDirectory(template);
            if (dir is not null && Directory.Exists(dir))
                results.Add(Path.GetFullPath(dir));
        }

        return results.ToList();
    }

    // ----- Manifest schema (only the parts we use) -----

    public sealed class ManifestGame
    {
        [YamlMember(Alias = "files")]
        public Dictionary<string, ManifestFileEntry>? Files { get; set; }

        [YamlMember(Alias = "installDir")]
        public Dictionary<string, object>? InstallDir { get; set; }
    }

    public sealed class ManifestFileEntry
    {
        [YamlMember(Alias = "tags")]
        public List<string>? Tags { get; set; }
    }
}
