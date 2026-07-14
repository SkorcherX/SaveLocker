using SaveLocker.Shared;

namespace SaveLocker.Agent.Linux;

/// <summary>
/// Answers "why isn't this working?" on a machine with no UI to look at. A Deck in Game Mode
/// shows the user nothing, so every link in the chain — server, Steam, shortcuts, prefixes,
/// save dirs, permissions — is checked out loud, in the order it has to succeed.
/// </summary>
public static class Doctor
{
    private static int _problems;

    public static async Task<int> RunAsync(AgentConfig config)
    {
        _problems = 0;

        Console.WriteLine("SaveLocker doctor");
        Console.WriteLine("=================");
        Console.WriteLine();

        Section("Agent");
        Info("config", config.ConfigPath);
        Info("state dir", AgentConfig.DefaultDir);
        Info("log", AgentLogger.LogPath);
        Check("state dir is writable", IsWritable(AgentConfig.DefaultDir),
            $"cannot write to {AgentConfig.DefaultDir} — the agent cannot save its config or queue.");

        Console.WriteLine();
        Section("Server");
        Info("url", config.ServerUrl);
        if (string.IsNullOrEmpty(config.ApiKey))
            Problem("this machine is not registered. Run: savelocker register --name <name>");
        else
            Info("machine", $"{config.MachineName} ({config.MachineId})");
        await CheckServerAsync(config);

        Console.WriteLine();
        Section("Steam");
        var roots = SteamRoots.Find();
        Check("Steam installation found", roots.Count > 0,
            "no Steam root found (looked for ~/.steam/steam, ~/.local/share/Steam and the Flatpak path).");
        foreach (var r in roots) Info("root", r);

        var shortcuts = await ReadShortcutsAsync(roots);
        Check("non-Steam shortcuts found", shortcuts.Count > 0,
            "no shortcuts in shortcuts.vdf. SaveLocker syncs non-Steam games added to Steam; " +
            "Steam-store games already have Steam Cloud.");

        Console.WriteLine();
        Section("Shortcuts and Proton prefixes");
        foreach (var (root, s) in shortcuts)
        {
            if (s.AppId is null)
            {
                Problem($"'{s.AppName}': no AppID in shortcuts.vdf — its Proton prefix cannot be located.");
                continue;
            }

            var prefix = SteamRoots.CompatDataPath(root, s.AppId);
            if (prefix is null)
            {
                Console.WriteLine($"  - {s.AppName}  (appid {s.AppId})");
                Console.WriteLine($"      no prefix at {Path.Combine(root, "steamapps", "compatdata", s.AppId)}");
                Console.WriteLine("      → launch it once through Steam with Proton (Force compatibility tool) to create it.");
                continue;
            }
            Console.WriteLine($"  ✓ {s.AppName}  (appid {s.AppId})");
            Console.WriteLine($"      prefix: {prefix}");
        }

        Console.WriteLine();
        Section("Tracked games");
        if (config.Games.Count == 0)
            Console.WriteLine("  (none — add one with: savelocker add-game --name <name> --dir <path> --appid <appid>)");

        foreach (var g in config.Games)
        {
            Console.WriteLine($"  {g.Name}");
            Info("    appid", g.SteamAppId ?? "(none — the launch wrapper cannot match this game)");
            if (string.IsNullOrWhiteSpace(g.SaveDirectory))
            {
                Problem($"'{g.Name}' has no save directory. Set one: savelocker add-game --name \"{g.Name}\" --dir <path>");
                continue;
            }
            Info("    save dir", g.SaveDirectory);
            if (!Directory.Exists(g.SaveDirectory))
                Problem($"'{g.Name}' save directory does not exist: {g.SaveDirectory}");
            else if (!IsWritable(g.SaveDirectory))
                Problem($"'{g.Name}' save directory is not writable — pull would fail: {g.SaveDirectory}");
            else
                Info("    files", SaveArchive.ListFiles(g.SaveDirectory, g.ExcludeGlobs).Count.ToString());

            if (g.SteamAppId is not null && config.Games.Any(o => o != g && o.SteamAppId == g.SteamAppId))
                Problem($"more than one tracked game claims appid {g.SteamAppId}; " +
                        "the launch wrapper would pick one arbitrarily.");
        }

        Console.WriteLine();
        Section("Platform");
        var probe = FileLockProbe.FirstWriter(AgentConfig.DefaultDir, Array.Empty<string>());
        if (probe.Supported)
            Info("open-file detection", "available (/proc)");
        else
            Console.WriteLine("  ! open-file detection unavailable — the settle gate will wait on the " +
                              "file fingerprint alone. Saves still sync; a game that flushes without " +
                              "changing file size may push slightly early.");

        Console.WriteLine();
        if (_problems == 0)
        {
            Console.WriteLine("No problems found.");
            return 0;
        }
        Console.WriteLine($"{_problems} problem(s) found — see the ✗ lines above.");
        return 1;
    }

    private static async Task CheckServerAsync(AgentConfig config)
    {
        try
        {
            var api = ApiClient.For(config);
            var games = await api.ListGamesAsync();
            Check("server reachable", true, "");
            Info("games on server", games.Count.ToString());
        }
        catch (Exception ex)
        {
            Problem($"server unreachable at {config.ServerUrl}: {ex.Message}");
        }
    }

    private static async Task<List<(string Root, SteamShortcut Shortcut)>> ReadShortcutsAsync(
        IReadOnlyList<string> roots)
    {
        var all = new List<(string, SteamShortcut)>();
        foreach (var root in roots)
            foreach (var s in await SteamShortcuts.ReadAllAsync(root))
                all.Add((root, s));
        return all;
    }

    /// <summary>Probe writability for real — the permission bits alone don't account for a
    /// read-only mount, and SteamOS mounts plenty of things read-only.</summary>
    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".savelocker-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static void Section(string name) => Console.WriteLine($"── {name} ──");
    private static void Info(string label, string value) => Console.WriteLine($"  {label}: {value}");

    private static void Check(string label, bool ok, string problem)
    {
        if (ok) Console.WriteLine($"  ✓ {label}");
        else Problem(problem);
    }

    private static void Problem(string message)
    {
        _problems++;
        Console.WriteLine($"  ✗ {message}");
    }
}
