namespace SaveLocker.Server.Services;

/// <summary>
/// Proactively removes expired leases on a 1-hour sweep. Without this, a stale
/// lease would linger in the DB until the next per-game query happens to touch it
/// via <see cref="SyncService.ActiveLeaseAsync"/> — meaning a machine that crashes
/// mid-session without releasing its lease could block others for up to 6 hours
/// even after the lease window passes. The sweeper closes that gap without waiting
/// for another query.
/// </summary>
public sealed class LeaseSweeperService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LeaseSweeperService> _log;

    public LeaseSweeperService(IServiceScopeFactory scopeFactory, ILogger<LeaseSweeperService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Interval, ct);
                await SweepAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
            var removed = await sync.SweepExpiredLeasesAsync();
            if (removed > 0)
                _log.LogInformation("Lease sweep: removed {Count} expired lease(s).", removed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Lease sweep failed.");
        }
    }
}
