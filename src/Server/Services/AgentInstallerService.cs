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
    private const string InfoFileName = "installer-info.json";

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public AgentInstallerService(IConfiguration cfg)
    {
        _root = cfg["Storage:AgentInstallerRoot"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "agent-installer");
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

    /// <summary>Returns the on-disk path to the hosted installer, or null if none is present.</summary>
    public string? GetInstallerPath()
    {
        var info = GetInfo();
        if (info is null) return null;
        var path = Path.Combine(_root, info.FileName);
        return File.Exists(path) ? path : null;
    }
}
