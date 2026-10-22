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
    string[]? ExcludeGlobs = null,
    ConflictPolicy ConflictPolicy = ConflictPolicy.Manual,
    Guid? PreferredMachineId = null);

/// <summary>A specific machine's stored save path for one game.</summary>
public record MachineSavePathDto(Guid MachineId, string MachineName, string SavePath);

/// <summary>
/// A save folder a machine's <c>scan</c> <b>found but has not adopted</b> — the game is tracked
/// there with no save directory, and this is the local scan's best guess.
/// <para>
/// It is deliberately NOT a <see cref="MachineSavePathDto"/>: a guess must not become the
/// authoritative per-machine path, because that path is pushed straight back to the agent on the
/// next poll. A human confirms it in the console first.
/// </para>
/// </summary>
public record MachineScanCandidateDto(
    Guid MachineId, string MachineName, string SuggestedPath, DateTime LastSeen);

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
    double AutoFetchHours = 0,
    AutoFetchSchedule? Schedule = null,
    DateTime? NextAutoFetchRunAt = null);

/// <summary>
/// When the server automatically checks GitHub for newer agent/plugin packages.
/// <paramref name="Mode"/> is <c>"disabled"</c>, <c>"hours"</c> (poll every
/// <paramref name="Hours"/>), <c>"weekly"</c> (every <paramref name="DayOfWeek"/> at
/// <paramref name="TimeOfDay"/>), or <c>"monthly"</c> (every <paramref name="DayOfMonth"/> at
/// <paramref name="TimeOfDay"/> — clamped to a shorter month's last day, so 31 means "the last
/// day" in February). <paramref name="TimeOfDay"/> (<c>"HH:mm"</c>) and <paramref name="DayOfWeek"/>
/// (0=Sunday..6=Saturday) are interpreted in the SERVER's local time zone — the schedule belongs to
/// the machine running the poll, not to whichever browser happens to be looking at it.
/// </summary>
public record AutoFetchSchedule(
    string Mode,
    double Hours,
    int DayOfWeek,
    int DayOfMonth,
    string TimeOfDay);

/// <summary>
/// The platforms the server hosts an agent package for. These strings are wire values — they appear
/// as <c>?platform=</c> on the update routes and are persisted in each slot's metadata — so they are
/// .NET RIDs rather than anything prettier, and they do not change.
/// <para>
/// <b>Absent means <see cref="Windows"/>.</b> Every agent released before platform slots existed
/// sends no parameter at all, and must keep being offered exactly what it was offered before.
/// </para>
/// </summary>
public static class AgentPlatform
{
    public const string Windows = "win-x64";
    public const string Linux = "linux-x64";

    /// <summary>
    /// The Decky plugin. Not a RID, and not an agent — it is a package the LINUX agent installs into
    /// another application's directory on the user's behalf, and it rides this enum because a slot is
    /// exactly what it needs: an upload, a digest, an atomic replace and a version to compare. The
    /// alternative was a second, near-identical set of routes and a second console card.
    /// <para>
    /// It is the one slot whose GitHub asset comes from a <b>different repository</b>
    /// (<c>SkorcherX/SaveLocker-Decky</c>), which is why a slot carries its own repo.
    /// </para>
    /// </summary>
    public const string DeckyPlugin = "decky-plugin";

    public static readonly string[] All = [Windows, Linux, DeckyPlugin];

