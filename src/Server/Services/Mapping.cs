using LocalGameSync.Server.Data;
using LocalGameSync.Shared;

namespace LocalGameSync.Server.Services;

/// <summary>Maps server entities to wire DTOs.</summary>
public static class Mapping
{
    public static MachineDto ToDto(this Machine m) =>
        new(m.Id, m.Name, m.CreatedAt, m.LastSeen);

    public static GameDto ToDto(this Game g) =>
        new(g.Id, g.Name, g.ManifestKey, g.CustomPathsJson, g.Enabled, g.SuggestedSaveDir,
            g.GridUrl, g.HeroUrl, g.LogoUrl, g.IconUrl);

    public static SaveVersionDto ToDto(this SaveVersion v) =>
        new(v.Id, v.GameId, v.MachineId, v.Machine?.Name ?? "", v.CreatedAt,
            v.ContentHash, v.Size, v.ParentVersionId);

    public static LeaseDto ToDto(this Lease? lease, Guid gameId) =>
        lease is null
            ? new LeaseDto(gameId, null, null, null, null)
            : new LeaseDto(gameId, lease.MachineId, lease.Machine?.Name,
                lease.AcquiredAt, lease.ExpiresAt);

    public static AgentCommandDto ToDto(this AgentCommand c) =>
        new(c.Id, c.MachineId, c.Machine?.Name, c.GameId, c.Type, c.Force,
            c.Status, c.CreatedAt, c.CompletedAt, c.Result);

    public static ConflictDto ToDto(this ConflictFlag c) =>
        new(c.Id, c.GameId, c.VersionAId, c.VersionBId, c.Status, c.CreatedAt,
            c.ResolvedVersionId, c.ResolvedBy, c.ResolvedAt);
}
