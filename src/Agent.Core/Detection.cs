using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Save-location detection: manages the cached Ludusavi manifest and resolves
/// a game's concrete save directories on this machine.
///
/// The resolver is injectable because it is not a per-machine constant everywhere: under Proton
/// the manifest's Windows tokens resolve *inside a game's Wine prefix*, so the map is per-game.
/// Pass <see cref="PathResolver.Proton"/> for a Proton game; the default is the host's own.
/// </summary>
public sealed class Detection
{
    private readonly AgentConfig _config;
    private ManifestLoader? _manifest;

    public Detection(AgentConfig config) => _config = config;

    /// <summary>Load the manifest from cache, downloading it if missing or if forced.</summary>
    public async Task<ManifestLoader> GetManifestAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (_manifest is not null && !forceRefresh) return _manifest;

        if (!forceRefresh && File.Exists(_config.ManifestCachePath))
            _manifest = ManifestLoader.LoadFromFile(_config.ManifestCachePath);
        else
            _manifest = await ManifestLoader.DownloadAsync(_config.ManifestCachePath, ct: ct);

        return _manifest;
    }

    /// <summary>
    /// Find candidate save directories for a manifest game name. Empty if the game isn't in the
    /// manifest or none of its paths exist. On Linux with no <paramref name="resolver"/> this is
    /// always empty — the host's own paths are meaningless for a Windows game, and guessing would
    /// be worse than admitting we cannot resolve it.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveSaveDirectoriesAsync(
        string gameName, PathResolver? resolver = null, CancellationToken ct = default) =>
        Dirs(await ResolveSaveLocationsAsync(gameName, resolver, ct));

    /// <summary><see cref="ResolveSaveDirectoriesAsync"/> with each folder's manifest template: the first
    /// is the primary folder, the rest are only ever suggested (tasks/multiple-save-paths plan §8).</summary>
    public async Task<IReadOnlyList<ResolvedSaveLocation>> ResolveSaveLocationsAsync(
        string gameName, PathResolver? resolver = null, CancellationToken ct = default)
    {
        resolver ??= HostResolver();
        if (resolver is null) return Array.Empty<ResolvedSaveLocation>();

        var manifest = await GetManifestAsync(ct: ct);
        return manifest.ResolveSaveLocations(gameName, resolver);
    }

    private static IReadOnlyList<string> Dirs(IReadOnlyList<ResolvedSaveLocation> locations) =>
        locations.Select(l => l.Directory).ToList();

    /// <summary>
    /// Resolve a Proton game's save directories, supplying the three per-game placeholders.
    /// <para>
    /// <c>&lt;root&gt;</c> is DERIVED from the compatdata path rather than plumbed in: the prefix is
    /// always <c>{root}/steamapps/compatdata/{appid}</c>, so the library root is three levels up.
    /// That keeps every caller — scanner, poller, launch wrapper, CLI — passing only what it
    /// genuinely knows.
    /// </para>
    /// </summary>
    /// <param name="installDir">
    /// The game's install directory on the host — <c>&lt;base&gt;</c>. For a non-Steam shortcut
    /// that is its <c>StartDir</c>.
    /// </param>
    /// <remarks>
    /// No <c>&lt;storeUserId&gt;</c> is passed: PathResolver discovers it from the filesystem,
    /// because Steam exposes both a 32-bit account id and a SteamID64 and games use either.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ResolveProtonAsync(
        string gameName, string compatDataPath, string? installDir = null,
        CancellationToken ct = default) =>
        Dirs(await ResolveProtonLocationsAsync(gameName, compatDataPath, installDir, ct));

    /// <inheritdoc cref="ResolveProtonAsync"/>
    public async Task<IReadOnlyList<ResolvedSaveLocation>> ResolveProtonLocationsAsync(
        string gameName, string compatDataPath, string? installDir = null,
        CancellationToken ct = default)
    {
        var storeRoot = SteamLayout.RootFromCompatData(compatDataPath);
        return await ResolveSaveLocationsAsync(
            gameName, PathResolver.Proton(compatDataPath, installDir, storeRoot), ct);
    }

    /// <summary>
    /// Resolve a game's save directories inside an arbitrary Wine prefix — the general form of
    /// <see cref="ResolveProtonAsync"/>, for prefixes Steam did not create (Heroic).
    /// <para>
    /// Empty when <paramref name="prefixRoot"/> holds no prefix. No <c>&lt;root&gt;</c> is passed:
    /// a store library root is a Steam concept, and inventing one would expand
    /// <c>&lt;root&gt;</c> paths to somewhere real and wrong rather than leaving them unresolved.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveWineAsync(
        string gameName, string prefixRoot, string? installDir = null,
        CancellationToken ct = default) =>
        Dirs(await ResolveWineLocationsAsync(gameName, prefixRoot, installDir, ct));

    /// <inheritdoc cref="ResolveWineAsync"/>
    public async Task<IReadOnlyList<ResolvedSaveLocation>> ResolveWineLocationsAsync(
        string gameName, string prefixRoot, string? installDir = null,
        CancellationToken ct = default)
    {
        var resolver = WinePrefix.ResolverFor(prefixRoot, installDir);
        return resolver is null
            ? Array.Empty<ResolvedSaveLocation>()
            : await ResolveSaveLocationsAsync(gameName, resolver, ct);
    }

    /// <summary>
    /// Resolve a natively-installed Windows game's save directories, with the per-game placeholders.
    /// Empty on any other platform — see <see cref="HostResolver"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveWindowsAsync(
        string gameName, string? installDir = null, string? storeRoot = null,
        CancellationToken ct = default) =>
        Dirs(await ResolveWindowsLocationsAsync(gameName, installDir, storeRoot, ct));

    /// <inheritdoc cref="ResolveWindowsAsync"/>
    public async Task<IReadOnlyList<ResolvedSaveLocation>> ResolveWindowsLocationsAsync(
        string gameName, string? installDir = null, string? storeRoot = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<ResolvedSaveLocation>();
        return await ResolveSaveLocationsAsync(
            gameName, PathResolver.Windows(installDir, storeRoot), ct);
    }

    /// <summary>
    /// The resolver for games running natively on this host — Windows only. There is no Linux
    /// equivalent: a Proton game's paths live in its own prefix (per-game, so the caller must
    /// supply it), and native-Linux builds are out of scope (Decisions.md §1).
    /// </summary>
    public static PathResolver? HostResolver() =>
        OperatingSystem.IsWindows() ? PathResolver.Windows() : null;

    /// <summary>
    /// The manifest's own spelling of a game, for any spelling we can match; null if it is not in
    /// the manifest. Discovery records this as the candidate's ManifestKey, and enrollment creates
    /// the server-side game under it, so every machine names the same game the same way.
    /// </summary>
    public async Task<string?> CanonicalNameAsync(string name, CancellationToken ct = default) =>
        (await GetManifestAsync(ct: ct)).CanonicalName(name);

    /// <summary>
    /// Whether the manifest says this game already syncs through Steam Cloud; null when it is not in
    /// the manifest and we therefore have no answer.
    /// <para>
    /// Discovery reads this instead of assuming that everything installed through Steam is covered.
    /// The assumption is wrong far more often than it is right — of the manifest's 48,908 titles with
    /// a Steam id, only 14,340 have Steam Cloud — and the default Add Games view HIDES whatever is
    /// flagged, so a wrong "yes" makes a game the user owns invisible. Breath of Fire IV is the
    /// shape of it: sold on Steam, GOG-cloud only, hidden as already-covered.
    /// </para>
    /// </summary>
    public async Task<bool?> HasSteamCloudAsync(string name, CancellationToken ct = default) =>
        (await GetManifestAsync(ct: ct)).HasSteamCloud(name);

    /// <summary>Suggest manifest game names that contain the given substring.</summary>
    public async Task<IReadOnlyList<string>> SearchAsync(string term, int max = 25, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct: ct);
        return manifest.GameNames
            .Where(n => n.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }
}
