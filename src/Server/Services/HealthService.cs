using Microsoft.EntityFrameworkCore;
using SaveLocker.Server.Data;
using SaveLocker.Shared;

namespace SaveLocker.Server.Services;

/// <summary>
/// Agent health: the console is the Deck's UI (Decisions.md §2). A headless spoke cannot raise a
/// toast, so it reports what went wrong here and the console surfaces it.
/// <para>
/// Two rules shape everything below. <b>Events deduplicate</b> on (machine, game, code) while open —
/// an agent polling every 20 s with a persistent fault must not manufacture 4,300 rows a day.
/// And <b>a machine heals its own alarms</b>: a game that syncs cleanly resolves that machine's open
/// events for it, so a Deck that recovers does not leave a stale problem on the dashboard forever.
/// </para>
/// </summary>
public sealed class HealthService
{
    /// <summary>
    /// How long after its last heartbeat an agent is presumed dead. The poll is 20 s, so this
    /// tolerates a few missed beats (a suspended Deck, a flaky link) before crying wolf.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;

    public HealthService(AppDbContext db) => _db = db;

    /// <summary>Record a heartbeat, fold in any reported events, and close what the agent just fixed.</summary>
    public async Task RecordHeartbeatAsync(Guid machineId, AgentHeartbeat beat)
    {
        var now = DateTime.UtcNow;

        var health = await _db.AgentHealth.FirstOrDefaultAsync(h => h.MachineId == machineId);
        if (health is null)
        {
            health = new AgentHealth { MachineId = machineId };
            _db.AgentHealth.Add(health);
        }

        health.LastHeartbeat = now;
        health.AgentVersion = beat.AgentVersion;
        health.Platform = beat.Platform;
        health.LastSyncTime = beat.LastSyncTime;
        health.TrackedGames = beat.TrackedGames;
        health.UnmappedGames = beat.UnmappedGames;
        health.OfflineQueueDepth = beat.OfflineQueueDepth;

        // Resolve first, then apply new events. A heartbeat can legitimately carry both — "Hades
        // synced fine" and "Celeste's save folder is gone" — and doing it in this order means a
        // fault re-reported in the same beat stays open instead of being wrongly cleared.
        foreach (var gameId in beat.ResolvedGameIds ?? Array.Empty<Guid>())
            await ResolveForGameAsync(machineId, gameId, now);

        foreach (var ev in beat.Events ?? Array.Empty<AgentEventReport>())
            await ApplyEventAsync(machineId, ev, now);

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Fold one reported condition into the open set: bump the existing row if this machine already
    /// has this condition open for this game, otherwise open a new one.
    /// </summary>
    private async Task ApplyEventAsync(Guid machineId, AgentEventReport ev, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(ev.Code)) return;

        // A game the server has since deleted would violate the FK; treat it as machine-wide.
        var gameId = ev.GameId;
        if (gameId is { } g && !await _db.Games.AnyAsync(x => x.Id == g))
            gameId = null;

        var open = await _db.AgentEvents.FirstOrDefaultAsync(e =>
            e.MachineId == machineId &&
            e.Code == ev.Code &&
            e.GameId == gameId &&
            e.ResolvedAt == null);

        if (open is not null)
        {
            open.LastSeen = ev.OccurredAt ?? now;
            open.Count++;
            open.Message = ev.Message;   // keep the freshest wording; the condition is the same
            open.Severity = ev.Severity;
            return;
        }

        _db.AgentEvents.Add(new AgentEvent
        {
            Id = Guid.NewGuid(),
            MachineId = machineId,
            GameId = gameId,
            Code = ev.Code,
            Severity = ev.Severity,
            Message = ev.Message,
            FirstSeen = ev.OccurredAt ?? now,
            LastSeen = ev.OccurredAt ?? now,
            Count = 1
        });
    }

    /// <summary>The agent synced this game cleanly, so whatever it was complaining about is over.</summary>
    private async Task ResolveForGameAsync(Guid machineId, Guid gameId, DateTime now)
    {
        var open = await _db.AgentEvents
            .Where(e => e.MachineId == machineId && e.GameId == gameId && e.ResolvedAt == null)
            .ToListAsync();
        foreach (var e in open) e.ResolvedAt = now;
    }

    /// <summary>Every machine's health, including machines that have never sent a heartbeat at all —
    /// an agent that was enrolled and never ran is exactly the case worth seeing.</summary>
    public async Task<List<AgentHealthDto>> ListAsync()
    {
        var machines = await _db.Machines.OrderBy(m => m.Name).ToListAsync();
        var health = await _db.AgentHealth.ToDictionaryAsync(h => h.MachineId);

        var openEvents = await _db.AgentEvents
            .Where(e => e.ResolvedAt == null)
            .Include(e => e.Game)
            .OrderByDescending(e => e.LastSeen)
            .ToListAsync();

        var cutoff = DateTime.UtcNow - StaleAfter;

        return machines.Select(m =>
        {
            var h = health.GetValueOrDefault(m.Id);
            return new AgentHealthDto(
                MachineId: m.Id,
                MachineName: m.Name,
                Online: h is not null && h.LastHeartbeat >= cutoff,
                LastHeartbeat: h?.LastHeartbeat,
                AgentVersion: h?.AgentVersion,
                Platform: h?.Platform,
                LastSyncTime: h?.LastSyncTime,
                TrackedGames: h?.TrackedGames ?? 0,
                UnmappedGames: h?.UnmappedGames ?? 0,
                OfflineQueueDepth: h?.OfflineQueueDepth ?? 0,
                OpenEvents: openEvents
                    .Where(e => e.MachineId == m.Id)
                    .Select(e => ToDto(e, m.Name))
                    .ToArray());
        }).ToList();
    }

    /// <summary>Every open problem across the fleet, worst first — what the console's badge counts.</summary>
    public async Task<List<AgentEventDto>> ListOpenEventsAsync()
    {
        var events = await _db.AgentEvents
            .Where(e => e.ResolvedAt == null)
            .Include(e => e.Machine)
            .Include(e => e.Game)
            .ToListAsync();

        return events
            .OrderByDescending(e => e.Severity)
            .ThenByDescending(e => e.LastSeen)
            .Select(e => ToDto(e, e.Machine?.Name ?? "(unknown)"))
            .ToList();
    }

    /// <summary>Admin: dismiss an event. It does not fix anything — if the condition persists, the
    /// agent's next report reopens it, which is the honest behaviour.</summary>
    public async Task<bool> DismissAsync(Guid eventId)
    {
        var ev = await _db.AgentEvents.FindAsync(eventId);
        if (ev is null || ev.ResolvedAt is not null) return false;
        ev.ResolvedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    private static AgentEventDto ToDto(AgentEvent e, string machineName) => new(
        e.Id, e.MachineId, machineName, e.GameId, e.Game?.Name,
        e.Severity, e.Code, e.Message, e.FirstSeen, e.LastSeen, e.Count);
}
