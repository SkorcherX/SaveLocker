using System.Diagnostics;
using SaveLocker.Shared;

namespace SaveLocker.Agent.Linux;

/// <summary>
/// The Steam launch wrapper: <c>savelocker run -- %command%</c> in a game's Launch Options.
///
/// This is the primary sync trigger on Linux (Decisions.md §3). Steam runs us *instead of* the
/// game and hands us the real command line, so we get exact pre-launch / post-exit hooks with no
/// process polling — which on Linux is genuinely unreliable, since Proton games hide behind
/// <c>reaper</c> / <c>pv-bwrap</c> / <c>wine</c> and <c>/proc/&lt;pid&gt;/comm</c> truncates at 15 chars.
///
/// We never talk to Steam. We read two environment variables it sets and supervise a child:
///   • <c>STEAM_COMPAT_DATA_PATH</c> — the exact Wine prefix. No compatdata scanning, no guessing.
///   • <c>SteamAppId</c>            — which game this is.
/// A portable (non-prefix) game still works: the prefix is simply not needed to find its saves.
/// </summary>
public static class ProtonRun
{
    /// <summary>
    /// Pull, run the game to completion, then settle-and-push. Returns the game's own exit code —
    /// Steam shows it to the user, so we must not swallow or replace it.
    /// </summary>
    public static async Task<int> ExecuteAsync(AgentConfig config, string[] childCommand)
    {
        if (childCommand.Length == 0)
        {
            Console.Error.WriteLine(
                "Nothing to run. Use it as a Steam launch option:  savelocker run -- %command%");
            return 2;
        }

        var prefix = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH");
        var appId = Environment.GetEnvironmentVariable("SteamAppId");

        void Log(string m) => AgentLogger.Log($"[run] {m}");
        Log($"launch: appid={appId ?? "(none)"} prefix={prefix ?? "(none)"}");

        var api = ApiClient.For(config);
        var offlineQueue = OfflineQueue.For(config);

        // The launch wrapper IS the sync path for a Proton game, so it is where a Deck's conflicts
        // and blocked pulls actually happen. Without a reporter here the console would never hear
        // about the failures that matter most (Decisions.md §2).
        var health = HealthReporter.For(config);
        // Disposed at the end of the run: this process holds a lease renewer for the whole game
        // session, and it must not outlive the wrapper. WA-06.
        await using var engine = new SyncEngine(config, api, log: Log, notify: Log,
            offlineQueue: offlineQueue, health: health);

        // The game must be found before launch, but a failure here must never stop it starting:
        // a save-sync tool that prevents you playing is worse than one that misses a sync.
        var game = await ResolveGameAsync(config, appId, prefix, Log);
        if (game is not null && prefix is not null) await MapExtraFoldersAsync(config, api, engine, game, prefix, Log);

        if (game is not null)
        {
            try
            {
                // The result was previously discarded, so a Deck user who launched a game another
                // machine had checked out got no warning anywhere: this process is not the daemon,
                // and Game Mode never shows the console. Persist it so both UIs can surface it.
                // preLaunch: true — this is the real boundary. Steam ran us instead of the game and
                // the child below has not started yet, so nothing can have the save open.
                var (granted, holder) = await engine.OnGameLaunchAsync(game, preLaunch: true);
                if (!granted && holder is not null)
                    LeaseWarningStore.For(config).Add(game.Name, holder);
            }
            catch (Exception ex) { Log($"pre-launch sync failed, launching anyway: {ex.Message}"); }
        }
        else
        {
            Log("no tracked game matches this launch — running without sync. " +
                "Map it with: savelocker add-game --name <name> --appid " + (appId ?? "<appid>"));
        }

        var exitCode = await RunChildAsync(childCommand, Log);

        if (game is not null)
        {
            // The settle gate runs here: the game's process is gone, but its save may still be
            // flushing. OnGameExitAsync waits for quiet before it archives.
            // The checkout is over, so the warning is no longer true whatever happened to the sync.
            LeaseWarningStore.For(config).Clear(game.Name);

            try { await engine.OnGameExitAsync(game); }
            catch (Exception ex) { Log($"post-exit sync failed: {ex.Message}"); }
        }

        // This process is about to die and it has no poller, so anything it just learned has to go
        // out now. What it cannot send survives on disk: the events file is shared with the daemon,
        // which drains it on its next heartbeat — and the server deduplicates, so an event sent
        // twice costs nothing while an event lost costs the user a silent failure.
        await health.SendAsync(api, config, offlineQueue, AgentLogger.Log);

        return exitCode;
    }

