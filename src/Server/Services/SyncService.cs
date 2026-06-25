using LocalGameSync.Server.Data;
using LocalGameSync.Shared;
using Microsoft.EntityFrameworkCore;

namespace LocalGameSync.Server.Services;

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
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM MachineSavePaths WHERE MachineId = {machineId}");
        _db.Machines.Remove(machine);
        await Audit(null, null, "machine.delete", machine.Name);
        await _db.SaveChangesAsync();
        return true;
    }

    // ----- Machine save paths -----

    private record GamePathResult(string GameId, string SavePath);
    private record MachinePathResult(string MachineId, string MachineName, string SavePath);

    /// <summary>All stored save paths for a game, one row per machine that has one.</summary>
    public async Task<List<MachineSavePathDto>> GetGameMachinePathsAsync(Guid gameId)
    {
        var rows = await _db.Database.SqlQuery<MachinePathResult>($"""
            SELECT p.MachineId, m.Name AS MachineName, p.SavePath
            FROM MachineSavePaths p
            JOIN Machines m ON m.Id = p.MachineId
            WHERE p.GameId = {gameId}
            ORDER BY m.Name
            """).ToListAsync();
        return rows.Select(r => new MachineSavePathDto(Guid.Parse(r.MachineId), r.MachineName, r.SavePath)).ToList();
    }

    /// <summary>All stored save paths for one machine, keyed by game ID (for reconcile injection).</summary>
    public async Task<Dictionary<Guid, string>> GetMachinePathMapAsync(Guid machineId)
    {
        var rows = await _db.Database.SqlQuery<GamePathResult>(
            $"SELECT GameId, SavePath FROM MachineSavePaths WHERE MachineId = {machineId}")
            .ToListAsync();
        return rows.ToDictionary(r => Guid.Parse(r.GameId), r => r.SavePath);
    }

    /// <summary>Upsert a machine's save path for a game (agent-reported or dashboard-set).</summary>
    public async Task SetMachinePathAsync(Guid machineId, Guid gameId, string path)
    {
        await _db.Database.ExecuteSqlAsync(
            $"INSERT OR REPLACE INTO MachineSavePaths (MachineId, GameId, SavePath) VALUES ({machineId}, {gameId}, {path})");
        await Audit(machineId, gameId, "machine_path.set", path);
        await _db.SaveChangesAsync();
    }

    /// <summary>Remove a machine's stored save path for a game.</summary>
    public async Task ClearMachinePathAsync(Guid machineId, Guid gameId)
    {
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM MachineSavePaths WHERE MachineId = {machineId} AND GameId = {gameId}");
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
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM MachineSavePaths WHERE GameId = {gameId}");
        _db.Games.Remove(game);
        await Audit(null, gameId, "game.delete", game.Name);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>All games with their current state, for the dashboard overview.</summary>
    public async Task<List<GameStateDto>> GetOverviewAsync()
    {
        var games = await _db.Games.OrderBy(g => g.Name).ToListAsync();
        var result = new List<GameStateDto>(games.Count);
        foreach (var g in games)
        {
            var state = await GetGameStateAsync(g.Id);
            if (state is not null) result.Add(state);
        }
        return result;
    }

    public async Task<GameStateDto?> GetGameStateAsync(Guid gameId)
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

        return new GameStateDto(game.ToDto(), head?.ToDto(), lease.ToDto(gameId), hasConflict);
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
            var conflict = new ConflictFlag
            {
                Id = Guid.NewGuid(),
                GameId = gameId,
                VersionAId = serverHead!.Id,
                VersionBId = versionId,
                Status = ConflictStatus.Open,
                CreatedAt = DateTime.UtcNow
            };
            _db.Conflicts.Add(conflict);
            await Audit(machineId, gameId, "upload.conflict", contentHash);
            await _db.SaveChangesAsync(ct);

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
    /// Keep only the most recent <c>RetainVersionsPerGame</c> versions per game,
    /// deleting older archives to bound storage. Never prunes the current head or
    /// versions referenced by an open conflict.
    /// </summary>
    private async Task PruneVersionsAsync(Guid gameId, CancellationToken ct)
    {
        if (_retainPerGame <= 0) return;

        var game = await _db.Games.FindAsync(new object?[] { gameId }, ct);
        var versions = await _db.SaveVersions
            .Where(v => v.GameId == gameId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
        if (versions.Count <= _retainPerGame) return;

        var protectedIds = new HashSet<Guid>();
        if (game?.HeadVersionId is { } head) protectedIds.Add(head);
        var openConflicts = await _db.Conflicts
            .Where(c => c.GameId == gameId && c.Status == ConflictStatus.Open)
            .ToListAsync(ct);
        foreach (var c in openConflicts) { protectedIds.Add(c.VersionAId); protectedIds.Add(c.VersionBId); }

        foreach (var old in versions.Skip(_retainPerGame))
        {
            if (protectedIds.Contains(old.Id)) continue;
            _store.Delete(old.ArchivePath);
            _db.SaveVersions.Remove(old);
        }
        await _db.SaveChangesAsync(ct);
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
        return true;
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
