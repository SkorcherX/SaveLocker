using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalGameSync.Agent;

/// <summary>
/// Per-machine agent configuration, persisted as JSON. Default location is
/// %PROGRAMDATA%\LocalGameSync\config.json, overridable via --config for tests
/// (lets two "machines" run side by side against one server).
/// </summary>
public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "http://localhost:5179";
    public string MachineName { get; set; } = Environment.MachineName;
    public string? ApiKey { get; set; }
    public Guid? MachineId { get; set; }
    public string ManifestCachePath { get; set; } =
        Path.Combine(DefaultDir, "manifest.yaml");
    /// <summary>Set once the first-run welcome prompt has been shown/dismissed so we don't nag.</summary>
    public bool FirstRunCompleted { get; set; }
    public List<TrackedGame> Games { get; set; } = new();

    [JsonIgnore] public string ConfigPath { get; private set; } = DefaultConfigPath;

    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SaveLocker");

    public static string DefaultConfigPath => Path.Combine(DefaultDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static AgentConfig Load(string? path = null)
    {
        path ??= DefaultConfigPath;
        if (!File.Exists(path))
        {
            var fresh = new AgentConfig { ConfigPath = path };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            fresh.Save();
            return fresh;
        }
        var cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOpts)
                  ?? new AgentConfig();
        cfg.ConfigPath = path;
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public TrackedGame? FindGame(string name) =>
        Games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A game this machine syncs, with its resolved local save location.</summary>
public sealed class TrackedGame
{
    public Guid GameId { get; set; }
    public string Name { get; set; } = "";
    public string? ManifestKey { get; set; }
    /// <summary>The local save directory to archive/restore.</summary>
    public string SaveDirectory { get; set; } = "";
    /// <summary>Process names (without .exe) that, when running, mean the game is in use.</summary>
    public List<string> ProcessNames { get; set; } = new();
    /// <summary>The server version this machine last pulled or pushed (its parent for the next push).</summary>
    public Guid? LastKnownVersionId { get; set; }
    /// <summary>Content hash of the local save at last sync, to detect real changes.</summary>
    public string? LastSyncedHash { get; set; }
}
