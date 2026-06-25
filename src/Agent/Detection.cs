using LocalGameSync.Shared;

namespace LocalGameSync.Agent;

/// <summary>
/// Save-location detection: manages the cached Ludusavi manifest and resolves
/// a game's concrete save directories on this machine.
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
    /// Find candidate save directories for a manifest game name on this machine.
    /// Empty if the game isn't in the manifest or none of its paths exist here.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveSaveDirectoriesAsync(string gameName, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct: ct);
        return manifest.ResolveSaveDirectories(gameName);
    }

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
