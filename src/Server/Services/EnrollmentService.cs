using Microsoft.EntityFrameworkCore;
using SaveLocker.Server.Data;
using SaveLocker.Shared;

namespace SaveLocker.Server.Services;

/// <summary>
/// Enrollment: the console mints a single-use, short-lived token wrapped in a policy file, and a
/// new agent trades that token for its real machine API key (Decisions.md §4). A leaked policy
/// file therefore expires on its own and is revocable — a long-lived API key sitting in
/// <c>~/Downloads</c> is neither.
/// </summary>
public sealed class EnrollmentService
{
    /// <summary>Long enough to walk to the other machine, short enough that a stray file is dead on arrival.</summary>
    public const int DefaultTtlMinutes = 15;
    private const int MaxTtlMinutes = 24 * 60;

    /// <summary>
    /// How long a token lingers in the console's enrollment list after its window closes, before it
    /// is pruned. The audit log keeps the permanent record (create / redeem / revoke / expire); this
    /// only controls how long dead files clutter the list.
    /// </summary>
    public static readonly TimeSpan ListRetention = TimeSpan.FromHours(24);

    private readonly AppDbContext _db;
    private readonly SyncService _sync;
    private readonly IConfiguration _config;

    public EnrollmentService(AppDbContext db, SyncService sync, IConfiguration config)
    {
        _db = db;
        _sync = sync;
        _config = config;
    }

    /// <summary>
    /// Mint a token and build the policy file around it. The raw token is returned here and
    /// nowhere else — only its hash is stored.
    /// </summary>
    public async Task<CreateEnrollmentResponse> CreateAsync(CreateEnrollmentRequest req, string requestServerUrl)
    {
        var ttl = Math.Clamp(req.TtlMinutes ?? DefaultTtlMinutes, 1, MaxTtlMinutes);
        var now = DateTime.UtcNow;
        var rawToken = Tokens.NewApiKey();

        var token = new EnrollmentToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Tokens.Hash(rawToken),
            MachineName = string.IsNullOrWhiteSpace(req.MachineName) ? null : req.MachineName.Trim(),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(ttl)
        };
        _db.EnrollmentTokens.Add(token);

        var games = await _db.Games
            .Where(g => g.Enabled)
            .OrderBy(g => g.Name)
            .ToListAsync();

        // A null GameIds means "every enabled game" — which is also what the agent's reconcile
        // would adopt on its first poll. The list is a head start, not a different answer.
        if (req.GameIds is { Length: > 0 })
        {
            var wanted = req.GameIds.ToHashSet();
            games = games.Where(g => wanted.Contains(g.Id)).ToList();
        }

        var policy = new EnrollmentPolicy(
            Version: EnrollmentPolicy.CurrentVersion,
            ServerUrl: (string.IsNullOrWhiteSpace(req.ServerUrl) ? requestServerUrl : req.ServerUrl).TrimEnd('/'),
            Token: rawToken,
            ExpiresAt: token.ExpiresAt,
            MachineName: token.MachineName,
            SettleQuietSeconds: req.SettleQuietSeconds,
            SettleMaxWaitSeconds: req.SettleMaxWaitSeconds,
            Games: games.Select(g => new EnrollmentGame(
                g.Id,
                g.Name,
                g.ManifestKey,
                g.SuggestedSaveDir,
                GlobConfig.Effective(_config, g.ExcludeGlobs))).ToArray());

        _db.AuditLogs.Add(NewAudit("enrollment.create", MintDetail(token)));
        await _db.SaveChangesAsync();

