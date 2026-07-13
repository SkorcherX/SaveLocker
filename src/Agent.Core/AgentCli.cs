using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// The agent's one-shot command surface, shared by the Windows and Linux hosts. Everything here
/// is platform-neutral; the pieces that are not (game discovery) arrive as <see cref="IGameScanner"/>.
/// Host-specific commands (the Windows tray, the Linux daemon / run wrapper / doctor) stay in
/// their own Program and delegate the rest here, so the two never drift.
/// </summary>
public static class AgentCli
{
    /// <summary>Commands handled here. A host checks this before falling through to its own.</summary>
    public static bool Handles(string command) => command is
        "register" or "set-server" or "whoami" or "search" or "scan" or "resolve" or
        "add-game" or "list" or "status" or "push" or "pull" or "refresh-manifest" or "log" or
        "hash";

    /// <summary>Run one command. Returns the process exit code.</summary>
    public static async Task<int> RunAsync(
        string command,
        Dictionary<string, string> opts,
        List<string> positionals,
        AgentConfig config,
        IGameScanner scanner)
    {
        ApiClient Api() => new(config.ServerUrl, config.ApiKey);
        void Log(string msg) { Console.WriteLine(msg); AgentLogger.Log(msg); }
        SyncEngine Engine() => new(config, Api(), Log);
        var detection = new Detection(config);

        try
        {
            switch (command)
            {
                case "register":
                {
                    var name = opts.GetValueOrDefault("name") ?? config.MachineName;
                    var adminPassword = opts.GetValueOrDefault("admin-password");
                    var reg = await Api().RegisterAsync(name, adminPassword);
                    config.MachineName = name;
                    config.ApiKey = reg.ApiKey;
                    config.MachineId = reg.MachineId;
                    config.Save();
                    Console.WriteLine($"Registered '{name}' (id {reg.MachineId}).");
                    Console.WriteLine($"API key:  {reg.ApiKey}");
                    Console.WriteLine($"Saved to: {config.ConfigPath}");
                    Console.WriteLine("Paste the API key into the dashboard to view this machine's saves.");
                    Console.WriteLine("(Re-registering this name later rotates the key; retrieve it anytime with 'whoami'.)");
                    break;
                }

                case "set-server":
                {
                    var url = opts.GetValueOrDefault("url") ?? positionals.FirstOrDefault()
                              ?? throw new ArgumentException("Pass the server URL, e.g. set-server --url https://lgs.example.com");
                    config.ServerUrl = url.TrimEnd('/');
                    config.Save();
                    Console.WriteLine($"Server URL set to {config.ServerUrl}");
                    break;
                }

                case "whoami":
                {
                    // Local-only: print the stored identity + key for this config.
                    Console.WriteLine($"Machine:   {config.MachineName}");
                    Console.WriteLine($"MachineId: {(config.MachineId?.ToString() ?? "(not registered)")}");
                    Console.WriteLine($"Server:    {config.ServerUrl}");
                    Console.WriteLine($"API key:   {(config.ApiKey ?? "(not registered)")}");
                    Console.WriteLine($"Config:    {config.ConfigPath}");
                    break;
                }

                case "search":
                {
                    var term = positionals.FirstOrDefault() ?? "";
                    foreach (var n in await detection.SearchAsync(term))
                        Console.WriteLine("  " + n);
                    break;
                }

                case "scan":
                {
                    // Local-only: discover enrollment candidates on this machine.
                    var hideCloud = opts.ContainsKey("no-cloud");
                    var candidates = (await scanner.ScanAsync())
                        .Where(c => !hideCloud || !c.HasSteamCloud)
                        .ToList();
                    if (candidates.Count == 0)
                    {
                        Console.WriteLine("No games discovered.");
                        break;
                    }
                    Console.WriteLine($"Discovered {candidates.Count} candidate(s):");
                    foreach (var c in candidates)
                    {
                        var save = c.SuggestedSaveDir ?? "(save dir unknown — pass --dir on add-game)";
                        var cloud = c.HasSteamCloud ? " [Steam Cloud]" : "";
                        var appid = c.SteamAppId is null ? "" : $" appid={c.SteamAppId}";
                        Console.WriteLine($"  {c.Name}  <{c.Source}>{cloud}{appid}");
                        Console.WriteLine($"      save: {save}");
                    }
                    break;
                }

                case "resolve":
                {
                    // Local-only: resolve a manifest game's save dirs on this machine.
                    var key = opts.GetValueOrDefault("manifest")
                              ?? positionals.FirstOrDefault()
                              ?? throw new ArgumentException("Pass a game name or --manifest.");
                    var dirs = await detection.ResolveSaveDirectoriesAsync(key, ResolverFor(opts));
                    if (dirs.Count == 0) Console.WriteLine("(no existing save directory found)");
                    foreach (var d in dirs) Console.WriteLine("Resolved: " + d);
                    break;
                }

                case "add-game":
                {
                    var name = opts.GetValueOrDefault("name")
                               ?? throw new ArgumentException("--name is required.");
                    var manifestKey = opts.GetValueOrDefault("manifest");
                    var dir = opts.GetValueOrDefault("dir");

                    if (string.IsNullOrEmpty(dir))
                    {
                        var resolved = await detection.ResolveSaveDirectoriesAsync(
                            manifestKey ?? name, ResolverFor(opts));
                        dir = resolved.FirstOrDefault()
                              ?? throw new InvalidOperationException(
                                  "Could not auto-detect a save directory; pass --dir.");
                        Console.WriteLine($"Auto-detected save directory: {dir}");
                    }

                    var game = await Api().CreateGameAsync(new CreateGameRequest(name, manifestKey, null));
                    var existing = config.FindGame(name);
                    var tracked = existing ?? new TrackedGame();
                    tracked.GameId = game.Id;
                    tracked.Name = game.Name;
                    tracked.ManifestKey = manifestKey;
                    tracked.SaveDirectory = dir!;
                    if (opts.TryGetValue("appid", out var appId) && !string.IsNullOrWhiteSpace(appId))
                        tracked.SteamAppId = appId.Trim();
                    if (opts.TryGetValue("proc", out var proc) && !string.IsNullOrWhiteSpace(proc))
                        tracked.ProcessNames = proc
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToList();
                    if (existing is null) config.Games.Add(tracked);
                    config.Save();
                    Console.WriteLine($"Tracking '{name}' -> {dir}");
                    break;
                }

                case "list":
                    foreach (var g in config.Games)
                        Console.WriteLine($"  {g.Name}  [{g.SaveDirectory}]  procs={string.Join(",", g.ProcessNames)}");
                    break;

                case "status":
                {
                    var api = Api();
                    foreach (var g in config.Games)
                    {
                        var s = await api.GetStateAsync(g.GameId);
                        var head = s?.Head is null
                            ? "(none)"
                            : $"{s.Head.Id:N}".Substring(0, 8) + $" from {s.Head.MachineName}";
                        var lease = s?.Lease?.HolderMachineName is { } h ? $"leased by {h}" : "free";
                        var conflict = s?.HasOpenConflict == true ? "  *** CONFLICT ***" : "";
                        Console.WriteLine($"  {g.Name}: head={head}, {lease}{conflict}");
                    }
                    break;
                }

                case "hash":
                {
                    // The content hash is what conflict detection compares, so it must be identical
                    // for the same bytes on Windows and on Linux. Printing it is the only way to
                    // assert that from outside the process (see tests/cross-os).
                    if (opts.TryGetValue("dir", out var hashDir) && !string.IsNullOrWhiteSpace(hashDir))
                    {
                        var globs = opts.GetValueOrDefault("exclude")?
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        Console.WriteLine(SaveArchive.HashDirectory(hashDir.Trim(), globs));
                        break;
                    }

                    foreach (var g in GamesFor(positionals.FirstOrDefault(), config))
                        Console.WriteLine($"  {g.Name}: {SaveArchive.HashDirectory(g.SaveDirectory, g.ExcludeGlobs)}");
                    break;
                }

                case "push":
                {
                    var engine = Engine();
                    var force = opts.ContainsKey("force");
                    foreach (var g in GamesFor(positionals.FirstOrDefault(), config))
                        await engine.PushAsync(g, force);
                    break;
                }

                case "pull":
                {
                    var engine = Engine();
                    var force = opts.ContainsKey("force");
                    foreach (var g in GamesFor(positionals.FirstOrDefault(), config))
                        await engine.PullAsync(g, force);
                    break;
                }

                case "refresh-manifest":
                {
                    var m = await detection.GetManifestAsync(forceRefresh: true);
                    Console.WriteLine($"Manifest refreshed: {m.GameCount} games.");
                    break;
                }

                case "log":
                {
                    var n = opts.TryGetValue("n", out var ns) && int.TryParse(ns, out var ni) ? ni : 50;
                    if (!File.Exists(AgentLogger.LogPath))
                    {
                        Console.WriteLine("(no log file yet)");
                        break;
                    }
                    var lines = File.ReadAllLines(AgentLogger.LogPath);
                    foreach (var l in lines.TakeLast(n))
                        Console.WriteLine(l);
                    break;
                }

                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    return 2;
            }
            return 0;
        }
        catch (Exception ex)
        {
            var msg = $"Error: {ex}";
            Console.Error.WriteLine(msg);
            AgentLogger.Log(msg);
            return 1;
        }
    }

    /// <summary>
    /// A game's manifest paths resolve inside its Wine prefix when one is given
    /// (<c>--prefix &lt;compatdata/appid&gt;</c>); otherwise against this host.
    /// </summary>
    private static PathResolver? ResolverFor(Dictionary<string, string> opts) =>
        opts.TryGetValue("prefix", out var prefix) && !string.IsNullOrWhiteSpace(prefix)
            ? PathResolver.Proton(prefix.Trim())
            : Detection.HostResolver();

    private static IEnumerable<TrackedGame> GamesFor(string? name, AgentConfig config)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("all", StringComparison.OrdinalIgnoreCase))
            return config.Games;
        var g = config.FindGame(name);
        return g is null ? Enumerable.Empty<TrackedGame>() : new[] { g };
    }
}