    /// <summary>
    /// Normalizes a caller-supplied platform, defaulting an absent one to <see cref="Windows"/>.
    /// Returns false for anything unrecognised — a platform names a directory, so it may never be
    /// taken on trust.
    /// </summary>
    public static bool TryNormalize(string? value, out string platform)
    {
        platform = Windows;
        if (string.IsNullOrWhiteSpace(value)) return true;

        var match = All.FirstOrDefault(p => string.Equals(p, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;

        platform = match;
        return true;
    }

    /// <summary>How a person should see it. Never used as a path or a query value.</summary>
    public static string Describe(string platform) => platform switch
    {
        Linux       => "Linux",
        DeckyPlugin => "Decky plugin",
        _           => "Windows",
    };
}

/// <summary>Status of the agent installer binary hosted on this server.</summary>
/// <param name="Sha256">
/// Lowercase hex SHA-256 of the installer, computed as it was stored. Null only for an installer
/// uploaded before digests existed — a value here is what lets an agent prove the bytes it
/// downloaded are the bytes this server holds.
/// </param>
/// <param name="Platform">
/// Which agent this package is for (<see cref="AgentPlatform"/>). Optional with a Windows default so
/// an <c>installer-info.json</c> written before slots existed still deserializes — that file is
/// always the Windows one, because it is the only one that could have been written.
/// </param>
/// <param name="Source">
/// <c>"manual"</c> (an admin uploaded it) or <c>"github"</c> (fetched from the release, by hand or
/// by the auto-poll). Defaults to <c>"manual"</c> so a package hosted before this field existed
/// deserializes as the more conservative of the two rather than silently claiming a provenance it
/// never recorded.
/// </param>
public record AgentInstallerStatus(
    string Version,
    string FileName,
    DateTime UploadedAt,
    long SizeBytes,
    string? Sha256 = null,
    string Platform = AgentPlatform.Windows,
    string Source = "manual");

/// <summary>
/// Result of comparing a hosted package's digest against its GitHub release's published
/// <c>SHA256SUMS*.txt</c>, when one exists. <paramref name="Status"/> is <c>"match"</c>,
/// <c>"mismatch"</c>, or <c>"unknown"</c> (no digest to compare, or the release publishes no
/// checksums file) — unknown is deliberately not treated as a failure.
/// </summary>
public record InstallerHashVerification(
    string Platform,
    string Status,
    string? PublishedSha256,
    string? ChecksumAsset,
    string? Note);

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
    /// <summary>Null once the uploading machine has been deleted; <see cref="MachineName"/> still
    /// names it.</summary>
    Guid? MachineId,
    string MachineName,
    DateTime CreatedAt,
    string ContentHash,
    long Size,
    Guid? ParentVersionId,
    bool Protected = false);

/// <summary>File count and newest per-file write time inside one version's archive — derived from
/// the zip on demand (<see cref="SaveLocker.Shared.SaveArchive.GetArchiveStats"/>), not stored on
/// <see cref="SaveVersionDto"/>, so fetching it costs nothing for the versions nobody is looking at.
/// Mainly for telling apart the two sides of an open conflict.</summary>
public record VersionStatsDto(int FileCount, DateTime? NewestFileWriteUtc);

[JsonConverter(typeof(JsonStringEnumConverter<UploadStatus>))]
public enum UploadStatus
{
    /// <summary>Archive accepted and became the new head version.</summary>
    Created,
    /// <summary>Incoming content matches the current head; nothing changed.</summary>
    NoChange,
    /// <summary>Incoming version diverged from the head; a conflict was recorded.</summary>
    Conflict,
    /// <summary>
    /// A delta upload was negotiated against a head that moved before it completed (another
    /// machine pushed in between) — the partial payload can no longer be safely reconstructed
    /// against the base it was diffed from. Never surfaced to <c>SyncEngine</c>: <c>ApiClient</c>
    /// retries transparently with a full archive, the same one a diverged push always sends.
    /// </summary>
    RetryFull
}

public record UploadResult(
    UploadStatus Status,
    SaveVersionDto? Version,
    ConflictDto? Conflict);

// ----- Upload (chunked) -----

/// <summary>One file's identity within a save folder, as the agent currently sees it on disk.
/// Enough to tell the server which paths it can copy forward from the current head unchanged and
/// which need fresh bytes, without re-transmitting or re-hashing content that did not change.</summary>
public record FileManifestEntry(string Path, string Sha256, long Size);

/// <param name="Files">
/// The agent's COMPLETE current file manifest for this game (every file, not just changed ones) —
/// optional so an older agent, a forced/first-ever push, or a save below the delta-worthwhile size
/// floor can omit it and get exactly today's whole-archive behavior. When present it doubles as the
/// full declared truth of "what should exist" (mirrors how <c>RestoreArchive</c> already treats
/// "absent from the archive" as a deletion) — no separate removed-paths list is needed.
/// </param>
public record BeginUploadRequest(
    string ContentHash, Guid? ParentVersionId, bool Force, FileManifestEntry[]? Files = null);

/// <summary>Either <see cref="SessionId"/> is set and the caller uploads chunks against it, or
/// <see cref="NoChange"/> is set and there is nothing to send — the server already has this content.
/// <para>
/// <paramref name="UseDeltaPath"/> is only meaningful when a <see cref="SessionId"/> came back and
/// <see cref="BeginUploadRequest.Files"/> was sent: true means the server has a stored per-file
/// manifest for its current head and the agent should zip and send only <paramref name="NeedPaths"/>;
/// false means send the whole archive exactly as before (a diverged/forced/first-ever push, or a
/// head with no stored manifest yet — self-healing once one push completes with a manifest).
/// </para>
/// </summary>
public record BeginUploadResponse(
    Guid? SessionId, UploadResult? NoChange, bool UseDeltaPath = false, string[]? NeedPaths = null);

public record ChunkAppendResponse(long BytesReceived);

// ----- Conflicts -----

[JsonConverter(typeof(JsonStringEnumConverter<ConflictStatus>))]
public enum ConflictStatus
{
    Open,
    Resolved
}

/// <param name="MachineId">The machine whose push diverged — the one that is stuck.</param>
/// <param name="Count">Divergent pushes folded into this one conflict; &gt;1 means it kept recurring.</param>
/// <param name="LastSeen">When the most recent divergent push arrived.</param>
/// <param name="Escalated">True once the conflict has remained open past the server's escalation threshold.</param>
public record ConflictDto(
    Guid Id,
    Guid GameId,
    Guid VersionAId,
    Guid VersionBId,
    ConflictStatus Status,
    DateTime CreatedAt,
    Guid? ResolvedVersionId,
    string? ResolvedBy,
    DateTime? ResolvedAt,
    Guid? MachineId = null,
    int Count = 1,
    DateTime? LastSeen = null,
    bool Escalated = false);

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
    /// <summary>
    /// Handed to the agent on a poll; awaiting its result. This is a <b>lease</b>, not a terminal
    /// state — once it expires unacknowledged the command becomes claimable again.
    /// </summary>
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
    string? Result,
    /// <summary>How many times this command has been handed to an agent. &gt;1 means an earlier
    /// claim was never acknowledged and the command was reclaimed.</summary>
    int ClaimCount = 0,
    /// <summary>When the current claim stops being exclusive; null unless awaiting a result.</summary>
    DateTime? LeaseExpiresAt = null);

