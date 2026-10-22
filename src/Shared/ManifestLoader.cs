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

    /// <summary>Any spelling we have seen → the manifest's OWN key, preserving its casing.</summary>
    private readonly Dictionary<string, string> _canonical;

    private ManifestLoader(Dictionary<string, ManifestGame> games, Dictionary<string, string> canonical)
    {
        _games = games;
        _canonical = canonical;
    }

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
        // A case-insensitive Dictionary finds the entry but will not hand back the key it stored
        // under, and the manifest's own casing is exactly what CanonicalName has to return.
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, game) in raw)
            if (games.TryAdd(name, game)) canonical[name] = name;

        return new ManifestLoader(games, canonical);
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

    /// <summary>
    /// Exact (case-insensitive) lookup, falling back to a punctuation-insensitive one.
    /// <para>
    /// Case was already handled; punctuation was not, and that is what actually breaks. A game added
    /// to Steam as a non-Steam shortcut carries whatever name the user typed — "DRAGON QUEST III HD
    /// 2D Remake" — against a manifest keyed "Dragon Quest III HD-2D Remake". One missing hyphen
    /// resolved nothing, and left the same game listed twice under two spellings because the
    /// scanners' de-dupe compares names too. Non-alphanumerics are dropped on both sides.
    /// </para>
    /// </summary>
    public bool TryGetGame(string name, out ManifestGame game)
    {
        if (_games.TryGetValue(name, out game!)) return true;

        _loose ??= BuildLooseIndex(_games);
        if (!_loose.TryGetValue(NormalizeName(name), out var entry)) return false;
        game = entry.Game;
        return true;
    }

    /// <summary>
    /// The manifest's own spelling of a game, given any spelling we can match; null if unknown.
    /// <para>
    /// This is what makes one game ONE game across machines. A game added to Steam as a non-Steam
    /// shortcut is named whatever its owner typed, and enrollment used that string as the server-side
    /// identity — so a Deck spelling it "DRAGON QUEST III HD 2D Remake" and a PC spelling it
    /// "DRAGON QUEST III HD-2D Remake" created two separate games that could never sync to each
    /// other. The server matches names case-insensitively but not past punctuation, so it cannot
    /// reconcile them either. Enrolling under the manifest's title makes both machines converge.
    /// </para>
    /// </summary>
    public string? CanonicalName(string name)
    {
        if (_canonical.TryGetValue(name, out var exact)) return exact;

        _loose ??= BuildLooseIndex(_games);
        return _loose.TryGetValue(NormalizeName(name), out var entry) ? entry.Key : null;
    }

    /// <summary>
    /// Whether the manifest says this game's saves sync through Steam Cloud. Null when the game is
    /// not in the manifest at all — which is not the same answer as "no", and callers must not
    /// collapse the two.
    /// <para>
    /// The manifest never writes <c>false</c>. A <c>cloud:</c> block lists only the stores whose
    /// PCGamingWiki "Save game cloud syncing" row is ticked, and is omitted entirely when none are —
    /// so a missing block and a block without <c>steam</c> state the same thing, and both mean no.
    /// 15,465 of 52,973 entries carry a block; 14,344 of those name Steam.
    /// </para>
    /// </summary>
    public bool? HasSteamCloud(string name) =>
        TryGetGame(name, out var game) ? game.Cloud?.Steam == true : null;

    /// <summary>
    /// Lowercase, punctuation reduced to single spaces, so spelling differs freely but WORDS do not.
    /// <para>
    /// Word boundaries have to survive. Deleting non-alphanumerics outright collapses
    /// "Dragon Quest I &amp; II HD-2D Remake" and "Dragon Quest III HD-2D Remake" onto the same key —
    /// "I &amp; II" becomes "III" — and the manifest contains both, so the wrong one won and DQ3
    /// resolved to paths that do not exist. Mapping punctuation to a space instead keeps "i ii"
    /// distinct from "iii" while still matching "HD-2D" to "HD 2D".
    /// </para>
    /// </summary>
    public static string NormalizeName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var pendingSpace = false;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else pendingSpace = true;
        }
        return sb.ToString();
    }

    // Built lazily: the great majority of lookups hit the exact index, and this walks 50k+ keys.
    private Dictionary<string, (string Key, ManifestGame Game)>? _loose;

    private static Dictionary<string, (string, ManifestGame)> BuildLooseIndex(
        Dictionary<string, ManifestGame> games)
    {
        // An AMBIGUOUS key resolves to nothing rather than to a guess. Distinct manifest entries can
        // still normalise together, and picking one by iteration order is how a game silently
        // acquires another game's save paths — the exact failure this fallback exists to prevent.
        var loose = new Dictionary<string, (string, ManifestGame)>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, game) in games)
        {
            var key = NormalizeName(name);
            if (key.Length == 0) continue;
            if (!loose.TryAdd(key, (name, game))) ambiguous.Add(key);
        }
        foreach (var key in ambiguous) loose.Remove(key);
        return loose;
    }


    /// <summary>
    /// Resolve the concrete, existing save directories for a game on this machine.
    /// Path templates with placeholders (e.g. &lt;winAppData&gt;/Celeste) are expanded
    /// and trimmed at the first wildcard so we return a directory to watch/archive.
    /// </summary>
    public IReadOnlyList<string> ResolveSaveDirectories(string gameName, PathResolver? resolver = null) =>
        ResolveSaveLocations(gameName, resolver).Select(l => l.Directory).ToList();

    /// <summary>
    /// <see cref="ResolveSaveDirectories"/>, keeping the manifest template each folder came from — what
    /// names a suggested extra save folder (tasks/multiple-save-paths plan §8). Same order, same folders.
    /// </summary>
    public IReadOnlyList<ResolvedSaveLocation> ResolveSaveLocations(string gameName, PathResolver? resolver = null)
    {
        if (!TryGetGame(gameName, out var game) || game.Files is null)
            return Array.Empty<ResolvedSaveLocation>();

        resolver ??= PathResolver.Windows();

        // Only SAVE paths, and only ones that apply to Windows.
        //
        // Every entry used to be considered, which had two consequences the caller could not see.
        // Callers take the first result, and the order was a HashSet's — so a game could be mapped
        // to its settings folder instead of its saves. DRAGON QUEST III does exactly that: its save
        // path needs <storeUserId>, its sibling "Steam/Config/WindowsNoEditor" does not, so the
        // config folder was the only one that resolved and it won. Roughly 4% of sampled games hit
        // this (tests/detection). Returning nothing is strictly better: the user is asked to pick,
        // instead of being handed a wrong path presented as certain.
        //
        // Ordering is the template order in the manifest, de-duplicated, so the first result is
        // stable across runs rather than depending on hash iteration.
        //
        // A result that IS the install directory is dropped — see PathResolver.IsInstallRoot. Those
        // arise from games that save as loose files beside their own executable, and the directory
        // that contains those files is the whole game.
        var results = new List<ResolvedSaveLocation>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var (template, entry) in game.Files)
        {
            if (!IsWindowsSave(entry)) continue;

            // ResolveToDirectories, not ResolveToDirectory: <storeUserId> fans out over the ids
            // actually present on disk, because it cannot be derived (see PathResolver).
            foreach (var dir in resolver.ResolveToDirectories(template))
            {
                if (!Directory.Exists(dir)) continue;
                if (resolver.IsInstallRoot(dir)) continue;
                var full = Path.GetFullPath(dir);
                if (seen.Add(full)) results.Add(new ResolvedSaveLocation(template, full));
            }
        }

        return results;
    }

    /// <summary>
    /// A file entry that describes a save on Windows. Absent <c>tags</c> or <c>when</c> mean
    /// "unspecified", which the manifest treats as applying — so absence must never be read as
    /// exclusion, or we would drop thousands of perfectly good entries.
    /// </summary>
    private static bool IsWindowsSave(ManifestFileEntry? entry)
    {
        var tagsOk = entry?.Tags is not { Count: > 0 } tags ||
                     tags.Contains("save", StringComparer.OrdinalIgnoreCase);

        var osOk = entry?.When is not { Count: > 0 } when ||
                   when.Any(w => string.IsNullOrEmpty(w?.Os) ||
                                 w.Os.Equals("windows", StringComparison.OrdinalIgnoreCase));

        return tagsOk && osOk;
    }

    // ----- Manifest schema (only the parts we use) -----

    public sealed class ManifestGame
    {
        // The value is nullable: "some/path:" with no tags block deserialises to null, not an
        // empty entry, and dereferencing it would take down the whole scan.
        [YamlMember(Alias = "files")]
        public Dictionary<string, ManifestFileEntry?>? Files { get; set; }

        [YamlMember(Alias = "installDir")]
        public Dictionary<string, object>? InstallDir { get; set; }

        [YamlMember(Alias = "cloud")]
        public ManifestCloud? Cloud { get; set; }
    }

    /// <summary>
    /// Storefronts that sync this game's saves themselves. Only the stores we act on are declared;
    /// the deserializer ignores the rest (gog, epic, origin, uplay).
    /// </summary>
    public sealed class ManifestCloud
    {
        [YamlMember(Alias = "steam")]
        public bool Steam { get; set; }
    }

    public sealed class ManifestFileEntry
    {
        [YamlMember(Alias = "tags")]
        public List<string>? Tags { get; set; }

        /// <summary>Applicability conditions. Only <c>os</c> is used; <c>store</c> is not, because
        /// the agent does not always know which store a given install came from.</summary>
        [YamlMember(Alias = "when")]
        public List<ManifestWhen?>? When { get; set; }
    }

    public sealed class ManifestWhen
    {
        [YamlMember(Alias = "os")]
        public string? Os { get; set; }
    }
}

/// <summary>A manifest save location that exists on this machine: the template as the manifest writes
/// it, and the folder it resolved to.</summary>
public sealed record ResolvedSaveLocation(string Template, string Directory);
