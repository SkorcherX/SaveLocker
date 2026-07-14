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
    int? RetainVersions = null,
    // Dashboard endpoints carry the game's own patterns; the agent /games endpoint
    // carries the effective set (global defaults ∪ per-game) that agents apply.
    string[]? ExcludeGlobs = null);

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
    bool AdminPasswordSet,
    string[]? DefaultExcludeGlobs = null,
    double AutoFetchHours = 0);

/// <summary>Status of the agent installer binary hosted on this server.</summary>
public record AgentInstallerStatus(
    string Version,
    string FileName,
    DateTime UploadedAt,
    long SizeBytes);

/// <summary>Set (or clear, when null/empty) the SteamGridDB API key from the dashboard.</summary>
public record SetSteamGridDbKeyRequest(string? ApiKey);

/// <summary>Sets how often the server checks GitHub for a newer agent installer; 0 disables it.</summary>
public record SetAutoFetchHoursRequest(double Hours);

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

// ----- Enrollment -----

/// <summary>
/// Admin: mint a single-use enrollment token. <paramref name="ServerUrl"/> overrides the URL the
/// console was reached on — needed when the admin browses over the LAN but the enrolling machine
/// must use the public (tunnel) URL. <paramref name="GameIds"/> null = every enabled game.
/// </summary>
public record CreateEnrollmentRequest(
    string? MachineName = null,
    int? TtlMinutes = null,
    string? ServerUrl = null,
    Guid[]? GameIds = null,
    int? SettleQuietSeconds = null,
    int? SettleMaxWaitSeconds = null);

/// <summary>An enrollment token as the console lists it. The raw token is shown ONCE, at mint.</summary>
public record EnrollmentDto(
    Guid Id,
    string? MachineName,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? RedeemedAt,
    string? RedeemedByMachineName);

/// <summary>Mint result: the row (for listing/revoking) plus the policy file handed to the agent.</summary>
public record CreateEnrollmentResponse(Guid Id, EnrollmentPolicy Policy);

/// <summary>
/// The enrollment file: <c>savelocker enroll --file &lt;policy&gt;</c>. Carries a single-use,
/// short-lived <see cref="Token"/> — never a machine API key, so a leaked file expires on its own
/// and is revocable.
/// <para>
/// <b>Deliberately unsigned</b> (Decisions.md §4). The threat a forged file poses is not a bogus
/// token but a <i>malicious server URL</i>, whose pull writes into save directories — and a fresh
/// agent has no trust anchor with which to check a signature, so a PKI here would be theatre. The
/// user is the trust anchor: they downloaded this from their own console. HTTPS plus the TOFU pin
/// the agent records at enrollment are what actually mitigate it.
/// </para>
/// The <see cref="Games"/> list only <i>pre-seeds</i> the agent so a fresh machine is useful before
/// its first poll; the server remains authoritative and the agent's reconcile keeps it so.
/// Null settle values mean "keep the agent's defaults".
/// </summary>
public record EnrollmentPolicy(
    int Version,
    string ServerUrl,
    string Token,
    DateTime ExpiresAt,
    string? MachineName = null,
    int? SettleQuietSeconds = null,
    int? SettleMaxWaitSeconds = null,
    EnrollmentGame[]? Games = null)
{
    /// <summary>Schema version of the policy file. Bump only on a breaking shape change.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>A game pre-selected by the console, as it appears in the policy file.</summary>
public record EnrollmentGame(
    Guid GameId,
    string Name,
    string? ManifestKey = null,
    string? SuggestedSaveDir = null,
    string[]? ExcludeGlobs = null);

/// <summary>Agent → server: trade the enrollment token for this machine's real API key.</summary>
public record RedeemEnrollmentRequest(string Token, string? MachineName = null);

/// <summary>The machine identity the redeemed token bought.</summary>
public record RedeemEnrollmentResponse(Guid MachineId, string ApiKey, string MachineName);

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
