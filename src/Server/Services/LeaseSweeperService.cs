namespace SaveLocker.Server.Services;

/// <summary>
/// The server's hourly maintenance sweep. It:
/// <list type="bullet">
/// <item>Removes expired leases. Without this, a stale lease would linger in the DB until the next
/// per-game query happens to touch it via <see cref="SyncService.ActiveLeaseAsync"/> — meaning a
/// machine that crashes mid-session without releasing its lease could block others for up to 6 hours
/// even after the lease window passes. The sweeper closes that gap without waiting for a query.</item>
/// <item>Prunes enrollment tokens whose window closed over a day ago
/// (<see cref="EnrollmentService.ListRetention"/>), so the console's enrollment list does not fill up
/// with dead files. The audit log keeps the permanent record.</item>
/// </list>
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

            var enrollment = scope.ServiceProvider.GetRequiredService<EnrollmentService>();
            var pruned = await enrollment.PruneExpiredAsync();
            if (pruned > 0)
                _log.LogInformation("Enrollment sweep: pruned {Count} stale token(s).", pruned);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Lease sweep failed.");
        }
    }
}
