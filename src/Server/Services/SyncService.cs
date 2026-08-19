using SaveLocker.Server.Data;
using SaveLocker.Shared;
using Microsoft.EntityFrameworkCore;

namespace SaveLocker.Server.Services;

/// <summary>
/// Core orchestration: machine registration, leasing, conflict-aware uploads,
/// downloads, and admin actions (conflict resolution, rollback).
/// </summary>
public sealed class SyncService
{
    private readonly AppDbContext _db;
    private readonly ArchiveStore _store;
    private readonly ConflictEscalationPolicy _conflictEscalation;
    private readonly TimeSpan _leaseDuration = TimeSpan.FromHours(6);
    private readonly int _retainPerGame;
    /// <summary>
    /// How long a claimed command stays invisible to other claims. It has to outlast a real
    /// execution — a Sync of a large save can run for minutes — while still being short enough that
    /// a crashed agent's command comes back on its own.
    /// </summary>
    private readonly TimeSpan _commandLease;
    /// <summary>The documented save-upload cap, enforced while copying rather than only by Kestrel.</summary>
    private readonly long _maxUploadBytes;

    public SyncService(
        AppDbContext db,
        ArchiveStore store,
        IConfiguration config,
        ConflictEscalationPolicy conflictEscalation)
    {
        _db = db;
        _store = store;
        _conflictEscalation = conflictEscalation;
        _retainPerGame = config.GetValue<int?>("Storage:RetainVersionsPerGame") ?? 10;
        _commandLease = TimeSpan.FromMinutes(
            config.GetValue<double?>("Commands:LeaseMinutes") ?? 10);
        _maxUploadBytes = (long)(config.GetValue<int?>("Storage:MaxUploadMb") ?? 200) * 1024 * 1024;
    }

    // ----- Machines -----

    public async Task<MachineRegisterResponse> RegisterMachineAsync(string name)
    {
        var existing = await _db.Machines.FirstOrDefaultAsync(m => m.Name == name);
        var apiKey = Tokens.NewApiKey();

        if (existing is not null)
        {
            // Re-register: rotate the key so a re-installed agent can recover.
            existing.ApiKeyHash = Tokens.Hash(apiKey);
            existing.LastSeen = DateTime.UtcNow;
            await Audit(existing.Id, null, "machine.reregister", name);
            await _db.SaveChangesAsync();
            return new MachineRegisterResponse(existing.Id, apiKey);
        }

        var machine = new Machine
        {
            Id = Guid.NewGuid(),
            Name = name,
            ApiKeyHash = Tokens.Hash(apiKey),
            CreatedAt = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow
        };
        _db.Machines.Add(machine);
        await Audit(machine.Id, null, "machine.register", name);
        await _db.SaveChangesAsync();
        return new MachineRegisterResponse(machine.Id, apiKey);
    }

    /// <summary>True when a machine with this exact name is already registered.</summary>
    public async Task<bool> MachineExistsAsync(string name) =>
        await _db.Machines.AnyAsync(m => m.Name == name);

