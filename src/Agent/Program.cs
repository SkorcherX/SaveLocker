using SaveLocker.Shared;

namespace SaveLocker.Agent;

static class Program
{
    // With no recognised command we launch the tray UI (STA thread, no prior
    // await); otherwise we run a one-shot CLI command (the manual-override surface).
    // Main is intentionally NON-async: [STAThread] is ignored on an async Main,
    // which leaves the WinForms thread MTA and makes OLE calls (clipboard, file
    // dialogs) throw. A synchronous STA Main runs the tray correctly; CLI commands
    // are bridged to async via GetAwaiter().GetResult().
    [STAThread]
    static int Main(string[] args)
    {
        var (command, opts, positionals) = CliArgs.Parse(args);
        var config = AgentConfig.Load(opts.GetValueOrDefault("config"));

        if (command is null)
        {
            // Single-instance guard: a second tray launch (e.g. auto-start firing while
            // one is already open) just exits. The mutex name is shared with the
            // installer's AppMutex so setup can detect a running agent and prompt the
            // user to close it before replacing files. CLI one-shots are not guarded.
            using var mutex = new Mutex(initiallyOwned: true, "SaveLocker.Agent", out var isNew);
            if (!isNew) return 0;
            TrayApp.Run(config);
            return 0;
        }

        return RunCommandAsync(command, opts, positionals, config).GetAwaiter().GetResult();
    }

    static async Task<int> RunCommandAsync(
        string command, Dictionary<string, string> opts, List<string> positionals, AgentConfig config)
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
                    var scanner = new GameScanner(detection);
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
                        Console.WriteLine($"  {c.Name}  <{c.Source}>{cloud}");
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
                    var dirs = await detection.ResolveSaveDirectoriesAsync(key);
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
                        var resolved = await detection.ResolveSaveDirectoriesAsync(manifestKey ?? name);
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

    static IEnumerable<TrackedGame> GamesFor(string? name, AgentConfig config)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("all", StringComparison.OrdinalIgnoreCase))
            return config.Games;
        var g = config.FindGame(name);
        return g is null ? Enumerable.Empty<TrackedGame>() : new[] { g };
    }
}
