namespace SaveLocker.Server.Services;

/// <summary>
/// Periodically checks the configured GitHub repository for a newer agent
/// installer and refreshes the server-hosted copy when enabled.
/// </summary>
public sealed class AgentInstallerPollerService : BackgroundService
{
    private static readonly TimeSpan ConfigurationCheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AgentInstallerPollerService> _log;

    public AgentInstallerPollerService(
        IServiceScopeFactory scopeFactory,
        ILogger<AgentInstallerPollerService> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            double? configuredHours = null;
            DateTime? nextPollAt = null;

            while (!ct.IsCancellationRequested)
            {
                var hours = await GetConfiguredHoursAsync(ct);
                if (configuredHours != hours)
                {
                    configuredHours = hours;
                    nextPollAt = null;
                    if (hours > 0)
                        _log.LogInformation(
                            "GitHub installer auto-poll enabled; checking every {Hours:0.##} hour(s).", hours);
                    else
                        _log.LogInformation(
                            "GitHub installer auto-poll disabled (AgentUpdate:AutoFetchHours is not positive).");
                }

                if (hours > 0 && (nextPollAt is null || DateTime.UtcNow >= nextPollAt.Value))
                {
                    // A newly enabled or reconfigured schedule checks immediately.
                    await PollAsync(ct);
                    nextPollAt = DateTime.UtcNow.AddHours(hours);
                }

                var untilNextPoll = nextPollAt is null
                    ? ConfigurationCheckInterval
                    : nextPollAt.Value - DateTime.UtcNow;
                await Task.Delay(
                    untilNextPoll < ConfigurationCheckInterval ? untilNextPoll : ConfigurationCheckInterval,
                    ct);
            }
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }

    private async Task<double> GetConfiguredHoursAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();
        return await settings.GetAutoFetchHoursAsync(ct);
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
