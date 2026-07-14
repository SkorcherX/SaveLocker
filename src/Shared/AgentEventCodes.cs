namespace SaveLocker.Shared;

/// <summary>
/// The stable vocabulary of agent event codes. A code is the <b>deduplication key</b> (with the
/// machine and game), so it must identify a <i>condition</i>, not an occurrence — "this game's save
/// folder is missing", never "at 10:04 the folder was missing".
/// <para>
/// Scope note, because it is easy to over-report: the server already knows everything that happens
/// <i>server-side</i>. A conflict, for instance, is recorded as a <c>ConflictFlag</c> the moment the
/// upload lands, and the dashboard already shows it. What these codes carry is the set of failures
/// the server <b>cannot infer</b>, because they happened on a machine it never heard from.
/// </para>
/// </summary>
public static class AgentEventCodes
{
    /// <summary>A pull was refused because local saves hold un-pushed changes. The machine is stuck
    /// until someone chooses a side — and on a headless box, nobody is being told.</summary>
    public const string PullBlocked = "pull.blocked";

    /// <summary>The game's save folder does not exist on this machine. Nothing is being synced for it.</summary>
    public const string SaveDirMissing = "savedir.missing";

    /// <summary>The server could not be reached; the push is queued for retry. Reported when contact
    /// is regained — by definition it cannot be reported while it is happening.</summary>
    public const string ServerUnreachable = "server.unreachable";

    /// <summary>An upload failed for a reason that is not a network drop (e.g. it exceeded the size cap).</summary>
    public const string PushFailed = "push.failed";

    /// <summary>Saves diverged from the server. The server knows too, but this ties the conflict to the
    /// machine that is stuck behind it — "the Deck is the one that cannot sync".</summary>
    public const string Conflict = "sync.conflict";

    /// <summary>The game was launched while another machine held the lease; a conflict is likely on exit.</summary>
    public const string LeaseHeldElsewhere = "lease.held_elsewhere";

    /// <summary>The save never went quiet, so the settle gate gave up and archived anyway — the
    /// snapshot may be mid-write.</summary>
    public const string SettleTimeout = "settle.timeout";
}
