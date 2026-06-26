using LocalGameSync.Shared;

namespace LocalGameSync.Server.Data;

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
    /// the user maps a local folder. Per-machine paths are not stored server-side.
    /// </summary>
    public string? SuggestedSaveDir { get; set; }

    // Cached SteamGridDB artwork (served relative URLs under wwwroot/art/, or null).
    public string? GridUrl { get; set; }
    public string? HeroUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? IconUrl { get; set; }

    /// <summary>How many versions to keep for this game. Null = use the server global default.</summary>
    public int? RetainVersions { get; set; }

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