    /// <summary>Run the game and wait for it. Child stdio is inherited so Steam's overlay/logs behave.</summary>
    private static async Task<int> RunChildAsync(string[] command, Action<string> log)
    {
        var psi = new ProcessStartInfo(command[0]) { UseShellExecute = false };
        foreach (var arg in command[1..]) psi.ArgumentList.Add(arg);

        using var child = Process.Start(psi);
        if (child is null)
        {
            log($"failed to start: {command[0]}");
            return 1;
        }

        await child.WaitForExitAsync();
        log($"game exited with code {child.ExitCode}.");
        return child.ExitCode;
    }

    /// <summary>
    /// The game's extra save folders not mapped here yet, expanded inside the prefix Steam just gave
    /// us — the one place the prefix is certain (tasks/multiple-save-paths plan §7). Mapping goes
    /// through the engine so a shadow's files move in; a folder that already holds different files is
    /// left for the user to decide, and keeps syncing through its shadow meanwhile. Never fatal: the
    /// game launches whatever happens here.
    /// </summary>
    private static async Task MapExtraFoldersAsync(AgentConfig config, ApiClient api, SyncEngine engine,
        TrackedGame game, string prefix, Action<string> log)
    {
        var resolver = PathResolver.Proton(prefix, game.InstallDir, SteamLayout.RootFromCompatData(prefix));
        foreach (var p in game.ExtraPaths.Where(p => !p.IsMapped && PathResolver.IsTemplate(p.Template)).ToList())
        {
            try
            {
                if (resolver.ResolveToDirectory(p.Template!) is not { } dir || !Directory.Exists(dir)) continue;
                var result = await engine.MapSavePathAsync(game, p.Key, dir);
                if (!result.Ok)
                {
                    log($"'{game.Name}' save folder '{p.Key}' not mapped to {dir}: {result.Error}");
                    continue;
                }
                log($"mapped '{game.Name}' save folder '{p.Key}' to {result.Directory} (resolved inside the prefix).");
                try
                {
                    await api.SetMachinePathAsync(game.GameId, result.Directory!, p.Key);
                    p.PathUnreported = false;
                    config.SaveGameFolders(game);
                }
                catch (Exception ex) { log($"could not report the folder to the server: {ex.Message}"); }
            }
            catch (Exception ex) { log($"mapping '{game.Name}' save folder '{p.Key}' failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Which tracked game is this? The AppID is the reliable key — it is what Steam names the
    /// prefix with — so match on it first, then fall back to the prefix path for a game that was
    /// mapped before it had an AppID recorded.
    /// </summary>
    private static async Task<TrackedGame?> ResolveGameAsync(
        AgentConfig config, string? appId, string? prefix, Action<string> log)
    {
        // ResolveSteamAppId(), not SteamAppId: a game mapped to a folder inside a prefix HAS an
        // AppID whether or not one was ever recorded, and matching only the stored field meant such
        // a game matched nothing. It did not fail loudly — the wrapper logged "no tracked game
        // matches this launch" and played the whole session with no sync at all, while the game was
        // tracked, its folder mapped and its launch options correct. Two of four tracked games on
        // real hardware were in exactly that state (2026-08-15).
        var game = config.Games.FirstOrDefault(g =>
            !string.IsNullOrEmpty(appId) && g.ResolveSteamAppId() == appId);

        // A prefix directory is named for its AppID, so its folder name identifies the game too.
        game ??= prefix is null
            ? null
            : config.Games.FirstOrDefault(g =>
                g.ResolveSteamAppId() is { Length: > 0 } id &&
                string.Equals(id, Path.GetFileName(prefix.TrimEnd('/')), StringComparison.Ordinal));

        if (game is null) return null;

        // Mapped but no save directory yet: resolve it inside the prefix Steam just gave us. This
        // is the one moment we know the prefix for certain, so it is the best time to fill it in.
        if (string.IsNullOrWhiteSpace(game.SaveDirectory) && prefix is not null)
        {
            var detection = new Detection(config);
            var dirs = await detection.ResolveProtonAsync(
                game.ManifestKey ?? game.Name, prefix, game.InstallDir);

            if (dirs.FirstOrDefault() is { } dir)
            {
                game.SaveDirectory = dir;
                config.Save();
                log($"mapped '{game.Name}' to {dir} (resolved inside the prefix).");
            }
            else
            {
                log($"'{game.Name}' has no save directory and none could be resolved in the prefix. " +
                    "Set one with: savelocker add-game --name \"" + game.Name + "\" --dir <path>");
                return null;
            }
        }

        return game;
    }
}
