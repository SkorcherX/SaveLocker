using System.Text.Json;
using SaveLocker.Server.Data;
using SaveLocker.Shared;

namespace SaveLocker.Server.Services;

/// <summary>Maps server entities to wire DTOs.</summary>
public static class Mapping
{
    public static MachineDto ToDto(this Machine m) =>
        new(m.Id, m.Name, m.CreatedAt, m.LastSeen);

    /// <summary>The game as the console sees it. <paramref name="extraPaths"/> are its extra save
    /// folders (<see cref="SyncService.GetExtraSavePathsAsync"/>); omitted, the DTO carries none.</summary>
    public static GameDto ToDto(this Game g, IEnumerable<GameSavePath>? extraPaths = null) =>
        g.ToDtoWithPaths(null, extraPaths);

    /// <summary>
    /// The game as one machine sees it: <paramref name="machinePaths"/> is that machine's stored folder
    /// per path key (<see cref="SyncService.GetMachinePathMapAsync"/>). The primary folder's goes in
    /// <see cref="GameDto.MachineSavePath"/>, where an older agent reads it.
    /// </summary>
    public static GameDto ToDtoWithPaths(this Game g, IReadOnlyDictionary<string, string>? machinePaths,
        IEnumerable<GameSavePath>? extraPaths) =>
        new(g.Id, g.Name, g.ManifestKey, g.CustomPathsJson, g.Enabled, g.SuggestedSaveDir,
            machinePaths?.GetValueOrDefault(SaveRoot.PrimaryKey), g.GridUrl, g.HeroUrl, g.LogoUrl, g.IconUrl,
            g.RetainVersions, GlobConfig.Parse(g.ExcludeGlobs), g.ConflictPolicy, g.PreferredMachineId,
            NullIfEmpty(GlobConfig.Parse(g.IncludeGlobs)),
            ToDtos(extraPaths, machinePaths),
            Release: ReadRelease(g));

    /// <summary>The game's release, or null for none — or for JSON this build cannot read.</summary>
    public static GameReleaseDto? ReadRelease(Game g)
    {
        if (g.ReleaseJson is null) return null;
        try { return JsonSerializer.Deserialize<GameReleaseDto>(g.ReleaseJson, JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }

    public static SavePathDto ToDto(this GameSavePath p, string? machinePath = null) =>
        new(p.Key, p.Label, p.Template, NullIfEmpty(GlobConfig.Parse(p.IncludeGlobs)), machinePath);

    private static SavePathDto[]? ToDtos(IEnumerable<GameSavePath>? paths, IReadOnlyDictionary<string, string>? machinePaths)
    {
        var list = paths?
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.ToDto(machinePaths?.GetValueOrDefault(p.Key)))
            .ToArray();
        return list is { Length: > 0 } ? list : null;
    }

    private static string[]? NullIfEmpty(string[] globs) => globs.Length == 0 ? null : globs;

    public static SaveVersionDto ToDto(this SaveVersion v) =>
        new(v.Id, v.GameId, v.MachineId, UploaderName(v), v.CreatedAt,
            v.ContentHash, v.Size, v.ParentVersionId, v.Protected);

    /// <summary>
    /// Names the uploader honestly. A version whose machine has been deleted keeps its snapshotted
    /// name and says so, rather than rendering as an empty string.
    /// </summary>
    private static string UploaderName(SaveVersion v)
    {
        if (v.MachineId is not null) return v.Machine?.Name ?? v.MachineName;
        return string.IsNullOrWhiteSpace(v.MachineName)
            ? "(deleted machine)"
            : $"{v.MachineName} (deleted)";
    }

    public static LeaseDto ToDto(this Lease? lease, Guid gameId) =>
        lease is null
            ? new LeaseDto(gameId, null, null, null, null)
            : new LeaseDto(gameId, lease.MachineId, lease.Machine?.Name,
                lease.AcquiredAt, lease.ExpiresAt);

    public static AgentCommandDto ToDto(this AgentCommand c) =>
        new(c.Id, c.MachineId, c.Machine?.Name, c.GameId, c.Type, c.Force,
            c.Status, c.CreatedAt, c.CompletedAt, c.Result, c.ClaimCount, c.LeaseExpiresAt);

    public static ConflictDto ToDto(this ConflictFlag c, bool escalated = false) =>
        new(c.Id, c.GameId, c.VersionAId, c.VersionBId, c.Status, c.CreatedAt,
            c.ResolvedVersionId, c.ResolvedBy, c.ResolvedAt,
            c.MachineId, c.Count, c.LastSeen, escalated);
}
