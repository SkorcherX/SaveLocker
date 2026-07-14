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
        var engine = new SyncEngine(config, api, log: Log, notify: Log, offlineQueue: new OfflineQueue());

        // The game must be found before launch, but a failure here must never stop it starting:
        // a save-sync tool that prevents you playing is worse than one that misses a sync.
        var game = await ResolveGameAsync(config, appId, prefix, Log);

        if (game is not null)
        {
            try { await engine.OnGameLaunchAsync(game); }
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
            try { await engine.OnGameExitAsync(game); }
            catch (Exception ex) { Log($"post-exit sync failed: {ex.Message}"); }
        }

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
    /// Which tracked game is this? The AppID is the reliable key — it is what Steam names the
    /// prefix with — so match on it first, then fall back to the prefix path for a game that was
    /// mapped before it had an AppID recorded.
    /// </summary>
    private static async Task<TrackedGame?> ResolveGameAsync(
        AgentConfig config, string? appId, string? prefix, Action<string> log)
    {
        var game = config.Games.FirstOrDefault(g =>
            !string.IsNullOrEmpty(appId) && g.SteamAppId == appId);

        // A prefix directory is named for its AppID, so its folder name identifies the game too.
        game ??= prefix is null
            ? null
            : config.Games.FirstOrDefault(g =>
                g.SteamAppId is { Length: > 0 } id &&
                string.Equals(id, Path.GetFileName(prefix.TrimEnd('/')), StringComparison.Ordinal));

        if (game is null) return null;

        // Mapped but no save directory yet: resolve it inside the prefix Steam just gave us. This
        // is the one moment we know the prefix for certain, so it is the best time to fill it in.
        if (string.IsNullOrWhiteSpace(game.SaveDirectory) && prefix is not null)
        {
            var detection = new Detection(config);
            var dirs = await detection.ResolveSaveDirectoriesAsync(
                game.ManifestKey ?? game.Name, PathResolver.Proton(prefix));

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