public record CommandResultRequest(CommandStatus Status, string? Result);

/// <summary>How many versions an explicit "prune now" actually removed.</summary>
public record PruneResult(int Removed);

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
/// The server URL an enrollment file would be minted with right now, so the console can show it
/// before a single-use token is spent on it.
/// </summary>
/// <param name="Url">The effective base URL, without a trailing slash.</param>
/// <param name="FromConfig">True when it came from <c>Server:PublicBaseUrl</c> rather than the
/// origin this request arrived on.</param>
/// <param name="IsLoopback">True when it names the machine asking. Combined with
/// <paramref name="FromConfig"/> being false this blocks minting, since an inferred loopback address
/// cannot work on the machine being enrolled; a loopback URL that was configured or typed is a
/// deliberate same-box setup and is allowed.</param>
public record EffectiveServerUrl(string Url, bool FromConfig, bool IsLoopback);

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

// ----- Agent health (Phase 5) -----

/// <summary>
/// How loud an agent event is. <see cref="Error"/> means this machine is <i>not syncing</i> and a
/// human must act; <see cref="Warning"/> means it synced but something is off.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentEventSeverity>))]
public enum AgentEventSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// One thing the agent needs a human to know. <paramref name="Code"/> is the stable identity used
/// to <b>deduplicate</b> — the same code for the same machine+game coalesces into one open event
/// with a count, instead of a new row every twenty seconds.
/// </summary>
public record AgentEventReport(
    string Code,
    AgentEventSeverity Severity,
    string Message,
    Guid? GameId = null,
    DateTime? OccurredAt = null);

/// <summary>
/// What the agent tells the server on every poll. This exists because a headless spoke cannot tell
/// the user anything itself (Decisions.md §2) — <b>the console is the Deck's UI</b>.
/// <para>
/// The heartbeat half is what makes silence readable: without it, an agent that never started and
/// an agent with nothing to report look identical. <see cref="ResolvedGameIds"/> carries the games
/// that just synced cleanly, so the machine's own open events for them close automatically — a Deck
/// that fixes itself should not leave a stale alarm on the console.
/// </para>
/// </summary>
public record AgentHeartbeat(
    string AgentVersion,
    string Platform,
    DateTime? LastSyncTime = null,
    int TrackedGames = 0,
    int UnmappedGames = 0,
    int OfflineQueueDepth = 0,
    AgentEventReport[]? Events = null,
    Guid[]? ResolvedGameIds = null,
    // Appended, and optional, on purpose: an older agent simply omits it and a newer agent talking
    // to an older server has it ignored, so the fleet and the container can be upgraded in either
    // order (see CONTEXT.md's deploy note).
    ScanPathCandidate[]? PathCandidates = null);