    public async Task<Machine?> AuthenticateAsync(string apiKey)
    {
        var hash = Tokens.Hash(apiKey);
        var machine = await _db.Machines.FirstOrDefaultAsync(m => m.ApiKeyHash == hash);
        if (machine is not null && (DateTime.UtcNow - machine.LastSeen).TotalSeconds > 30)
        {
            machine.LastSeen = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return machine;
    }

    public async Task<List<Machine>> ListMachinesAsync() =>
        await _db.Machines.OrderBy(m => m.Name).ToListAsync();

    /// <summary>
    /// Admin: delete a machine (revoking its API key). Its leases and pending
    /// commands are removed; its uploaded <see cref="SaveVersion"/>s are KEPT as
    /// history (they may still be a game's head) — those rows just lose their
    /// machine name. Returns false if the machine doesn't exist.
    /// </summary>
    public async Task<bool> DeleteMachineAsync(Guid machineId)
    {
        var machine = await _db.Machines.FindAsync(machineId);
        if (machine is null) return false;

        _db.Leases.RemoveRange(await _db.Leases.Where(l => l.MachineId == machineId).ToListAsync());
        _db.AgentCommands.RemoveRange(
            await _db.AgentCommands.Where(c => c.MachineId == machineId).ToListAsync());
        _db.MachineSavePaths.RemoveRange(
            await _db.MachineSavePaths.Where(p => p.MachineId == machineId).ToListAsync());
        _db.MachineScanCandidates.RemoveRange(
            await _db.MachineScanCandidates.Where(c => c.MachineId == machineId).ToListAsync());
        // Clear conflict-policy preference references so no game silently points at a deleted machine.
        var gamesWithPref = await _db.Games
            .Where(g => g.PreferredMachineId == machineId)
            .ToListAsync();
        foreach (var g in gamesWithPref)
        {
            g.PreferredMachineId = null;
            g.ConflictPolicy = ConflictPolicy.Manual;
        }

        // Detach this machine's history explicitly rather than leaning on ON DELETE SET NULL: the
        // FK only fires when SQLite's foreign_keys pragma is on, and a version left pointing at a
        // machine that no longer exists is exactly the dangling state this finding was about.
        // Snapshotting the name here is the last chance to name the uploader at all.
        var history = await _db.SaveVersions.Where(v => v.MachineId == machineId).ToListAsync();
        foreach (var v in history)
        {
            if (string.IsNullOrEmpty(v.MachineName)) v.MachineName = machine.Name;
            v.MachineId = null;
        }

        _db.Machines.Remove(machine);
        await Audit(null, null, "machine.delete", machine.Name);
        await _db.SaveChangesAsync();
        return true;
    }

    // ----- Machine save paths -----

    /// <summary>All stored save paths for a game, one row per machine that has one.</summary>
    public async Task<List<MachineSavePathDto>> GetGameMachinePathsAsync(Guid gameId)
    {
        return await (from p in _db.MachineSavePaths
                      join m in _db.Machines on p.MachineId equals m.Id
                      where p.GameId == gameId
                      orderby m.Name
                      select new MachineSavePathDto(p.MachineId, m.Name, p.SavePath))
            .ToListAsync();
    }

    /// <summary>All stored save paths for one machine, keyed by game ID (for reconcile injection).</summary>
    public async Task<Dictionary<Guid, string>> GetMachinePathMapAsync(Guid machineId)
    {
        return await _db.MachineSavePaths
            .Where(p => p.MachineId == machineId)
            .ToDictionaryAsync(p => p.GameId, p => p.SavePath);
    }

    /// <summary>
    /// Unconfirmed scan guesses for a game, one row per machine that reported one. These are
    /// offered in the console for confirmation; they are never applied on the agent's say-so.
    /// </summary>
    public async Task<List<MachineScanCandidateDto>> GetGameScanCandidatesAsync(Guid gameId)
    {
        return await (from c in _db.MachineScanCandidates
                      join m in _db.Machines on c.MachineId equals m.Id
                      where c.GameId == gameId
                      orderby m.Name
                      select new MachineScanCandidateDto(c.MachineId, m.Name, c.SuggestedPath, c.LastSeen))
            .ToListAsync();
    }

    /// <summary>Upsert a machine's save path for a game (agent-reported or dashboard-set).</summary>
    public async Task SetMachinePathAsync(Guid machineId, Guid gameId, string path)
    {
        var existing = await _db.MachineSavePaths.FindAsync(machineId, gameId);
        var unchanged = existing is not null && existing.SavePath == path;

        if (existing is null)
            _db.MachineSavePaths.Add(new MachineSavePath { MachineId = machineId, GameId = gameId, SavePath = path });
        else
            existing.SavePath = path;

        // The guess has served its purpose once a real path exists — leaving it would make the
        // console keep offering to "apply" a path for a game that is now mapped.
        var guess = await _db.MachineScanCandidates.FindAsync(machineId, gameId);
        if (guess is not null) _db.MachineScanCandidates.Remove(guess);

        // Audit only a real change. Agents re-assert their current path on every poll, so auditing
        // unconditionally writes a row every 20 s per machine per game — ~4,300 a day from one idle
        // Deck, burying the changes anyone actually wants to find. This is the same reasoning that
        // makes health events deduplicate rather than append (HealthService).
        if (!unchanged)
            await Audit(machineId, gameId, "machine_path.set", path);

        await _db.SaveChangesAsync();
    }

    /// <summary>Remove a machine's stored save path for a game.</summary>
    public async Task ClearMachinePathAsync(Guid machineId, Guid gameId)
    {
        var existing = await _db.MachineSavePaths.FindAsync(machineId, gameId);
        if (existing is null) return;
        _db.MachineSavePaths.Remove(existing);
        await _db.SaveChangesAsync();
    }

    // ----- Games -----

    public async Task<List<Game>> ListGamesAsync() =>
        await _db.Games.OrderBy(g => g.Name).ToListAsync();

    /// <summary>Set/clear the suggested save folder propagated to agents.</summary>
    public async Task<bool> SetSuggestedSaveDirAsync(Guid gameId, string? dir)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game is null) return false;
        game.SuggestedSaveDir = string.IsNullOrWhiteSpace(dir) ? null : dir.Trim();
        await Audit(null, gameId, "game.save_dir", game.SuggestedSaveDir);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// An agent offering a <b>template</b> for a game that has no save location recorded yet.
    /// Returns true only if it was actually taken.
    /// <para>
    /// Two rules are enforced here rather than in the agent, because the agent is the untrusted
    /// side: the value <b>must be a template</b>, so this cannot be used to push an arbitrary
    /// machine-specific path onto every machine; and an existing value is <b>never overwritten</b>,
    /// so one misconfigured agent cannot rewrite a location a human chose — or that a
    /// correctly-configured machine established first. First correct machine wins; the console
    /// always overrides.
    /// </para>
    /// </summary>
    public async Task<bool> TrySetSaveTemplateAsync(Guid gameId, string template)
    {
        if (!PathResolver.IsTemplate(template)) return false;

        var game = await _db.Games.FindAsync(gameId);
        if (game is null || !string.IsNullOrWhiteSpace(game.SuggestedSaveDir)) return false;

        game.SuggestedSaveDir = template.Trim();
        await Audit(null, gameId, "game.save_template", game.SuggestedSaveDir);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Admin: enable or disable a game (disabled games are skipped by agents).</summary>
    public async Task<bool> SetGameEnabledAsync(Guid gameId, bool enabled)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game is null) return false;
        game.Enabled = enabled;
        await Audit(null, gameId, enabled ? "game.enable" : "game.disable", game.Name);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Game> CreateGameAsync(CreateGameRequest req)
    {
        // Match by name case-insensitively and trimmed so the PC and laptop map to
        // the SAME game even if they enroll with different casing/whitespace.
        var name = req.Name.Trim();
        var lowered = name.ToLower();
        var existing = await _db.Games.FirstOrDefaultAsync(g => g.Name.ToLower() == lowered);
        if (existing is not null) return existing;

        var game = new Game
        {
            Id = Guid.NewGuid(),
            Name = name,
            ManifestKey = req.ManifestKey,
            CustomPathsJson = req.CustomPathsJson,
            SuggestedSaveDir = string.IsNullOrWhiteSpace(req.SuggestedSaveDir) ? null : req.SuggestedSaveDir.Trim(),
            Enabled = true
        };
        _db.Games.Add(game);
        await Audit(null, game.Id, "game.create", req.Name);
        await _db.SaveChangesAsync();
        return game;
    }

