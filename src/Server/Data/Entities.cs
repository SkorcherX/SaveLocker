using SaveLocker.Shared;

namespace SaveLocker.Server.Data;

/// <summary>A registered machine (the PC or the laptop).</summary>
public class Machine
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastSeen { get; set; }
}

/// <summary>A tracked game whose saves are synced.</summary>
public class Game
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Key into the Ludusavi manifest, if matched.</summary>
    public string? ManifestKey { get; set; }
    /// <summary>JSON array of user-provided override paths.</summary>
    public string? CustomPathsJson { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Save folder suggested when the game is defined (e.g. in the dashboard).
    /// Propagated to agents, which use it if it exists on that machine, otherwise
    /// the user maps a local folder. The resolved per-machine folder is stored
    /// server-side as a <see cref="MachineSavePath"/>.
    /// </summary>
    public string? SuggestedSaveDir { get; set; }

    // Cached SteamGridDB artwork (served relative URLs under wwwroot/art/, or null).
    public string? GridUrl { get; set; }
    public string? HeroUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? IconUrl { get; set; }

    /// <summary>How many versions to keep for this game. Null = use the server global default.</summary>
    public int? RetainVersions { get; set; }

    /// <summary>Newline-separated per-game exclude globs (e.g. <c>*.log</c>). Applied on top
    /// of the server global defaults when agents hash + archive this game's saves.</summary>
    public string? ExcludeGlobs { get; set; }

    /// <summary>The current authoritative version agents should pull. Null until first upload.</summary>
    public Guid? HeadVersionId { get; set; }

    public List<SaveVersion> Versions { get; set; } = new();
}

/// <summary>An uploaded snapshot of a game's saves. Forms a parent chain (lineage).</summary>
public class SaveVersion
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    public Guid MachineId { get; set; }
    public Machine? Machine { get; set; }

    public DateTime CreatedAt { get; set; }
    /// <summary>Stable hash of the save directory contents.</summary>
    public string ContentHash { get; set; } = "";
    public long Size { get; set; }

    /// <summary>The version this snapshot was based on (the head the uploader last knew).</summary>
    public Guid? ParentVersionId { get; set; }

    /// <summary>Relative path of the stored archive within the archive store.</summary>
    public string ArchivePath { get; set; } = "";
}

/// <summary>An exclusive checkout of a game's saves by one machine.</summary>
public class Lease
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    public Guid MachineId { get; set; }
    public Machine? Machine { get; set; }

    public DateTime AcquiredAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>A recorded divergence between two save versions awaiting admin resolution.</summary>
public class ConflictFlag
{
    public Guid Id { get; set; }
    public Guid GameId { get; set; }
    public Game? Game { get; set; }

    public Guid VersionAId { get; set; }
    public Guid VersionBId { get; set; }

    /// <summary>
    /// The machine whose push diverged — the one that produced <see cref="VersionBId"/>, and the one
    /// that is stuck. Part of the dedupe key, and it answers "which machine?" without a join; until
    /// now only an agent-reported health event could say.
    /// <para>
    /// Nullable solely because rows written before dedupe existed have no value. Every new conflict
    /// sets it.
    /// </para>
    /// </summary>
    public Guid? MachineId { get; set; }

    /// <summary>
    /// How many divergent pushes this one open conflict represents. A machine that keeps saving while
    /// conflicted bumps this instead of writing a row per push — the same reasoning that makes agent
    /// health events deduplicate rather than append (<c>HealthService</c>). One real incident produced
    /// <b>75 rows for a single unresolved divergence</b>.
    /// </summary>
    public int Count { get; set; } = 1;

    /// <summary>When the most recent divergent push arrived. <see cref="CreatedAt"/> stays put.</summary>
    public DateTime LastSeen { get; set; }

    public ConflictStatus Status { get; set; } = ConflictStatus.Open;
    public DateTime CreatedAt { get; set; }