/// <summary>An unresolved conflict old enough to demand attention on an agent that can toast.</summary>
public record ConflictEscalationDto(
    Guid ConflictId,
    Guid GameId,
    string GameName,
    string? StuckMachineName,
    DateTime CreatedAt,
    int Count);

/// <summary>Server guidance returned with a heartbeat.</summary>
public record AgentHeartbeatResponse(ConflictEscalationDto[] EscalatedConflicts);

/// <summary>
/// "This machine's scan found a save folder for a game it tracks but has not mapped." Reported so
/// the console can offer it for one-click confirmation — on a Deck, typing a 130-character path on
/// an on-screen keyboard is the alternative.
/// </summary>
public record ScanPathCandidate(Guid GameId, string SuggestedPath);

/// <summary>An open (or dismissed) agent event as the console shows it.</summary>
public record AgentEventDto(
    Guid Id,
    Guid MachineId,
    string MachineName,
    Guid? GameId,
    string? GameName,
    AgentEventSeverity Severity,
    string Code,
    string Message,
    DateTime FirstSeen,
    DateTime LastSeen,
    int Count);

/// <summary>
/// A machine's health as the console shows it. <paramref name="Online"/> is computed from the last
/// heartbeat against the staleness window — an agent that stopped reporting is the single most
/// important thing this endpoint exists to surface.
/// </summary>
public record AgentHealthDto(
    Guid MachineId,
    string MachineName,
    bool Online,
    DateTime? LastHeartbeat,
    string? AgentVersion,
    string? Platform,
    DateTime? LastSyncTime,
    int TrackedGames,
    int UnmappedGames,
    int OfflineQueueDepth,
    AgentEventDto[] OpenEvents);

// ----- Agent update channel -----

/// <summary>Latest available agent version info, served by the SaveLocker server.</summary>
/// <param name="Sha256">
/// Lowercase hex SHA-256 the downloaded installer must match before the agent will execute it.
/// <para>
/// This is the <b>only</b> integrity control on the default configuration: SaveLocker ships no
/// certificates and plain http is supported (Decisions.md), so the transport proves nothing about
/// what arrived. Null when the server cannot supply one — an installer stored before digests
/// existed, or a hand-configured <c>AgentUpdate:DownloadUrl</c> with no <c>AgentUpdate:Sha256</c>
/// beside it. The agent decides what to do about null; for an off-origin download it refuses.
/// </para>
/// </param>
public record AgentVersionInfo(string LatestVersion, string DownloadUrl, string? Sha256 = null);

// ----- Per-game conflict policy -----

/// <summary>
/// What the server does when an upload diverges from the current head.
/// Default is <see cref="Manual"/>: record the conflict and wait for an admin to resolve it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictPolicy>))]
public enum ConflictPolicy
{
    /// <summary>Record the conflict and let an admin resolve it. (Default.)</summary>
    Manual = 0,
    /// <summary>The incoming version always wins — no conflict row is created. Useful for
    /// single-player games where the most recent save is always the right one.</summary>
    NewestWins = 1,
    /// <summary>The designated machine's saves always win. When it pushes a divergent save the
    /// head advances. Pushes from any other machine follow the <see cref="Manual"/> path.</summary>
    PreferMachine = 2,
}

public record SetConflictPolicyRequest(ConflictPolicy Policy, Guid? PreferredMachineId = null);

// ----- Server / console build identity -----

/// <summary>
/// What the console is running. Served unauthenticated from /api/admin/status, which is
/// already the reachability probe — "is my fix deployed?" must be answerable before you can
/// authenticate, since a wrong password is one of the things you'd be diagnosing.
/// <paramref name="Version"/> is the product version shared with the agent (one git tag for
/// the whole repo); it carries a <c>+{n}.{sha}</c> suffix on builds after the nearest tag.
/// <paramref name="BuiltAt"/> is UTC, null when unstamped.
/// </summary>
public record ServerBuildInfo(string Version, string Commit, DateTime? BuiltAt, bool IsRelease);

/// <summary>Response of /api/admin/status — reachability, auth requirement and build identity.</summary>
public record AdminStatus(bool PasswordRequired, ServerBuildInfo Build);

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