    /// <summary>Admin: remove a game and all its versions, archives, leases, and conflicts.</summary>
    public async Task<bool> DeleteGameAsync(Guid gameId)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game is null) return false;

        var versions = await _db.SaveVersions.Where(v => v.GameId == gameId).ToListAsync();
        _db.SaveVersions.RemoveRange(versions);
        _db.Leases.RemoveRange(await _db.Leases.Where(l => l.GameId == gameId).ToListAsync());
        _db.Conflicts.RemoveRange(await _db.Conflicts.Where(c => c.GameId == gameId).ToListAsync());
        _db.MachineSavePaths.RemoveRange(
            await _db.MachineSavePaths.Where(p => p.GameId == gameId).ToListAsync());
        _db.MachineScanCandidates.RemoveRange(
            await _db.MachineScanCandidates.Where(c => c.GameId == gameId).ToListAsync());
        _db.Games.Remove(game);
        await Audit(null, gameId, "game.delete", game.Name);
        await _db.SaveChangesAsync();
        // The game row is gone, so the audit below can no longer reference it — pass no game id.
        var orphans = versions.Where(v => !_store.TryDelete(v.ArchivePath)).ToList();
        if (orphans.Count > 0)
        {
            await Audit(null, null, "archive.orphaned",
                $"{orphans.Count} archive file(s) left after deleting '{game.Name}'");
            await _db.SaveChangesAsync();
        }
        return true;
    }

    /// <summary>All games with their current state, for the dashboard overview.</summary>
    public async Task<List<GameStateDto>> GetOverviewAsync()
    {
        var games = await _db.Games.OrderBy(g => g.Name).ToListAsync();

        // Batch-query storage totals to avoid N+1 (one GROUP BY instead of one SUM per game).
        var storageTotals = await _db.SaveVersions
            .GroupBy(v => v.GameId)
            .Select(g => new { GameId = g.Key, Total = g.Sum(v => v.Size) })
            .ToDictionaryAsync(x => x.GameId, x => x.Total);

        var result = new List<GameStateDto>(games.Count);
        foreach (var g in games)
        {
            var state = await GetGameStateAsync(g.Id, storageTotals.GetValueOrDefault(g.Id));
            if (state is not null) result.Add(state);
        }
        return result;
    }

    public async Task<GameStateDto?> GetGameStateAsync(Guid gameId, long? precomputedStorage = null)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game is null) return null;

        var head = game.HeadVersionId is null
            ? null
            : await _db.SaveVersions.Include(v => v.Machine)
                .FirstOrDefaultAsync(v => v.Id == game.HeadVersionId);

        var lease = await ActiveLeaseAsync(gameId);
        var hasConflict = await _db.Conflicts
            .AnyAsync(c => c.GameId == gameId && c.Status == ConflictStatus.Open);

        var totalStorage = precomputedStorage
            ?? await _db.SaveVersions.Where(v => v.GameId == gameId).SumAsync(v => v.Size);

        return new GameStateDto(game.ToDto(), head?.ToDto(), lease.ToDto(gameId), hasConflict, totalStorage);
    }

    // ----- Leases -----

    /// <summary>
    /// The game's lease if one is currently held, otherwise null.
    /// <para>
    /// Deliberately a pure read. It used to DELETE an expired row as a side effect, which made
    /// <c>GET /api/overview</c> a write: two dashboard requests landing together both loaded the same
    /// expired lease and both tried to remove it, and the loser got a concurrency exception on a
    /// plain read. An expired row is simply treated as absent — acquisition takes it over in place
    /// and <see cref="SweepExpiredLeasesAsync"/> collects it.
    /// </para>
    /// </summary>
    public async Task<Lease?> ActiveLeaseAsync(Guid gameId)
    {
        var lease = await _db.Leases.Include(l => l.Machine)
            .FirstOrDefaultAsync(l => l.GameId == gameId);
        return lease is null || lease.ExpiresAt < DateTime.UtcNow ? null : lease;
    }

    /// <summary>
    /// Take the game's lease, or report who holds it.
    /// <para>
    /// There is a unique index on <c>Lease.GameId</c>, and this used to read, decide, then insert.
    /// Two machines launching the same game at the same moment both saw no lease and both inserted;
    /// one got a database exception and the caller got a 500 — at precisely the moment lease-based
    /// conflict prevention is the thing being relied on. Both steps below are atomic, and losing the
    /// race is an ordinary <c>Granted=false</c> answer, not an error.
    /// </para>
    /// </summary>
    public async Task<LeaseAcquireResponse> AcquireLeaseAsync(Guid gameId, Guid machineId)
    {
        var now = DateTime.UtcNow;
        var expires = now.Add(_leaseDuration);

        // 1. Take over the existing row if it is already mine or has expired. The predicate is
        //    re-evaluated by the database as the UPDATE runs, so a row someone else just claimed no
        //    longer matches and this affects nothing.
        var claimed = await _db.Leases
            .Where(l => l.GameId == gameId && (l.MachineId == machineId || l.ExpiresAt < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.MachineId, machineId)
                .SetProperty(l => l.AcquiredAt, now)
                .SetProperty(l => l.ExpiresAt, expires));

        if (claimed == 0)
        {
            // 2. Nothing to take over: either there is no lease at all, or someone else holds a live
            //    one. Let the unique index be the arbiter rather than a check that can go stale
            //    between reading and writing.
            var fresh = new Lease
            {
                Id = Guid.NewGuid(),
                GameId = gameId,
                MachineId = machineId,
                AcquiredAt = now,
                ExpiresAt = expires
            };
            _db.Leases.Add(fresh);
            try
            {
                await _db.SaveChangesAsync();
                claimed = 1;
            }
            catch (DbUpdateException)
            {
                // Lost the race. Detach explicitly: a failed Add stays tracked, and the audit write
                // below would otherwise retry the same doomed insert.
                _db.Entry(fresh).State = EntityState.Detached;
            }
        }

        var holder = await _db.Leases.Include(l => l.Machine)
            .FirstOrDefaultAsync(l => l.GameId == gameId);

        // Re-checked against the row that actually exists rather than trusting the step above: it is
        // the holder on disk that decides, and it may have changed hands again in between.
        var granted = claimed > 0 && holder is not null && holder.MachineId == machineId;
        if (granted)
        {
            await Audit(machineId, gameId, "lease.acquire", null);
            await _db.SaveChangesAsync();
        }

        return new LeaseAcquireResponse(granted, holder.ToDto(gameId));
    }

    public async Task ReleaseLeaseAsync(Guid gameId, Guid machineId)
    {
        // Conditional delete: only the holder can release, decided by the database in one statement.
        var removed = await _db.Leases
            .Where(l => l.GameId == gameId && l.MachineId == machineId)
            .ExecuteDeleteAsync();
        if (removed > 0)
        {
            await Audit(machineId, gameId, "lease.release", null);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> RenewLeaseAsync(Guid gameId, Guid machineId)
    {
        var expires = DateTime.UtcNow.Add(_leaseDuration);
        var renewed = await _db.Leases
            .Where(l => l.GameId == gameId && l.MachineId == machineId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresAt, expires));
        if (renewed == 0) return false;

        await Audit(machineId, gameId, "lease.renew", null);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Admin: force-release a stuck lease regardless of holder.</summary>
    public async Task ForceReleaseLeaseAsync(Guid gameId)
    {
        // Same one-statement delete as the other lease paths: two admins clicking at once must not
        // turn the second click into a concurrency exception.
        var removed = await _db.Leases.Where(l => l.GameId == gameId).ExecuteDeleteAsync();
        if (removed > 0)
        {
            await Audit(null, gameId, "lease.force_release", null);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Proactively remove all expired leases. Called hourly by <see cref="LeaseSweeperService"/>
    /// so stale leases don't linger until the next per-game query touches them.
    /// </summary>
    public async Task<int> SweepExpiredLeasesAsync()
    {
        // One conditional DELETE rather than load-then-remove. Loading first opens a window in which
        // a machine renews a lease the sweeper has already decided to delete, and the sweeper then
        // deletes the live one by primary key — the game's checkout silently disappearing mid-session
        // for no reason anyone could see afterwards.
        var now = DateTime.UtcNow;
        return await _db.Leases.Where(l => l.ExpiresAt < now).ExecuteDeleteAsync();
    }

    // ----- Upload (conflict-aware) -----

    /// <summary>
    /// Ingest an uploaded archive. <paramref name="parentVersionId"/> is the head
    /// the uploading machine last knew. If it matches the server head we fast-forward;
    /// if content is unchanged we report NoChange; otherwise we record a conflict and
    /// leave the head untouched for an admin to resolve.
    /// </summary>
    public async Task<UploadResult> UploadAsync(
        Guid gameId, Guid machineId, Guid? parentVersionId,
        string contentHash, Stream archive, bool force, CancellationToken ct = default)
    {
        var (game, serverHead, diverged, noChange) =
            await PrepareUploadAsync(gameId, parentVersionId, contentHash, force, ct);
        if (noChange is not null) return noChange;

        // Persist the incoming archive as a new version regardless of outcome,
        // so the admin can choose it during conflict resolution.
        var versionId = Guid.NewGuid();
        var (rel, size) = await _store.SaveAsync(gameId, versionId, archive, _maxUploadBytes, ct);

        try
        {
            return await IngestAsync(game, versionId, rel, size, machineId, parentVersionId,
                contentHash, serverHead, diverged, force, ct);
        }
        catch
        {
            // The archive is published but nothing indexes it. Remove it rather than leave a file
            // no row can ever name — the inverse of the state the staged write prevents.
            _store.TryDelete(rel);
            throw;
        }
    }

    /// <summary>
    /// The conflict-detection preamble shared by the single-shot and chunked upload paths: look up
    /// the game and its current head, and decide up front whether this content is a no-op (matches
    /// the head exactly) or diverges from the parent the uploader last knew. Split out because the
    /// chunked path needs to run it twice — once at Begin, to skip the transfer entirely when nothing
    /// changed, and again at Complete against whatever the head has become by the time the last byte
    /// landed, which is the only moment that matters for what actually gets published.
    /// </summary>
    private async Task<(Game Game, SaveVersion? ServerHead, bool Diverged, UploadResult? NoChange)> PrepareUploadAsync(
        Guid gameId, Guid? parentVersionId, string contentHash, bool force, CancellationToken ct)
    {
        var game = await _db.Games.FindAsync(new object?[] { gameId }, ct)
                   ?? throw new InvalidOperationException("Unknown game.");

        var serverHead = game.HeadVersionId is null
            ? null
            : await _db.SaveVersions.FindAsync(new object?[] { game.HeadVersionId }, ct);

        if (serverHead is not null && serverHead.ContentHash == contentHash)
            return (game, serverHead, false, new UploadResult(UploadStatus.NoChange, serverHead.ToDto(), null));

        var diverged = !force && serverHead is not null && serverHead.Id != parentVersionId;
        return (game, serverHead, diverged, null);
    }

    // ----- Upload (chunked) -----
    //
    // The single-shot UploadAsync above streams the whole archive in one request, which is exactly
    // what fails for a large save over a slow link behind a proxy with a fixed edge timeout (measured
    // against Cyberpunk 2077: Cloudflare kills the connection at ~100s, and a well-played save's
    // archive can take minutes over a modest home upload — see Gotchas.md). This is the same
    // conflict-aware ingest, just fed by bytes that arrived over several short requests instead of
    // one long one. The old route is untouched, for agents that have not updated yet.

    /// <summary>
    /// Start a chunked upload. Short-circuits with NoChange and no session when the content already
    /// matches the head — the one case chunking can pre-empt before a single byte moves. A conflict
    /// still needs the full archive (an admin may choose it later), so only an exact match skips the
    /// wire.
    /// </summary>
    public async Task<(Guid? SessionId, UploadResult? NoChange)> BeginChunkedUploadAsync(
        Guid gameId, Guid machineId, Guid? parentVersionId, string contentHash, bool force,
        CancellationToken ct = default)
    {
        var (_, _, _, noChange) = await PrepareUploadAsync(gameId, parentVersionId, contentHash, force, ct);
        if (noChange is not null) return (null, noChange);

        var versionId = Guid.NewGuid();
        var sessionId = _store.BeginSession(gameId, machineId, versionId, contentHash, parentVersionId, force);
        return (sessionId, null);
    }

    /// <summary>Append one chunk. The session's own machine id is checked against the caller's — a
    /// key that can reach this route at all can only ever touch its own upload.</summary>
    public async Task<long> AppendUploadChunkAsync(
        Guid gameId, Guid machineId, Guid sessionId, long expectedOffset, Stream content,
        CancellationToken ct = default)
    {
        if (!_store.TryGetSession(sessionId, out var info) || info.GameId != gameId || info.MachineId != machineId)
            throw new UnknownUploadSessionException(sessionId);

        return await _store.AppendChunkAsync(sessionId, expectedOffset, content, _maxUploadBytes, ct);
    }

    /// <summary>
    /// Finish a chunked upload: publish the staged file and run it through the same conflict-aware
    /// ingest a single-shot upload gets. Re-checks the head against what it has become since Begin —
    /// a slow chunked transfer widens the window another machine could have pushed in, and this is
    /// the last moment that can still matter before something gets published.
    /// </summary>
    public async Task<UploadResult> CompleteChunkedUploadAsync(
        Guid gameId, Guid machineId, Guid sessionId, CancellationToken ct = default)
    {
        if (!_store.TryGetSession(sessionId, out var info) || info.GameId != gameId || info.MachineId != machineId)
            throw new UnknownUploadSessionException(sessionId);

        var (game, serverHead, diverged, noChange) =
            await PrepareUploadAsync(gameId, info.ParentVersionId, info.ContentHash, info.Force, ct);
        if (noChange is not null)
        {
            // The head caught up to this exact content while the chunks were in flight — nothing to
            // publish.
            _store.AbortSession(sessionId);
            return noChange;
        }

        var (rel, size) = _store.CompleteSession(sessionId);
        try
        {
            return await IngestAsync(game, info.VersionId, rel, size, machineId, info.ParentVersionId,
                info.ContentHash, serverHead, diverged, info.Force, ct);
        }
        catch
        {
            _store.TryDelete(rel);
            throw;
        }
    }

    /// <summary>
    /// The database half of an upload, split out so a failure anywhere in it can un-publish the
    /// archive that was just written. Every exit path here has already been paid for on disk.
    /// </summary>
    private async Task<UploadResult> IngestAsync(
        Game game, Guid versionId, string rel, long size, Guid machineId, Guid? parentVersionId,
        string contentHash, SaveVersion? serverHead, bool diverged, bool force, CancellationToken ct)
    {
        var gameId = game.Id;

        var version = new SaveVersion
        {
            Id = versionId,
            GameId = gameId,
            MachineId = machineId,
            MachineName = await _db.Machines.Where(m => m.Id == machineId)
                .Select(m => m.Name).FirstOrDefaultAsync(ct) ?? "",
            CreatedAt = DateTime.UtcNow,
            ContentHash = contentHash,
            Size = size,
            ParentVersionId = parentVersionId,
            ArchivePath = rel
        };
        _db.SaveVersions.Add(version);

        if (diverged)
        {
            // Check conflict policy before recording a conflict row.
            var autoWins =
                game.ConflictPolicy == ConflictPolicy.NewestWins ||
                (game.ConflictPolicy == ConflictPolicy.PreferMachine &&
                 game.PreferredMachineId.HasValue &&
                 machineId == game.PreferredMachineId);

            if (autoWins)
            {
                // Policy says the incoming version wins — advance head without creating a conflict.
                // The machines this just displaced have to be told: the policy decided against their
                // parent, and nothing in their own sync loop would reveal that until their next push
                // was rejected. Propagating here is the difference between a policy that resolves
                // conflicts and one that merely hides the first of them.
                await SetHeadAndPropagateAsync(game, versionId, "upload.auto_resolved",
                    $"policy={game.ConflictPolicy} {contentHash}",
                    propagate: true, exceptMachineId: machineId, ct);
                await PruneVersionsAsync(gameId, ct);
                await LoadMachine(version, ct);
                return new UploadResult(UploadStatus.Created, version.ToDto(), null);
            }

            var now = DateTime.UtcNow;

            // Fold into the machine's existing open conflict rather than opening another. The head
            // never advances while a conflict is open, so VersionAId is constant and a machine that
            // keeps saving would otherwise write one row per push: a real incident produced 75 rows
            // for a single unresolved divergence, which the console then showed one at a time,
            // oldest first. Keyed per MACHINE so two genuinely diverging machines still get two
            // conflicts. Same shape as HealthService.ApplyEventAsync, and for the same reason.
            var conflict = await _db.Conflicts.FirstOrDefaultAsync(c =>
                c.GameId == gameId &&
                c.Status == ConflictStatus.Open &&
                c.VersionAId == serverHead!.Id &&
                c.MachineId == machineId, ct);

            if (conflict is null)
            {
                conflict = new ConflictFlag
                {
                    Id = Guid.NewGuid(),
                    GameId = gameId,
                    VersionAId = serverHead!.Id,
                    VersionBId = versionId,
                    MachineId = machineId,
                    Status = ConflictStatus.Open,
                    CreatedAt = now,
                    LastSeen = now
                };
                _db.Conflicts.Add(conflict);
            }
            else
            {
                // Carry the NEWEST divergent save as the choice offered. The older ones remain in
                // the version list and can still be promoted with "Set as Latest" — but the newest
                // is what the user has actually been playing, and it is what they almost always
                // want. Offering the oldest is precisely what made the console useless mid-incident.
                conflict.VersionBId = versionId;
                conflict.Count++;
                conflict.LastSeen = now;
            }

            await Audit(machineId, gameId, "upload.conflict", contentHash);
            await _db.SaveChangesAsync(ct);

            // Retention must run here too. It used to be reachable only from the fast-forward path
            // below, so a game stuck in conflict stopped pruning entirely and grew without bound —
            // 80 versions and 2.66 GB on a game configured to keep 5. Safe to call: the protected
            // set already covers the head and both versions of every open conflict.
            await PruneVersionsAsync(gameId, ct);

            await LoadMachine(version, ct);
            return new UploadResult(UploadStatus.Conflict, version.ToDto(), conflict.ToDto());
        }

        // Fast-forward (or forced overwrite). No fan-out: see SetHeadAndPropagateAsync's `propagate`.
        await SetHeadAndPropagateAsync(game, versionId,
            force ? "upload.force" : "upload.create", contentHash,
            propagate: false, exceptMachineId: machineId, ct);

        await PruneVersionsAsync(gameId, ct);

        await LoadMachine(version, ct);
        return new UploadResult(UploadStatus.Created, version.ToDto(), null);
    }

    /// <summary>
    /// Keep only the most recent N versions per game (per-game limit, falling back to the
    /// server global default). Never prunes the current head, open-conflict versions, or versions
    /// an admin protected.
    /// </summary>
    private async Task PruneVersionsAsync(Guid gameId, CancellationToken ct)
    {
        var game = await _db.Games.FindAsync(new object?[] { gameId }, ct);
        var limit = game?.RetainVersions ?? _retainPerGame;
        if (limit <= 0) return;

        var versions = await _db.SaveVersions
            .Where(v => v.GameId == gameId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
        if (versions.Count <= limit) return;

        var protectedIds = new HashSet<Guid>();
        if (game?.HeadVersionId is { } head) protectedIds.Add(head);
        var openConflicts = await _db.Conflicts
            .Where(c => c.GameId == gameId && c.Status == ConflictStatus.Open)
            .ToListAsync(ct);
        foreach (var c in openConflicts) { protectedIds.Add(c.VersionAId); protectedIds.Add(c.VersionBId); }

        var pruned = new List<SaveVersion>();
        foreach (var old in versions.Skip(limit))
        {
            if (old.Protected || protectedIds.Contains(old.Id)) continue;
            _db.SaveVersions.Remove(old);
            pruned.Add(old);
        }
        if (pruned.Count == 0) return;

        // Rows first, files second — see ReleaseArchivesAsync.
        await _db.SaveChangesAsync(ct);
        await ReleaseArchivesAsync(gameId, pruned, ct);
    }

    /// <summary>
    /// Delete the archives behind rows that have <b>already been removed</b> from the database.
    /// <para>
    /// The order is the point. Files-first leaves a live <c>SaveVersion</c> whose archive is gone if
    /// anything fails in between — a save the console still offers and cannot produce, which is
    /// unrecoverable from the user's side. Rows-first leaves at worst an orphaned file: wasted space,
    /// recoverable, and audited here as cleanup debt rather than silently ignored.
    /// </para>
    /// </summary>
    private async Task ReleaseArchivesAsync(Guid gameId, IEnumerable<SaveVersion> removed, CancellationToken ct)
    {
        var orphans = removed.Where(v => !_store.TryDelete(v.ArchivePath)).ToList();
        if (orphans.Count == 0) return;

        await Audit(null, gameId, "archive.orphaned",
            $"{orphans.Count} archive file(s) could not be deleted: " +
            string.Join(", ", orphans.Take(5).Select(v => v.ArchivePath)));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Set (or clear) the per-game version retention limit. Null = use global default.</summary>
    public async Task<bool> SetGameRetentionAsync(Guid gameId, int? retain)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game is null) return false;
        game.RetainVersions = retain;
        await Audit(null, gameId, "game.retention", retain?.ToString() ?? "default");
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Set (or clear) a game's per-game exclude globs. Takes effect on agents' next reconcile.</summary>
    public async Task<bool> SetExcludeGlobsAsync(Guid gameId, IEnumerable<string> patterns)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game is null) return false;

        // The caller replaces the whole list, so the only way to say what actually changed is to
        // diff against what was there before — otherwise the audit trail says "3 pattern(s)" and a
        // user troubleshooting "why did my save stop syncing" has no way to see WHICH pattern did it.
        var before = GlobConfig.Parse(game.ExcludeGlobs).ToHashSet(StringComparer.OrdinalIgnoreCase);
        game.ExcludeGlobs = GlobConfig.Join(patterns);
        var after = GlobConfig.Parse(game.ExcludeGlobs).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = before.Except(after, StringComparer.OrdinalIgnoreCase).ToArray();
        var parts = new List<string>();
        if (added.Length > 0) parts.Add("+" + string.Join(", +", added));
        if (removed.Length > 0) parts.Add("-" + string.Join(", -", removed));

        await Audit(null, gameId, "game.excludes", parts.Count > 0 ? string.Join("; ", parts) : "no change");
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Set the conflict resolution policy for a game. For <see cref="ConflictPolicy.PreferMachine"/>,
    /// <paramref name="preferredMachineId"/> must identify an existing machine.
    /// Clears <c>PreferredMachineId</c> when switching away from that policy.
    /// </summary>
    public async Task<bool> SetConflictPolicyAsync(Guid gameId, ConflictPolicy policy, Guid? preferredMachineId)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game is null) return false;

        if (policy == ConflictPolicy.PreferMachine && preferredMachineId.HasValue)
        {
            if (await _db.Machines.FindAsync(preferredMachineId.Value) is null) return false;
        }

        game.ConflictPolicy = policy;
        game.PreferredMachineId = policy == ConflictPolicy.PreferMachine ? preferredMachineId : null;

        await Audit(null, gameId, "game.conflict_policy",
            policy == ConflictPolicy.PreferMachine && preferredMachineId.HasValue
                ? $"{policy}:{preferredMachineId}"
                : policy.ToString());
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Delete a single version by id. Refuses to delete the current head or any version
    /// referenced by an open conflict. Returns (true, null) on success or (false, reason).
    /// </summary>
    public async Task<(bool ok, string? error)> DeleteVersionAsync(Guid gameId, Guid versionId)
    {
        var version = await _db.SaveVersions.FindAsync(versionId);
        if (version is null || version.GameId != gameId) return (false, "not_found");

        var game = await _db.Games.FindAsync(gameId);
        if (game?.HeadVersionId == versionId) return (false, "Cannot delete the current head version.");

        var inConflict = await _db.Conflicts.AnyAsync(c =>
            c.GameId == gameId && c.Status == ConflictStatus.Open &&
            (c.VersionAId == versionId || c.VersionBId == versionId));
        if (inConflict) return (false, "Cannot delete a version that is part of an open conflict.");

        _db.SaveVersions.Remove(version);
        await Audit(null, gameId, "version.delete", versionId.ToString());
        await _db.SaveChangesAsync();
        await ReleaseArchivesAsync(gameId, new[] { version }, default);
        return (true, null);
    }

    /// <summary>Protect or unprotect a version from automatic retention.</summary>
    public async Task<(bool ok, string? error)> SetVersionProtectedAsync(
        Guid gameId, Guid versionId, bool value)
    {
        var version = await _db.SaveVersions.FindAsync(versionId);
        if (version is null || version.GameId != gameId) return (false, "not_found");

        version.Protected = value;
        await Audit(null, gameId, value ? "version.protect" : "version.unprotect", versionId.ToString());
        await _db.SaveChangesAsync();
        return (true, null);
    }

    // ----- Download -----

    public async Task<(SaveVersion version, Stream content)?> DownloadHeadAsync(Guid gameId)
    {
        var game = await _db.Games.FindAsync(gameId);
        if (game?.HeadVersionId is null) return null;
        return await DownloadVersionAsync(game.HeadVersionId.Value);
    }

    public async Task<(SaveVersion version, Stream content)?> DownloadVersionAsync(Guid versionId)
    {
        var version = await _db.SaveVersions.Include(v => v.Machine)
            .FirstOrDefaultAsync(v => v.Id == versionId);
        if (version is null || !_store.Exists(version.ArchivePath)) return null;
        return (version, _store.OpenRead(version.ArchivePath));
    }

    // ----- Admin: conflicts & rollback -----

    /// <summary>
    /// Every open conflict, <b>most recently active first</b>.
    ///
    /// It used to be oldest-first, and the console took the first match per game — so it reliably
    /// surfaced the <i>least</i> useful one. During the 2026-07-22 incident that meant being offered
    /// a pair of day-old saves while the save actually being played sat in a conflict the UI never
    /// showed. Dedupe (0.1) makes this mostly moot by collapsing a game to one row per machine, but
    /// the ordering was wrong on its own merits.
    /// </summary>
    public async Task<List<ConflictDto>> ListOpenConflictsAsync()
    {
        var now = DateTime.UtcNow;
        var conflicts = await _db.Conflicts.Where(c => c.Status == ConflictStatus.Open)
            .OrderByDescending(c => c.LastSeen)
            .ThenByDescending(c => c.CreatedAt)
            .ToListAsync();
        return conflicts
            .Select(c => c.ToDto(_conflictEscalation.IsEscalated(c, now)))
            .ToList();
    }

    /// <summary>
    /// Admin: apply this game's retention limit right now, without waiting for a push to trigger it.
    /// Returns how many versions were removed.
    /// <para>
    /// Retention otherwise only runs as a side effect of an upload, so a game that has stopped being
    /// played keeps whatever it accumulated — and recovering from that needed shell access to the
    /// admin API. Deliberately reuses the same <see cref="PruneVersionsAsync"/> the upload path calls,
    /// so "Prune now" and automatic pruning can never disagree about what is safe to delete.
    /// </para>
    /// </summary>
    public async Task<int> PruneNowAsync(Guid gameId)
    {
        var before = await _db.SaveVersions.CountAsync(v => v.GameId == gameId);
        await PruneVersionsAsync(gameId, CancellationToken.None);
        return before - await _db.SaveVersions.CountAsync(v => v.GameId == gameId);
    }

    /// <summary>
    /// Resolve a conflict by promoting the chosen version to head. Refuses to replace a newer head
    /// with an older conflict option.
    /// </summary>
    public async Task<(bool ok, string? error)> ResolveConflictAsync(
        Guid conflictId,
        Guid winningVersionId,
        string resolvedBy,
        bool keepBoth = false)
    {
        var conflict = await _db.Conflicts.FindAsync(conflictId);
        if (conflict is null || conflict.Status != ConflictStatus.Open)
            return (false, "Conflict is no longer open.");
        if (winningVersionId != conflict.VersionAId && winningVersionId != conflict.VersionBId)
            return (false, "The chosen version is not part of this conflict.");

        var game = await _db.Games.FindAsync(conflict.GameId);
        var winner = await _db.SaveVersions.FindAsync(winningVersionId);
        if (game is null || winner is null)
            return (false, "The game or chosen version no longer exists.");

        var losingVersionId = winningVersionId == conflict.VersionAId ? conflict.VersionBId : conflict.VersionAId;
        var loser = await _db.SaveVersions.FindAsync(losingVersionId);

        var currentHead = game.HeadVersionId is { } headId
            ? await _db.SaveVersions.FindAsync(headId)
            : null;
        if (currentHead is not null &&
            currentHead.Id != winner.Id &&
            winner.CreatedAt < currentHead.CreatedAt)
        {
            await Audit(null, conflict.GameId, "conflict.resolve_rewind_blocked",
                $"winner={winner.Id} currentHead={currentHead.Id}");
            await _db.SaveChangesAsync();
            return (false,
                "Latest changed after this conflict opened. Resolving to that older save would rewind it; review the current versions first.");
        }

        if (keepBoth)
        {
            var versions = await _db.SaveVersions
                .Where(v => v.Id == conflict.VersionAId || v.Id == conflict.VersionBId)
                .ToListAsync();
            if (versions.Count != 2)
                return (false, "One of the conflict versions no longer exists.");
            foreach (var version in versions) version.Protected = true;
        }

        conflict.Status = ConflictStatus.Resolved;
        conflict.ResolvedVersionId = winningVersionId;
        conflict.ResolvedBy = resolvedBy;
        conflict.ResolvedAt = DateTime.UtcNow;
        // Named by uploading machine, not just version IDs — the whole point of this detail string
        // is that "the wrong side was picked" is investigable after the fact without a DB query.
        var resolveDetail =
            $"resolved by {resolvedBy}: kept {winner.MachineName}'s version ({winner.CreatedAt:O})" +
            (loser is not null
                ? $", {(keepBoth ? "also kept" : "discarded")} {loser.MachineName}'s version ({loser.CreatedAt:O})"
                : "");

        // Propagation is queued below rather than here, for the ordering reason spelled out there.
        await SetHeadAndPropagateAsync(game, winningVersionId,
            keepBoth ? "conflict.resolve_keep_both" : "conflict.resolve",
            resolveDetail,
            propagate: false, exceptMachineId: null, CancellationToken.None);

        // ORDER MATTERS, and the obvious order is wrong. Queue the pulls FIRST: resolving unpins
        // both of the conflict's versions, so pruning here can legitimately delete the losing one —
        // and QueueResolutionPullsAsync identifies the machines to notify by looking those versions
        // up. Prune first and the loser's row is gone before anyone asks who owned it, so only the
        // winner gets told and the loser stays stuck. Caught by run-agent-tests, not by review.
        await QueueResolutionPullsAsync(conflict);

        // Resolution is the first moment a pile of versions stops being pinned by an open conflict,
        // so it is the first moment retention can actually act on them.
        await PruneVersionsAsync(conflict.GameId, CancellationToken.None);
        return (true, null);
    }

    /// <summary>
    /// Tell both machines in a resolved conflict to pull, so the resolution actually reaches them.
    ///
    /// Resolving used to be a database edit that merely looked like an action. An agent's parent
    /// version advances only on a successful push or a pull, and the upload path deliberately does
    /// NOT advance it on conflict — so both machines stayed behind the new head and conflicted again
    /// on their very next save. The console said resolved; the fleet disagreed.
    ///
    /// <para>
    /// The <b>winner</b> is the counter-intuitive half, and the one that bit hardest in practice: its
    /// content is already byte-identical to the new head, but its pointer still names the parent it
    /// presented, so its next push is rejected exactly like the loser's.
    /// </para>
    ///
    /// <para>
    /// The pull is deliberately <b>unforced</b>. What that means differs per machine, and every
    /// outcome is the right one:
    /// <list type="bullet">
    /// <item>the winner's content already matches the head, so the pull short-circuits before
    /// touching a single file and just repairs the pointer;</item>
    /// <item>a loser that had cleanly synced its version has nothing unpushed, so the pull restores
    /// the winner over it;</item>
    /// <item>a loser carrying local changes made since is <b>blocked</b>, and says so on the console.
    /// That is the honest answer — a forced pull here would silently destroy work the server has
    /// never seen, which is the failure class <c>Decisions.md</c> §9 and v0.3.2 already fought twice.
    /// Note the console's own Pull button sends <c>force: true</c>; this deliberately does not.</item>
    /// </list>
    /// </para>
    /// </summary>
    private Task QueueResolutionPullsAsync(ConflictFlag conflict) =>
        // Every live machine that syncs the game, not just the conflict's two: the same head change
        // displaces a third machine sitting on the old parent exactly as much as it displaces the
        // loser. QueueHeadPullsAsync already dedupes and already skips machines that are gone —
        // DeleteMachineAsync keeps a deleted machine's versions as history with a null MachineId,
        // and queueing a command for one would violate the foreign key.
        QueueHeadPullsAsync(conflict.GameId, exceptMachineId: null, CancellationToken.None);

    /// <summary>Admin: move the head pointer to an earlier version (rollback).</summary>
    public Task<bool> RollbackAsync(Guid gameId, Guid versionId, string by) =>
        SetHeadAsync(gameId, versionId, "rollback", by);

    /// <summary>
    /// Admin: designate a chosen version as the authoritative "Latest" agents pull
    /// (the same head-pointer move as rollback, used by the initial-sync wizard and
    /// the "Set as Latest" action — see "Latest" nomenclature in Decisions).
    /// </summary>
    public Task<bool> SetAsLatestAsync(Guid gameId, Guid versionId, string by) =>
        SetHeadAsync(gameId, versionId, "set_latest", by);

    /// <summary>
    /// The admin head move behind both rollback and Set as Latest.
    /// <para>
    /// Both used to be a pointer write and nothing else, while the console promised every machine
    /// would pull the chosen save. Nothing told them: an agent's parent advances only on its own
    /// push or pull, so the fleet stayed on the previous head and conflicted on its next save.
    /// </para>
    /// </summary>
    private async Task<bool> SetHeadAsync(Guid gameId, Guid versionId, string action, string by)
    {
        var game = await _db.Games.FindAsync(gameId);
        var version = await _db.SaveVersions.FindAsync(versionId);
        if (game is null || version is null || version.GameId != gameId) return false;

        // Close the conflicts this choice actually decides — the ones offering the chosen version.
        // Leaving those open would have the console insist the very version it was just told to
        // trust is unresolved. Conflicts between two OTHER versions stay open on purpose: the admin
        // has said nothing about them, their machine is still stuck, and closing them here would
        // also disarm the guard that refuses to resolve a conflict in a way that rewinds a newer
        // Latest (run-agent-tests covers exactly that sequence). Audited one by one, so this is
        // visible rather than a quiet side effect of clicking Set as Latest.
        var superseded = await _db.Conflicts
            .Where(c => c.GameId == gameId && c.Status == ConflictStatus.Open
                        && (c.VersionAId == versionId || c.VersionBId == versionId))
            .ToListAsync();
        foreach (var c in superseded)
        {
            c.Status = ConflictStatus.Resolved;
            c.ResolvedVersionId = versionId;
            c.ResolvedBy = by;
            c.ResolvedAt = DateTime.UtcNow;
            await Audit(c.MachineId, gameId, "conflict.resolve_superseded",
                $"{action} to {versionId} by {by}");
        }

        await SetHeadAndPropagateAsync(game, versionId, action, $"{versionId} by {by}",
            propagate: true, exceptMachineId: null, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// The one place <see cref="Game.HeadVersionId"/> moves. Upload, automatic conflict policy,
    /// manual resolution, rollback and Set as Latest all come through here, so "who gets told about
    /// a new head" is one rule rather than four that drifted apart.
    /// </summary>
    /// <param name="propagate">
    /// Whether to queue pulls for the game's other machines. False for an ordinary push: the
    /// uploader learns the new head from its own upload response, and every other machine is
    /// deliberately left to discover it through its normal pre-launch pull. Fanning commands out on
    /// every save would mean a pull arriving while a game is running, which is the failure the
    /// lease and settle gate exist to avoid. It is true where the server made a decision the fleet
    /// could not otherwise learn: an automatic conflict policy, a manual resolution, or an admin
    /// naming Latest.
    /// </param>
    /// <param name="exceptMachineId">
    /// The machine that already knows — the uploader whose own response carried the new head. A
    /// redundant command for it would be a no-op pull, but it would also be noise in the console's
    /// command list at exactly the moment an admin is trying to read it.
    /// </param>
    private async Task SetHeadAndPropagateAsync(
        Game game, Guid newHeadId, string action, string detail,
        bool propagate, Guid? exceptMachineId, CancellationToken ct)
    {
        game.HeadVersionId = newHeadId;
        await Audit(exceptMachineId, game.Id, action, detail);
        await _db.SaveChangesAsync(ct);

        if (propagate) await QueueHeadPullsAsync(game.Id, exceptMachineId, ct);
    }

    /// <summary>
    /// Queue a deduplicated, <b>unforced</b> Pull for every live machine that syncs this game, so a
    /// server-authoritative head change actually reaches the fleet.
    /// <para>
    /// Unforced is the safety property, and it is deliberate (see
    /// <see cref="QueueResolutionPullsAsync"/>): a machine that is already current no-ops, a machine
    /// with nothing unpushed takes the new head, and a machine carrying unsynced local work reports
    /// <b>blocked</b> instead of having that work destroyed. The console's own Pull button is the
    /// forced one, because there a human is asking for exactly that.
    /// </para>
    /// </summary>
    private async Task QueueHeadPullsAsync(Guid gameId, Guid? exceptMachineId, CancellationToken ct)
    {
        // "Syncs this game" = has a mapped save folder for it, or has uploaded a version of it.
        // Two queries and a set rather than a Union: this has to survive a version whose machine
        // was deleted (MachineId is null) without dragging that case through the translation.
        var candidates = new HashSet<Guid>(
            await _db.MachineSavePaths.Where(p => p.GameId == gameId)
                .Select(p => p.MachineId).ToListAsync(ct));
        foreach (var id in await _db.SaveVersions
                     .Where(v => v.GameId == gameId && v.MachineId != null)
                     .Select(v => v.MachineId!.Value).Distinct().ToListAsync(ct))
            candidates.Add(id);

        if (exceptMachineId is { } skip) candidates.Remove(skip);
        if (candidates.Count == 0) return;

        // Only machines that still exist: a command for a deleted one violates the foreign key.
        var live = await _db.Machines
            .Where(m => candidates.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync(ct);

        foreach (var machineId in live)
        {
            // One pull brings an agent to the current head no matter how many head changes it
            // missed, so a pending pull is already the whole answer.
            var alreadyQueued = await _db.AgentCommands.AnyAsync(c =>
                c.MachineId == machineId &&
                c.GameId == gameId &&
                c.Type == AgentCommandType.Pull &&
                c.Status == CommandStatus.Pending, ct);
            if (alreadyQueued) continue;

            await EnqueueCommandAsync(new EnqueueCommandRequest(
                machineId, gameId, AgentCommandType.Pull, Force: false));
        }
    }

    public async Task<List<SaveVersion>> ListVersionsAsync(Guid gameId) =>
        await _db.SaveVersions.Include(v => v.Machine)
            .Where(v => v.GameId == gameId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync();

    // ----- Agent command channel -----

    /// <summary>Dashboard: queue a command for an agent to run on its next poll.</summary>
    public async Task<AgentCommand> EnqueueCommandAsync(EnqueueCommandRequest req)
    {
        var cmd = new AgentCommand
        {
            Id = Guid.NewGuid(),
            MachineId = req.MachineId,
            GameId = req.GameId,
            Type = req.Type,
            Force = req.Force,
            Status = CommandStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        _db.AgentCommands.Add(cmd);
        await Audit(req.MachineId, req.GameId, "command.enqueue", req.Type.ToString());
        await _db.SaveChangesAsync();
        return await _db.AgentCommands.Include(c => c.Machine).FirstAsync(c => c.Id == cmd.Id);
    }

    /// <summary>
    /// Agent: claim this machine's due commands under a <b>visibility lease</b>.
    /// <para>
    /// Delivery is at-least-once, not at-most-once. A command is due when it is Pending, or when it
    /// is Dispatched and its lease has run out unacknowledged — an agent that dies between the poll
    /// and the result no longer takes the command with it. Completed and failed commands are
    /// terminal and never reappear.
    /// </para>
    /// <para>
    /// The claim is one atomic UPDATE stamped with a fresh <see cref="AgentCommand.ClaimToken"/>,
    /// then a read of exactly the rows carrying that token. The old read-then-update could hand the
    /// same command to two processes polling with one machine identity.
    /// </para>
    /// <para>
    /// Re-delivery is safe for every command type, which is why at-least-once is the right trade
    /// here: <c>Pull</c> re-reads the current head and either no-ops or reports blocked, <c>Push</c>
    /// re-hashes the local save and the server answers <c>NoChange</c> when the content already
    /// matches the head (so a retry cannot manufacture a duplicate version), <c>Sync</c> is those
    /// two in order, and <c>Scan</c> only reads. No retry turns a safe command into a destructive
    /// one; losing a requested sync silently, which is what the old code did, is the worse failure.
    /// </para>
    /// </summary>
    public async Task<List<AgentCommand>> DequeueCommandsAsync(Guid machineId)
    {
        var now = DateTime.UtcNow;
        var token = Guid.NewGuid();

        var claimed = await _db.AgentCommands
            .Where(c => c.MachineId == machineId
                        && (c.Status == CommandStatus.Pending
                            || (c.Status == CommandStatus.Dispatched
                                && c.LeaseExpiresAt != null
                                && c.LeaseExpiresAt < now)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, CommandStatus.Dispatched)
                .SetProperty(c => c.DispatchedAt, now)
                .SetProperty(c => c.LeaseExpiresAt, now + _commandLease)
                .SetProperty(c => c.ClaimToken, token)
                .SetProperty(c => c.ClaimCount, c => c.ClaimCount + 1));

        if (claimed == 0) return new List<AgentCommand>();

        var commands = await _db.AgentCommands
            .Where(c => c.ClaimToken == token)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        // A reclaim is the interesting event: it means an earlier delivery was never answered.
        foreach (var c in commands.Where(c => c.ClaimCount > 1))
            await Audit(machineId, c.GameId, "command.reclaim",
                $"{c.Type}: lease expired, delivery #{c.ClaimCount}");
        await _db.SaveChangesAsync();

        return commands;
    }

    /// <summary>
    /// Agent: report a command's outcome. Terminal states stick — a duplicate or late result for a
    /// command already completed is accepted as a no-op rather than reopening it, so an agent
    /// retrying a lost POST does not have to reason about what happened server-side.
    /// </summary>
    public async Task<bool> CompleteCommandAsync(Guid commandId, Guid machineId, CommandStatus status, string? result)
    {
        var cmd = await _db.AgentCommands.FindAsync(commandId);
        if (cmd is null || cmd.MachineId != machineId) return false;
        if (cmd.Status is CommandStatus.Done or CommandStatus.Failed) return true;

        cmd.Status = status == CommandStatus.Failed ? CommandStatus.Failed : CommandStatus.Done;
        cmd.Result = result;
        cmd.CompletedAt = DateTime.UtcNow;
        cmd.LeaseExpiresAt = null;
        cmd.ClaimToken = null;
        await Audit(machineId, cmd.GameId, "command.complete", $"{cmd.Type}: {status}");
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Dashboard: recent commands across all machines, newest first.</summary>
    public async Task<List<AgentCommand>> ListCommandsAsync(int take = 50) =>
        await _db.AgentCommands.Include(c => c.Machine)
            .OrderByDescending(c => c.CreatedAt)
            .Take(take)
            .ToListAsync();

    // ----- helpers -----

    private async Task LoadMachine(SaveVersion v, CancellationToken ct)
    {
        if (v.Machine is null && v.MachineId is not null)
            v.Machine = await _db.Machines.FindAsync(new object?[] { v.MachineId.Value }, ct);
    }

    public async Task<List<AuditEntryDto>> GetAuditLogAsync(int limit = 200)
    {
        return await (
            from a in _db.AuditLogs
            join m in _db.Machines on a.MachineId equals m.Id into ms
            from m in ms.DefaultIfEmpty()
            join g in _db.Games on a.GameId equals g.Id into gs
            from g in gs.DefaultIfEmpty()
            orderby a.Timestamp descending
            select new AuditEntryDto(a.Id, a.Timestamp, a.MachineId, m.Name, a.GameId, g.Name, a.Action, a.Detail)
        ).Take(limit).ToListAsync();
    }

    /// <summary>Machine- and game-less audit entry, for events with no natural owner of either kind
    /// (agent installer packages). Saves immediately — unlike the private overload, callers here are
    /// not already inside a larger unit of work that saves for them.</summary>
    public async Task LogAuditAsync(string action, string? detail)
    {
        await Audit(null, null, action, detail);
        await _db.SaveChangesAsync();
    }

    private Task Audit(Guid? machineId, Guid? gameId, string action, string? detail)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            MachineId = machineId,
            GameId = gameId,
            Action = action,
            Detail = detail
        });
        return Task.CompletedTask;
    }
}
