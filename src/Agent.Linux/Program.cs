using System.Runtime.InteropServices;
using SaveLocker.Agent;

namespace SaveLocker.Agent.Linux;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 0; }

        // `run` is parsed by hand and BEFORE the option parser: its tail is the game's own command
        // line (`%command%`, which Steam expands into a reaper/proton invocation full of things
        // that look like our flags). Everything after `--` belongs to the game, untouched.
        if (args[0] == "run")
            return await RunWrapperAsync(args[1..]);

        var (command, opts, positionals) = CliArgs.Parse(args);
        var config = AgentConfig.Load(ConfigPath(opts));

        if (command is null) { PrintUsage(); return 0; }

        // Commands shared with the Windows agent (register, push, pull, status, scan, …).
        if (AgentCli.Handles(command))
            return await AgentCli.RunAsync(command, opts, positionals, config,
                new LinuxGameScanner(new Detection(config)));

        switch (command)
        {
            case "daemon":
                await RunDaemonAsync(config, listenOnAllInterfaces: opts.ContainsKey("lan"));
                return 0;

            case "doctor":
                return await Doctor.RunAsync(config);

            case "autostart":
            {
                var autoStart = new SystemdAutoStart();
                if (opts.ContainsKey("disable"))
                {
                    autoStart.SetEnabled(false);
                    Console.WriteLine("Auto-start disabled.");
                }
                else if (opts.ContainsKey("enable"))
                {
                    if (!autoStart.SetEnabled(true))
                    {
                        Console.Error.WriteLine("Could not enable auto-start (is systemd --user available?).");
                        return 1;
                    }
                    Console.WriteLine("Auto-start enabled (systemd --user unit savelocker.service).");
                }
                else
                {
                    Console.WriteLine(autoStart.IsEnabled() ? "enabled" : "disabled");
                }
                return 0;
            }

            case "help" or "--help" or "-h":
                PrintUsage();
                return 0;

            default:
                Console.Error.WriteLine($"Unknown command '{command}'.");
                PrintUsage();
                return 2;
        }
    }

    /// <summary>
    /// <c>savelocker run [--config path] -- &lt;game command&gt;</c>. Steam passes the game's command
    /// line where <c>%command%</c> sits, so we split at the first bare <c>--</c> and hand the tail
    /// to the game verbatim.
    /// </summary>
    private static async Task<int> RunWrapperAsync(string[] tail)
    {
        var sep = Array.IndexOf(tail, "--");
        var ourArgs = sep >= 0 ? tail[..sep] : Array.Empty<string>();
        var childCommand = sep >= 0 ? tail[(sep + 1)..] : tail;

        var (_, opts, _) = CliArgs.Parse(ourArgs);
        var config = AgentConfig.Load(ConfigPath(opts));

        return await ProtonRun.ExecuteAsync(config, childCommand);
    }

    private static async Task RunDaemonAsync(AgentConfig config, bool listenOnAllInterfaces)
    {
        using var cts = new CancellationTokenSource();

        // systemd stops a unit with SIGTERM; Ctrl-C in a shell sends SIGINT. Handle both, so the
        // daemon shuts its listeners and watchers down rather than being killed mid-sync.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            cts.Cancel();
        });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
        {
            ctx.Cancel = true;
            cts.Cancel();
        });

        await using var daemon = new Daemon(config);
        await daemon.RunAsync(listenOnAllInterfaces, cts.Token);
    }

    /// <summary>Config path from --config, else SAVELOCKER_CONFIG (handy in a systemd unit), else the default.</summary>
    private static string? ConfigPath(Dictionary<string, string> opts) =>
        opts.GetValueOrDefault("config")
        ?? Environment.GetEnvironmentVariable("SAVELOCKER_CONFIG");

    private static void PrintUsage() => Console.WriteLine(
        """
        savelocker — SaveLocker agent for Linux (Proton / Steam Deck)

        Setup
          enroll --file <policy.json> [--name <name>]      Set up from a console enrollment file (start here)
          register --name <name> [--admin-password <pw>]   Register this machine by hand instead
          set-server --url <url>                           Point the agent at a server
          trust [--accept]                                 Show the pinned server TLS key, or re-pin it
          doctor                                           Diagnose the whole chain

        Games
          scan                                             Find non-Steam shortcuts and their prefixes
          add-game --name <n> [--dir <path>] [--appid <id>] [--manifest <key>] [--prefix <compatdata>]
          list                                             Show tracked games
          status                                           Server head / lease / conflicts
          hash [game] | --dir <path>                       Content hash (what conflict detection compares)

        Sync
          push [game|all] [--force]                        Upload saves
          pull [game|all] [--force]                        Download saves
          run -- %command%                                 Steam launch wrapper: pull, play, push

        Daemon
          daemon [--lan]                                   Run headless; serves the agent UI on :5178
          autostart --enable | --disable                   systemd --user unit

        Add this to a game's Steam launch options to sync it automatically:
          savelocker run -- %command%
        """);
}
