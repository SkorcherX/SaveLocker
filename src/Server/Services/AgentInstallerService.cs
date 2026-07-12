using System.Text.Json;
using SaveLocker.Shared;

namespace SaveLocker.Server.Services;

/// <summary>
/// Manages the agent installer binary stored on the server so agents can
/// self-update without requiring a separate CDN or GitHub release URL.
/// </summary>
public class AgentInstallerService
{
    private readonly string _root;
    private readonly string _githubRepo;
    private const string InfoFileName = "installer-info.json";

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public AgentInstallerService(IConfiguration cfg)
    {
        _root = cfg["Storage:AgentInstallerRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "agent-installer");
        _githubRepo = cfg["AgentUpdate:GitHubRepo"] ?? "SkorcherX/SaveLocker";
        Directory.CreateDirectory(_root);
    }

    public AgentInstallerStatus? GetInfo()
    {
        var path = Path.Combine(_root, InfoFileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AgentInstallerStatus>(File.ReadAllText(path), _json); }
        catch { return null; }
    }

    public async Task<AgentInstallerStatus> SaveAsync(
        Stream content, string version, string fileName, CancellationToken ct)
    {
        // Remove the previous installer exe before writing the new one.
        foreach (var f in Directory.GetFiles(_root, "*.exe"))
            File.Delete(f);

        var exePath = Path.Combine(_root, Path.GetFileName(fileName));
        await using (var fs = File.Create(exePath))
            await content.CopyToAsync(fs, ct);

        var info = new AgentInstallerStatus(
            version,
            Path.GetFileName(fileName),
            DateTime.UtcNow,
            new FileInfo(exePath).Length);

        await File.WriteAllTextAsync(
            Path.Combine(_root, InfoFileName),
            JsonSerializer.Serialize(info), ct);

        return info;
    }

    public void Delete()
    {
        foreach (var f in Directory.GetFiles(_root, "*.exe"))
            File.Delete(f);
        var info = Path.Combine(_root, InfoFileName);
        if (File.Exists(info)) File.Delete(info);
    }

    /// <summary>
    /// Fetches the latest release's agent installer asset from the configured GitHub repo
    /// (<c>AgentUpdate:GitHubRepo</c>) and stores it as the hosted installer — automating the
    /// otherwise-manual download-from-GitHub-then-upload step. Throws if no matching asset exists.
    /// </summary>
    public async Task<AgentInstallerStatus> FetchLatestFromGitHubAsync(
        HttpClient http, CancellationToken ct, bool onlyIfNewer = false)
    {
        using var meta = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{_githubRepo}/releases/latest");
        meta.Headers.UserAgent.ParseAdd("SaveLocker-Server");
        meta.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var metaResp = await http.SendAsync(meta, ct);
        metaResp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await metaResp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var version = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');

        string? assetName = null, assetUrl = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("SaveLocker-Agent-Setup", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                assetName = name;
                assetUrl = a.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        if (assetUrl is null || assetName is null)
            throw new InvalidOperationException(
                $"Latest release of {_githubRepo} has no SaveLocker-Agent-Setup-*.exe asset.");

        // The scheduled poll still needs to inspect release metadata, but should not
        // repeatedly download the same installer on every interval. Manual fetches
        // retain their original force-refresh behavior.
        var current = GetInfo();
        if (onlyIfNewer && current is not null && GetInstallerPath() is not null &&
            !IsNewerVersion(version, current.Version))
            return current;

        using var dl = new HttpRequestMessage(HttpMethod.Get, assetUrl);
        dl.Headers.UserAgent.ParseAdd("SaveLocker-Server");
        using var dlResp = await http.SendAsync(dl, HttpCompletionOption.ResponseHeadersRead, ct);
        dlResp.EnsureSuccessStatusCode();

        await using var stream = await dlResp.Content.ReadAsStreamAsync(ct);
        return await SaveAsync(stream, version, assetName, ct);
    }

    private static bool IsNewerVersion(string candidate, string current)
    {
        var candidateVersion = ParseVersion(candidate);
        var currentVersion = ParseVersion(current);
        if (candidateVersion is not null && currentVersion is not null)
            return candidateVersion > currentVersion;

        // Release tags are expected to be semver-like. If either value is not,
        // only an exact match is considered current so a malformed new tag can
        // still be surfaced instead of silently ignored.
        return !string.Equals(
            candidate.Trim().TrimStart('v', 'V'),
            current.Trim().TrimStart('v', 'V'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static Version? ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0) normalized = normalized[..suffix];
        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }

    /// <summary>Returns the on-disk path to the hosted installer, or null if none is present.</summary>
    public string? GetInstallerPath()
    {
        var info = GetInfo();
        if (info is null) return null;
        var path = Path.Combine(_root, info.FileName);
        return File.Exists(path) ? path : null;
    }
}
