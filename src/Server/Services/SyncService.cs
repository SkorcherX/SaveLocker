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
    private readonly TimeSpan _leaseDuration = TimeSpan.FromHours(6);
    private readonly int _retainPerGame;

    public SyncService(AppDbContext db, ArchiveStore store, IConfiguration config)
    {
        _db = db;
        _store = store;
        _retainPerGame = config.GetValue<int?>("Storage:RetainVersionsPerGame") ?? 10;
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
        foreach (var v in versions) _store.Delete(v.ArchivePath);
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

    public async Task<Lease?> ActiveLeaseAsync(Guid gameId)
    {
        var lease = await _db.Leases.Include(l => l.Machine)
            .FirstOrDefaultAsync(l => l.GameId == gameId);
        if (lease is null) return null;
        if (lease.ExpiresAt < DateTime.UtcNow)
        {
            _db.Leases.Remove(lease);
            await _db.SaveChangesAsync();
            return null;
        }
        return lease;
    }

    public async Task<LeaseAcquireResponse> AcquireLeaseAsync(Guid gameId, Guid machineId)
    {
        var current = await ActiveLeaseAsync(gameId);
        if (current is not null && current.MachineId != machineId)
            return new LeaseAcquireResponse(false, current.ToDto(gameId));

        if (current is null)
        {
            current = new Lease { Id = Guid.NewGuid(), GameId = gameId, MachineId = machineId };
            _db.Leases.Add(current);
        }
        current.AcquiredAt = DateTime.UtcNow;
        current.ExpiresAt = DateTime.UtcNow.Add(_leaseDuration);
        await Audit(machineId, gameId, "lease.acquire", null);
        await _db.SaveChangesAsync();

        current = await _db.Leases.Include(l => l.Machine).FirstAsync(l => l.GameId == gameId);
        return new LeaseAcquireResponse(true, current.ToDto(gameId));
    }

    public async Task ReleaseLeaseAsync(Guid gameId, Guid machineId)
    {
        var lease = await _db.Leases.FirstOrDefaultAsync(l => l.GameId == gameId);
        if (lease is not null && lease.MachineId == machineId)
        {
            _db.Leases.Remove(lease);
            await Audit(machineId, gameId, "lease.release", null);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<bool> RenewLeaseAsync(Guid gameId, Guid machineId)
    {
        var lease = await _db.Leases.FirstOrDefaultAsync(l => l.GameId == gameId);
        if (lease is null || lease.MachineId != machineId) return false;
        lease.ExpiresAt = DateTime.UtcNow.Add(_leaseDuration);
        await Audit(machineId, gameId, "lease.renew", null);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Admin: force-release a stuck lease regardless of holder.</summary>
    public async Task ForceReleaseLeaseAsync(Guid gameId)
    {
        var lease = await _db.Leases.FirstOrDefaultAsync(l => l.GameId == gameId);
        if (lease is not null)
        {
            _db.Leases.Remove(lease);
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
        var expired = await _db.Leases
            .Where(l => l.ExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        if (expired.Count == 0) return 0;
        _db.Leases.RemoveRange(expired);
        await _db.SaveChangesAsync();
        return expired.Count;
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
        var game = await _db.Games.FindAsync(new object?[] { gameId }, ct)
                   ?? throw new InvalidOperationException("Unknown game.");

        var serverHead = game.HeadVersionId is null
            ? null
            : await _db.SaveVersions.FindAsync(new object?[] { game.HeadVersionId }, ct);

        // No-op if the content already matches the head.
        if (serverHead is not null && serverHead.ContentHash == contentHash)
            return new UploadResult(UploadStatus.NoChange, serverHead.ToDto(), null);

        var diverged = !force
                       && serverHead is not null
                       && serverHead.Id != parentVersionId;

        // Persist the incoming archive as a new version regardless of outcome,
        // so the admin can choose it during conflict resolution.
        var versionId = Guid.NewGuid();
        var (rel, size) = await _store.SaveAsync(gameId, versionId, archive, ct);

        var version = new SaveVersion
        {
            Id = versionId,
            GameId = gameId,
            MachineId = machineId,
            CreatedAt = DateTime.UtcNow,
            ContentHash = contentHash,
            Size = size,
            ParentVersionId = parentVersionId,
            ArchivePath = rel
        };
        _db.SaveVersions.Add(version);

        if (diverged)
        {
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

        // Fast-forward (or forced overwrite).
        game.HeadVersionId = versionId;
        await Audit(machineId, gameId, force ? "upload.force" : "upload.create", contentHash);
        await _db.SaveChangesAsync(ct);

        await PruneVersionsAsync(gameId, ct);

        await LoadMachine(version, ct);
        return new UploadResult(UploadStatus.Created, version.ToDto(), null);
    }

    /// <summary>
    /// Keep only the most recent N versions per game (per-game limit, falling back to the
    /// server global default). Never prunes the current head or open-conflict versions.
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

        foreach (var old in versions.Skip(limit))
        {
            if (protectedIds.Contains(old.Id)) continue;
            _store.Delete(old.ArchivePath);
            _db.SaveVersions.Remove(old);
        }
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
        game.ExcludeGlobs = GlobConfig.Join(patterns);
        await Audit(null, gameId, "game.excludes", GlobConfig.Parse(game.ExcludeGlobs).Length + " pattern(s)");
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

        _store.Delete(version.ArchivePath);
        _db.SaveVersions.Remove(version);
        await Audit(null, gameId, "version.delete", versionId.ToString());
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

    public async Task<List<ConflictFlag>> ListOpenConflictsAsync() =>
        await _db.Conflicts.Where(c => c.Status == ConflictStatus.Open)
            .OrderBy(c => c.CreatedAt).ToListAsync();

    /// <summary>Resolve a conflict by promoting the chosen version to head.</summary>
    public async Task<bool> ResolveConflictAsync(Guid conflictId, Guid winningVersionId, string resolvedBy)
    {
        var conflict = await _db.Conflicts.FindAsync(conflictId);
        if (conflict is null || conflict.Status != ConflictStatus.Open) return false;
        if (winningVersionId != conflict.VersionAId && winningVersionId != conflict.VersionBId)
            return false;

        var game = await _db.Games.FindAsync(conflict.GameId);
        if (game is null) return false;

        game.HeadVersionId = winningVersionId;
        conflict.Status = ConflictStatus.Resolved;
        conflict.ResolvedVersionId = winningVersionId;
        conflict.ResolvedBy = resolvedBy;
        conflict.ResolvedAt = DateTime.UtcNow;
        await Audit(null, conflict.GameId, "conflict.resolve", winningVersionId.ToString());
        await _db.SaveChangesAsync();

        // ORDER MATTERS, and the obvious order is wrong. Queue the pulls FIRST: resolving unpins
        // both of the conflict's versions, so pruning here can legitimately delete the losing one —
        // and QueueResolutionPullsAsync identifies the machines to notify by looking those versions
        // up. Prune first and the loser's row is gone before anyone asks who owned it, so only the
        // winner gets told and the loser stays stuck. Caught by run-agent-tests, not by review.
        await QueueResolutionPullsAsync(conflict);

        // Resolution is the first moment a pile of versions stops being pinned by an open conflict,
        // so it is the first moment retention can actually act on them.
        await PruneVersionsAsync(conflict.GameId, CancellationToken.None);
        return true;
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
    private async Task QueueResolutionPullsAsync(ConflictFlag conflict)
    {
        var versionIds = new[] { conflict.VersionAId, conflict.VersionBId };

        // Joined against Machines on purpose: DeleteMachineAsync keeps a deleted machine's versions
        // as history, so a version's MachineId can name a machine that no longer exists — and
        // queueing a command for it would violate the foreign key.
        var machineIds = await (
            from v in _db.SaveVersions
            join m in _db.Machines on v.MachineId equals m.Id
            where versionIds.Contains(v.Id)
            select v.MachineId).Distinct().ToListAsync();

        foreach (var machineId in machineIds)
        {
            // An admin clearing several conflicts in a row must not queue a pull per click. One pull
            // brings the agent to the current head regardless of how many were resolved.
            var alreadyQueued = await _db.AgentCommands.AnyAsync(c =>
                c.MachineId == machineId &&
                c.GameId == conflict.GameId &&
                c.Type == AgentCommandType.Pull &&
                c.Status == CommandStatus.Pending);
            if (alreadyQueued) continue;

            await EnqueueCommandAsync(new EnqueueCommandRequest(
                machineId, conflict.GameId, AgentCommandType.Pull, Force: false));
        }
    }

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

    private async Task<bool> SetHeadAsync(Guid gameId, Guid versionId, string action, string by)
    {
        var game = await _db.Games.FindAsync(gameId);
        var version = await _db.SaveVersions.FindAsync(versionId);
        if (game is null || version is null || version.GameId != gameId) return false;

        game.HeadVersionId = versionId;
        await Audit(null, gameId, action, $"{versionId} by {by}");
        await _db.SaveChangesAsync();
        return true;
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
    /// Agent: claim this machine's pending commands, marking them Dispatched so a
    /// later poll won't run them again before a result is reported.
    /// </summary>
    public async Task<List<AgentCommand>> DequeueCommandsAsync(Guid machineId)
    {
        var pending = await _db.AgentCommands
            .Where(c => c.MachineId == machineId && c.Status == CommandStatus.Pending)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync();

        foreach (var c in pending)
        {
            c.Status = CommandStatus.Dispatched;
            c.DispatchedAt = DateTime.UtcNow;
        }
        if (pending.Count > 0) await _db.SaveChangesAsync();
        return pending;
    }

    /// <summary>Agent: report a command's outcome.</summary>
    public async Task<bool> CompleteCommandAsync(Guid commandId, Guid machineId, CommandStatus status, string? result)
    {
        var cmd = await _db.AgentCommands.FindAsync(commandId);
        if (cmd is null || cmd.MachineId != machineId) return false;
        cmd.Status = status == CommandStatus.Failed ? CommandStatus.Failed : CommandStatus.Done;
        cmd.Result = result;
        cmd.CompletedAt = DateTime.UtcNow;
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
        if (v.Machine is null)
            v.Machine = await _db.Machines.FindAsync(new object?[] { v.MachineId }, ct);
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
