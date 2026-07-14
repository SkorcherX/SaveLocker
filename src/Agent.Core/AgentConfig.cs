using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveLocker.Agent;

/// <summary>
/// Per-machine agent configuration, persisted as JSON. Default location is
/// %PROGRAMDATA%\SaveLocker\config.json, overridable via --config for tests
/// (lets two "machines" run side by side against one server).
/// </summary>
public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "http://localhost:5179";
    public string MachineName { get; set; } = Environment.MachineName;
    public string? ApiKey { get; set; }
    public Guid? MachineId { get; set; }
    /// <summary>
    /// TOFU pin of the server's TLS public key, recorded at enrollment. Null over plain http, or
    /// for agents registered before enrollment existed. See <see cref="ServerTrust"/>.
    /// </summary>
    public string? ServerPin { get; set; }
    public string ManifestCachePath { get; set; } =
        Path.Combine(DefaultDir, "manifest.yaml");
    /// <summary>Set once the first-run welcome prompt has been shown/dismissed so we don't nag.</summary>
    public bool FirstRunCompleted { get; set; }
    public List<TrackedGame> Games { get; set; } = new();
    /// <summary>Cumulative count of save versions successfully pushed to the server.</summary>
    public int TotalSavesPushed { get; set; }
    /// <summary>UTC timestamp of the most recent push or pull across all games.</summary>
    public DateTime? LastSyncTime { get; set; }
    /// <summary>Version string the user chose to skip ("Skip This Version"); suppresses update prompts for that version.</summary>
    public string? SkipVersion { get; set; }
    /// <summary>UTC timestamp of the last update check; used to enforce a 24 h cooldown between background checks.</summary>
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>
    /// Seconds a save folder must be quiet — no file changes, nothing open for writing — before an
    /// automatic push archives it. Games that keep flushing after exit need a longer wait; 0 disables
    /// the gate and archives immediately (fast, but can capture a half-written save).
    /// </summary>
    public int SettleQuietSeconds { get; set; } = 10;
    /// <summary>Hard cap on the settle wait. Past this the push proceeds regardless, so a game that
    /// never goes quiet can't block syncing forever.</summary>
    public int SettleMaxWaitSeconds { get; set; } = 120;

    [JsonIgnore] public string ConfigPath { get; private set; } = DefaultConfigPath;

    /// <summary>
    /// Agent state root. Windows keeps the machine-wide %PROGRAMDATA%\SaveLocker.
    /// Linux must NOT: SpecialFolder.CommonApplicationData maps to /usr/share there, which is
    /// not user-writable and — on SteamOS — is the immutable rootfs, wiped on every update.
    /// State goes in the user's XDG data dir instead (Decisions.md §5).
    /// </summary>
    public static string DefaultDir => Path.Combine(StateRoot(), "SaveLocker");

    private static string StateRoot()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return xdg;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share");
    }

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
    /// <summary>
    /// Steam AppID this game launches under, as the <b>unsigned</b> string Steam uses to name
    /// <c>compatdata/&lt;appid&gt;/</c>. Set for non-Steam shortcuts on Linux; the launch wrapper
    /// matches on it to find the game for the prefix Steam handed it. Null on Windows.
    /// </summary>
    public string? SteamAppId { get; set; }
    /// <summary>The server version this machine last pulled or pushed (its parent for the next push).</summary>
    public Guid? LastKnownVersionId { get; set; }
    /// <summary>Content hash of the local save at last sync, to detect real changes.</summary>
    public string? LastSyncedHash { get; set; }
    /// <summary>Effective exclude globs (global defaults ∪ per-game) from the server;
    /// files matching these are skipped when hashing and archiving.</summary>
    public List<string> ExcludeGlobs { get; set; } = new();
}