    public Guid? ResolvedVersionId { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>
/// A queued instruction from the dashboard for an agent to execute on its next
/// poll (the agent command channel). Agents poll for their pending commands,
/// run them, and report the outcome back.
/// </summary>
public class AgentCommand
{
    public Guid Id { get; set; }

    /// <summary>The machine that should run this command.</summary>
    public Guid MachineId { get; set; }
    public Machine? Machine { get; set; }

    /// <summary>Target game, or null to apply to all of the machine's games.</summary>
    public Guid? GameId { get; set; }

    public AgentCommandType Type { get; set; }
    public bool Force { get; set; }
    public CommandStatus Status { get; set; } = CommandStatus.Pending;

    public DateTime CreatedAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Human-readable outcome the agent reports (e.g. "pushed 1 game").</summary>
    public string? Result { get; set; }
}

/// <summary>
/// A machine's stored save-folder path for a specific game. Keyed on
/// (MachineId, GameId); one row per machine that has mapped a local folder.
/// Injected into the agent's game list for reconcile and surfaced in the dashboard.
/// </summary>
public class MachineSavePath
{
    public Guid MachineId { get; set; }
    public Guid GameId { get; set; }
    public string SavePath { get; set; } = "";
}

/// <summary>
/// An <b>unconfirmed</b> save folder a machine's scan found for a game it has not mapped. Keyed on
/// (MachineId, GameId) like <see cref="MachineSavePath"/>, and deliberately a separate table:
/// writing a guess into MachineSavePath would push it back to the agent as authority on the next
/// poll. The console offers it; a human promotes it.
/// </summary>
public class MachineScanCandidate
{
    public Guid MachineId { get; set; }
    public Guid GameId { get; set; }
    public string SuggestedPath { get; set; } = "";

    /// <summary>Last heartbeat that still reported this path — a stale guess is worth distrusting.</summary>
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// A single-use, short-lived token the console mints so a new agent can trade it for a real
/// machine API key (Decisions.md §4). Only the hash is stored — the raw token exists solely in
/// the policy file the admin downloads.
/// </summary>
public class EnrollmentToken
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = "";

    /// <summary>Machine name this token was minted for. When set, it is binding: the enrolling
    /// agent cannot claim a different identity. Null lets the agent supply its own hostname.</summary>
    public string? MachineName { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set the moment the token is spent. Non-null = burnt; a second redeem is refused.</summary>
    public DateTime? RedeemedAt { get; set; }
    public Guid? RedeemedByMachineId { get; set; }
}

/// <summary>
/// The last heartbeat an agent sent (one row per machine). This is what makes an agent's silence
/// readable: without it, a Deck that never started and a Deck with nothing to report look the same.
/// </summary>
public class AgentHealth
{
    public Guid MachineId { get; set; }
    public Machine? Machine { get; set; }

    public DateTime LastHeartbeat { get; set; }
    public string? AgentVersion { get; set; }
    public string? Platform { get; set; }

    /// <summary>The agent's own last successful sync — not the server's view of it.</summary>
    public DateTime? LastSyncTime { get; set; }

    public int TrackedGames { get; set; }
    /// <summary>Tracked games with no save folder on this machine: silently skipped, so worth naming.</summary>
    public int UnmappedGames { get; set; }
    /// <summary>Pushes waiting on the network. A number that only grows means this machine is stranded.</summary>
    public int OfflineQueueDepth { get; set; }
}

/// <summary>
/// A problem an agent reported that the server could not have inferred on its own. Deduplicated on
/// (MachineId, GameId, Code) while open: a condition that persists updates <see cref="LastSeen"/>
/// and <see cref="Count"/> rather than filling the table with a row every poll.
/// </summary>
public class AgentEvent
{
    public Guid Id { get; set; }

    public Guid MachineId { get; set; }
    public Machine? Machine { get; set; }

    public Guid? GameId { get; set; }
    public Game? Game { get; set; }

    /// <summary>Stable condition id from <see cref="SaveLocker.Shared.AgentEventCodes"/>.</summary>
    public string Code { get; set; } = "";
    public AgentEventSeverity Severity { get; set; }
    public string Message { get; set; } = "";

    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>How many times this condition has been reported since it opened.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Set when the condition cleared — either the agent synced that game cleanly, or an
    /// admin dismissed it. Non-null means it no longer shows as an open problem.</summary>
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>A persisted server setting (key/value), e.g. the SteamGridDB API key.
/// DB values override <c>IConfiguration</c> (appsettings/env) so admins can manage
/// settings from the dashboard without editing config files.</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Append-only audit trail for visibility in the dashboard.</summary>
public class AuditLog
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid? MachineId { get; set; }
    public Guid? GameId { get; set; }
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
}
