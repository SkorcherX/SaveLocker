using SaveLocker.Shared;

namespace SaveLocker.Agent;

/// <summary>
/// Turns scan candidates into tracked games: creates each on the server and records it locally.
/// Shared by the Windows tray and the Linux daemon — both enroll from the same agent UI.
/// </summary>
public static class Enroller
{
    /// <summary>
    /// Enroll the candidates at <paramref name="ids"/>. Skips ones already tracked or with no
    /// resolved save directory. Saves the config if anything was added.
    /// </summary>
    public static async Task<(int enrolled, int skipped)> EnrollAsync(
        AgentConfig config,
        IReadOnlyList<ScanCandidate> candidates,
        int[] ids,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            throw new InvalidOperationException("Not registered yet. Open Settings and click Register first.");

        var api = ApiClient.For(config);
        var enrolled = 0;
        var skipped = 0;

        foreach (var id in ids)
        {
            if (id < 0 || id >= candidates.Count) continue;
            var c = candidates[id];
            if (config.FindGame(c.Name) is not null) { skipped++; continue; }
            if (string.IsNullOrEmpty(c.SuggestedSaveDir)) { skipped++; continue; }

            // A scanner's save-root heuristic can land on something far too broad — an install tree,
            // a profile folder. Enrolling it would create the game on the server AND map it, so the
            // refusal has to happen before the game exists. WA-02.
            var check = SavePathGuard.Check(c.SuggestedSaveDir, config.StateDir);
            if (!check.Ok)
            {
                AgentLogger.Log($"Enrollment skipped '{c.Name}': {check.Reason} " +
                                $"(scanner suggested '{c.SuggestedSaveDir}')");
                skipped++;
                continue;
            }

            // The MANIFEST's spelling, not the shortcut's, is the server-side identity. A shortcut
            // carries whatever its owner typed, and the server matches names case-insensitively but
            // not past punctuation — so a Deck saying "DRAGON QUEST III HD 2D Remake" and a PC saying
            // "DRAGON QUEST III HD-2D Remake" created two games that could never sync to each other.
            // Falls back to the scanned name for anything the manifest does not know.
            var serverName = c.ManifestKey ?? c.Name;
            var game = await api.CreateGameAsync(new CreateGameRequest(serverName, c.ManifestKey, null));
            // Persisted per candidate, not once at the end: a later candidate that fails — or a UI
            // window closed mid-batch — must not lose the games already created on the server, along
            // with their Steam AppIDs. SetTracked also clears any per-machine opt-out, so re-adding
            // a game removed here earlier actually re-adds it.
            config.SetTracked(game.Id, tracked: true, entry: new TrackedGame
            {
                GameId = game.Id,
                Name = game.Name,
                ManifestKey = c.ManifestKey,
                SaveDirectory = check.Canonical!,
                SteamAppId = c.SteamAppId,
                HasSteamCloud = c.HasSteamCloud,
                InstallDir = c.InstallDir,
                // Without this the Windows ProcessWatcher excludes the game outright, so lease,
                // exit-push and the running-game pull refusal never run for anything enrolled
                // through the UI. Only the CLI's --proc used to populate it. WA-08.
                ProcessNames = c.SuggestedProcessName is { } proc ? new List<string> { proc } : new(),
            });

            // Report the chosen path to the server now, so it is authoritative from the start. The
            // Windows tray gets away without this because its in-process CommandPoller reports the
            // path on its next tick — but the Deck's `savelocker ui` is a separate process with no
            // poller, so without this the console shows "not set" and a daemon reconcile (which
            // resolves paths from the server) blanks the local path it was never told about, leaving
            // the game unmapped and a Sync reporting "no matching mapped game on this machine".
            try { await api.SetMachinePathAsync(game.Id, check.Canonical!); }
            catch (Exception ex) { AgentLogger.LogException("Enroller.SetMachinePath", ex); }
            enrolled++;
        }

        return (enrolled, skipped);
    }
}
