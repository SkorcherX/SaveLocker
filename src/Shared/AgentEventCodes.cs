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

    /// <summary>A pull was refused because the game itself is running. Restoring under a live process
    /// loses the restored save the moment the game writes at exit.</summary>
    public const string PullBlockedRunning = "pull.blocked_running";

    /// <summary>The game is mapped to a folder that may never be archived or replaced — a drive root,
    /// a user profile, a system directory, or the agent's own state. Nothing is being synced for it,
    /// and that is the safe outcome.</summary>
    public const string UnsafeSavePath = "savedir.unsafe";

    /// <summary>Another process on this machine holds the game's sync lock, so this operation was
    /// skipped rather than run alongside it. Nothing was read or written.</summary>
    public const string SyncBusy = "sync.busy";

    /// <summary>The game's save folder does not exist on this machine. Nothing is being synced for it.</summary>
    public const string SaveDirMissing = "savedir.missing";

    /// <summary>Every save file of the game is gone from a folder that had them when it last synced. Nothing is pushed
    /// or pulled for it here until someone chooses Delete everywhere or Restore here.</summary>
    public const string SaveFolderEmptied = "savedir.emptied";

    /// <summary>
    /// One of a game's extra save folders resolved to a folder on this machine that already holds
    /// different files from the copy the fleet syncs, so it was NOT mapped: which side to keep is the
    /// user's call (<c>add-path --keep local|cloud</c>). Until then the folder keeps syncing through
    /// its shadow, so nothing is lost — but nothing in that folder is synced either.
    /// </summary>
    public const string SaveFolderNeedsChoice = "savedir.needs_choice";

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

    /// <summary>
    /// A newer agent has been downloaded, verified and unpacked, and will be installed the next time
    /// this machine's agent starts. Informational, and the only notice anyone gets: a Deck has no
    /// tray to prompt from, so "an update is waiting" exists on the dashboard or nowhere.
    /// </summary>
    public const string UpdateStaged = "update.staged";

    /// <summary>
    /// A staged update could not be used — it failed its digest, unpacked to something that is not
    /// an agent, or would not run when asked for its version. Nothing was replaced, so the machine
    /// keeps working; what it stops doing is <i>updating</i>, silently and indefinitely, which is
    /// exactly the kind of fault a headless device never surfaces on its own.
    /// </summary>
    public const string UpdateFailed = "update.failed";

    /// <summary>
    /// An installed update did not survive its first start and the previous version was put back.
    /// The machine is running and syncing, so nothing else will look wrong — but this fleet has a
    /// build that bricks the agent on at least one device, and that is worth an admin knowing before
    /// they roll it out anywhere else.
    /// </summary>
    public const string UpdateRolledBack = "update.rolled_back";

    /// <summary>
    /// The Decky plugin installed on this machine was updated in place by the agent. Informational,
    /// and the only account anyone gets of it: the plugin is updated without Decky's store, without
    /// sudo and without a Steam restart, so absolutely nothing else on the device announces that the
    /// code running inside Steam's UI just changed.
    /// </summary>
    public const string PluginUpdated = "plugin.updated";

    /// <summary>
    /// A newer Decky plugin was offered but not installed. The commonest cause is not a fault at all
    /// in the usual sense — a package that needs a file the agent may not create, which means the
    /// plugin must be reinstalled once through Decky by hand. Reported because nothing else would:
    /// the old plugin keeps working, so the device simply stops keeping up, indefinitely.
    /// </summary>
    public const string PluginUpdateFailed = "plugin.update_failed";
}
