namespace SaveLocker.Server.Services;

/// <summary>
/// Periodically checks the configured GitHub repository for a newer agent
/// installer and refreshes the server-hosted copy when enabled.
/// </summary>
public sealed class AgentInstallerPollerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentInstallerPollerService> _log;
    private readonly TimeSpan? _interval;

    public AgentInstallerPollerService(
        IConfiguration cfg,
        IServiceScopeFactory scopeFactory,
        ILogger<AgentInstallerPollerService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;

        var hours = cfg.GetValue<double?>("AgentUpdate:AutoFetchHours");
        _interval = hours is > 0 ? TimeSpan.FromHours(hours.Value) : null;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_interval is null)
        {
            _log.LogInformation(
                "GitHub installer auto-poll disabled (AgentUpdate:AutoFetchHours is not positive).");
            return;
        }

        _log.LogInformation(
            "GitHub installer auto-poll enabled; checking every {Hours:0.##} hour(s).",
            _interval.Value.TotalHours);

        try
        {
            // Check once immediately so enabling the feature does not require a
            // server restart to happen to land just before the next interval.
            await PollAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_interval.Value, ct);
                await PollAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var installer = scope.ServiceProvider.GetRequiredService<AgentInstallerService>();
            var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
            var before = installer.GetInfo();
            var after = await installer.FetchLatestFromGitHubAsync(http, ct, onlyIfNewer: true);

            if (before?.Version == after.Version && before.UploadedAt == after.UploadedAt)
                _log.LogDebug("GitHub installer auto-poll: hosted installer v{Version} is current.", after.Version);
            else
                _log.LogInformation("GitHub installer auto-poll: hosted installer updated to v{Version}.", after.Version);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "GitHub installer auto-poll failed.");
        }
    }
}