        return new CreateEnrollmentResponse(token.Id, policy);
    }

    /// <summary>
    /// Every still-relevant token, newest first, with the machine that spent it (for the console's
    /// list). Tokens whose window closed more than <see cref="ListRetention"/> ago are dropped —
    /// their history lives in the audit log, not this list. The sweeper deletes them for real
    /// shortly after; filtering here makes the 24-hour cutoff exact regardless of sweep timing.
    /// </summary>
    public async Task<List<EnrollmentDto>> ListAsync()
    {
        var cutoff = DateTime.UtcNow - ListRetention;
        var rows = await _db.EnrollmentTokens
            .Where(t => t.ExpiresAt > cutoff)
            .OrderByDescending(t => t.CreatedAt)
            .Take(100)
            .ToListAsync();

        var machineNames = await _db.Machines.ToDictionaryAsync(m => m.Id, m => m.Name);

        return rows.Select(t => new EnrollmentDto(
            t.Id,
            t.MachineName,
            t.CreatedAt,
            t.ExpiresAt,
            t.RedeemedAt,
            t.RedeemedByMachineId is { } id ? machineNames.GetValueOrDefault(id) : null)).ToList();
    }

    /// <summary>Revoke a token. Deleting a redeemed one only drops the record; it does not
    /// un-issue the API key (delete the machine for that).</summary>
    public async Task<bool> RevokeAsync(Guid id)
    {
        var token = await _db.EnrollmentTokens.FindAsync(id);
        if (token is null) return false;
        _db.EnrollmentTokens.Remove(token);
        _db.AuditLogs.Add(NewAudit("enrollment.revoke", Who(token)));
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Delete tokens whose window closed more than <see cref="ListRetention"/> ago, so the console's
    /// enrollment list does not accumulate dead files forever. The audit log is the durable record:
    /// a token that was never redeemed is logged as <c>enrollment.expire</c> on the way out (a
    /// redeemed one already logged <c>enrollment.redeem</c>, a revoked one is deleted at revoke time).
    /// Called on the hourly maintenance sweep. Returns how many rows were removed.
    /// </summary>
    public async Task<int> PruneExpiredAsync()
    {
        var cutoff = DateTime.UtcNow - ListRetention;
        var stale = await _db.EnrollmentTokens
            .Where(t => t.ExpiresAt < cutoff)
            .ToListAsync();
        if (stale.Count == 0) return 0;

        foreach (var t in stale)
        {
            if (t.RedeemedAt is null)
                _db.AuditLogs.Add(NewAudit("enrollment.expire", ExpiredDetail(t)));
            _db.EnrollmentTokens.Remove(t);
        }
        await _db.SaveChangesAsync();
        return stale.Count;
    }

    /// <summary>
    /// Spend a token for a machine API key. Returns null with a reason when the token is unknown,
    /// expired or already spent — the caller must not distinguish those to the client beyond the
    /// message, since all three mean the same thing: this file is no good.
    /// </summary>
    public async Task<(RedeemEnrollmentResponse? Result, string? Error)> RedeemAsync(RedeemEnrollmentRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Token))
            return (null, "Enrollment token is required.");

        var hash = Tokens.Hash(req.Token.Trim());
        var token = await _db.EnrollmentTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (token is null)
            return (null, "Unknown enrollment token. Mint a new enrollment file from the console.");
        if (token.RedeemedAt is not null)
            return (null, "This enrollment file has already been used. Mint a new one from the console.");
        if (token.ExpiresAt <= DateTime.UtcNow)
            return (null, "This enrollment file has expired. Mint a new one from the console.");

        // The name on the token is binding when set: a token minted for the Deck cannot be spent to
        // claim the desktop's identity. Only an unbound token lets the agent name itself.
        var name = token.MachineName
                   ?? (string.IsNullOrWhiteSpace(req.MachineName) ? null : req.MachineName.Trim());
        if (string.IsNullOrWhiteSpace(name))
            return (null, "No machine name: this token was not minted for a machine, so the agent must supply one.");

        // Burn the token before issuing anything, conditionally on it still being unspent, so two
        // agents racing the same file cannot both come away with a key. ExecuteUpdate is its own
        // transaction; a zero row count means the other one won.
        var burnt = await _db.EnrollmentTokens
            .Where(t => t.Id == token.Id && t.RedeemedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RedeemedAt, DateTime.UtcNow));
        if (burnt == 0)
            return (null, "This enrollment file has already been used. Mint a new one from the console.");

        // Registering an existing name ROTATES its key. That is the intended re-enrollment path (a
        // wiped Deck gets a fresh file and comes back as itself) and it is authorised by the token,
        // exactly as the admin password authorises re-registration on /api/machines/register.
        var reg = await _sync.RegisterMachineAsync(name);

        await _db.EnrollmentTokens
            .Where(t => t.Id == token.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RedeemedByMachineId, reg.MachineId));

        _db.AuditLogs.Add(NewAudit("enrollment.redeem", name, reg.MachineId));
        await _db.SaveChangesAsync();

        return (new RedeemEnrollmentResponse(reg.MachineId, reg.ApiKey, name), null);
    }

    private static AuditLog NewAudit(string action, string? detail, Guid? machineId = null) => new()
    {
        Id = Guid.NewGuid(),
        Timestamp = DateTime.UtcNow,
        MachineId = machineId,
        Action = action,
        Detail = detail
    };

    private static string Who(EnrollmentToken t) => t.MachineName ?? "(any machine)";

    // Record the expiry on the audit trail so troubleshooting can confirm when a file was valid,
    // even after the token row itself is pruned from the list.
    private static string MintDetail(EnrollmentToken t) =>
        $"{Who(t)} (expires {t.ExpiresAt:yyyy-MM-dd HH:mm} UTC)";

    private static string ExpiredDetail(EnrollmentToken t) =>
        $"{Who(t)} (expired {t.ExpiresAt:yyyy-MM-dd HH:mm} UTC, never used)";
}
