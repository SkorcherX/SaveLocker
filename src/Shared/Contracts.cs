using System.Text.Json.Serialization;

namespace SaveLocker.Shared;

/// <summary>
/// DTOs exchanged over the REST API between the agent and the server.
/// These are the wire contract; server entities live separately.
/// </summary>

// ----- Machine registration -----

public record MachineRegisterRequest(string Name);

public record MachineRegisterResponse(Guid MachineId, string ApiKey);

public record MachineDto(Guid Id, string Name, DateTime CreatedAt, DateTime LastSeen);

// ----- Games -----

public record GameDto(
    Guid Id,
    string Name,
    string? ManifestKey,
    string? CustomPathsJson,
    bool Enabled,
    string? SuggestedSaveDir = null,
    string? MachineSavePath = null,
    string? GridUrl = null,
    string? HeroUrl = null,
    string? LogoUrl = null,
    string? IconUrl = null,
    int? RetainVersions = null);

/// <summary>A specific machine's stored save path for one game.</summary>
public record MachineSavePathDto(Guid MachineId, string MachineName, string SavePath);

public record CreateGameRequest(
    string Name,
    string? ManifestKey,
    string? CustomPathsJson,
    string? SuggestedSaveDir = null);

// ----- Server settings (dashboard-managed) -----

/// <summary>Server settings surfaced to the dashboard. Never returns the raw key.</summary>
public record ServerSettingsDto(
    bool SteamGridDbConfigured,
    string? SteamGridDbKeyMasked,
    bool SteamGridDbFromConfig,
    bool AdminPasswordSet);

/// <summary>Set (or clear, when null/empty) the SteamGridDB API key from the dashboard.</summary>
public record SetSteamGridDbKeyRequest(string? ApiKey);

/// <summary>Set (or clear, when null/empty) the admin dashboard password.</summary>
public record SetAdminPasswordRequest(string? Password);

// ----- Leases -----

public record LeaseDto(
    Guid GameId,
    Guid? HolderMachineId,
    string? HolderMachineName,
    DateTime? AcquiredAt,
    DateTime? ExpiresAt);

public record LeaseAcquireResponse(bool Granted, LeaseDto Lease);

// ----- Save versions -----

public record SaveVersionDto(
    Guid Id,
    Guid GameId,
    Guid MachineId,
    string MachineName,
    DateTime CreatedAt,
    string ContentHash,
    long Size,
    Guid? ParentVersionId);

[JsonConverter(typeof(JsonStringEnumConverter<UploadStatus>))]
public enum UploadStatus
{
    /// <summary>Archive accepted and became the new head version.</summary>
    Created,
    /// <summary>Incoming content matches the current head; nothing changed.</summary>
    NoChange,
    /// <summary>Incoming version diverged from the head; a conflict was recorded.</summary>
    Conflict
}

public record UploadResult(
    UploadStatus Status,
    SaveVersionDto? Version,
    ConflictDto? Conflict);

// ----- Conflicts -----

[JsonConverter(typeof(JsonStringEnumConverter<ConflictStatus>))]
public enum ConflictStatus
{
    Open,
    Resolved
}

public record ConflictDto(
    Guid Id,
    Guid GameId,
    Guid VersionAId,
    Guid VersionBId,
    ConflictStatus Status,
    DateTime CreatedAt,
    Guid? ResolvedVersionId,
    string? ResolvedBy,
    DateTime? ResolvedAt);

// ----- Aggregate state (dashboard + agent sync) -----

public record GameStateDto(
    GameDto Game,
    SaveVersionDto? Head,
    LeaseDto? Lease,
    bool HasOpenConflict,
    long TotalStorageBytes = 0);

// ----- Agent command channel (dashboard → agent) -----

/// <summary>What a dashboard-issued command asks an agent to do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentCommandType>))]
public enum AgentCommandType
{
    /// <summary>Force-pull the server head (overwrites local).</summary>
    Pull,
    /// <summary>Force-push the local save (overwrites server head).</summary>
    Push,
    /// <summary>Pull then push.</summary>
    Sync,
    /// <summary>Run a local discovery scan and report what was found.</summary>
    Scan
}

[JsonConverter(typeof(JsonStringEnumConverter<CommandStatus>))]
public enum CommandStatus
{
    /// <summary>Queued, not yet handed to the agent.</summary>
    Pending,
    /// <summary>Handed to the agent on a poll; awaiting its result.</summary>
    Dispatched,
    Done,
    Failed
}

/// <summary>A command the dashboard wants an agent to run (null GameId = all games).</summary>
public record EnqueueCommandRequest(Guid MachineId, Guid? GameId, AgentCommandType Type, bool Force);

public record AgentCommandDto(
    Guid Id,
    Guid? MachineId,
    string? MachineName,
    Guid? GameId,
    AgentCommandType Type,
    bool Force,
    CommandStatus Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? Result);

public record CommandResultRequest(CommandStatus Status, string? Result);

// ----- Agent update channel -----

/// <summary>Latest available agent version info, served by the SaveLocker server.</summary>
public record AgentVersionInfo(string LatestVersion, string DownloadUrl);

// ----- Server backups (admin) -----

/// <summary>One on-box SQLite snapshot file. <paramref name="CreatedAt"/> is UTC.</summary>
public record BackupInfo(string FileName, long SizeBytes, DateTime CreatedAt);

/// <summary>Outcome of a manual/scheduled backup run and the resulting retained count.</summary>
public record BackupResult(bool Ok, string? Message, BackupInfo? Backup, int TotalBackups);

// ----- Audit log -----

public record AuditEntryDto(
    Guid Id,
    DateTime Timestamp,
    Guid? MachineId,
    string? MachineName,
    Guid? GameId,
    string? GameName,
    string Action,
    string? Detail);
