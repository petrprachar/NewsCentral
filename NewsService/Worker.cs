using NewsService.Configuration;
using NewsService.Services;

namespace NewsService;

public sealed class Worker(
    ILogger<Worker> logger,
    SyncService syncService,
    RegistryConfiguration registry,
    ServiceConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = registry.GetPollIntervalSeconds() ?? config.Service.PollIntervalSeconds;
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        logger.LogInformation("NewsService starting. Poll interval: {Interval}s", intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var teams = registry.GetTeams();

            if (teams.Length == 0)
                logger.LogWarning(
                    "No teams configured. Add a 'teams' REG_SZ value under " +
                    "HKLM\\Software\\{Company}\\{App} with semicolon-separated team folder names.",
                    config.Company, config.ApplicationName);
            else
                await RunCycleAsync(teams, stoppingToken);

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("NewsService stopped.");
    }

    private async Task RunCycleAsync(string[] teams, CancellationToken ct)
    {
        try
        {
            await syncService.RunCycleAsync(teams, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in poll cycle");
        }
    }
}
