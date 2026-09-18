using NewsCentral.Configuration;
using NewsService.Configuration;
using NewsService.Services;

namespace NewsService;

public sealed class Worker(
    ILogger<Worker> logger,
    SyncService syncService,
    IConfiguration configuration,
    ServiceConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.Service.PollIntervalSeconds);

        logger.LogInformation("NewsService starting. Poll interval: {Interval}s",
            config.Service.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // The effective team set is static (registry/appsettings) union dynamic
            // (Entra-resolved), and the dynamic half is not known until SyncService has
            // refreshed Entra INSIDE the cycle itself — so an empty static list here is not an
            // empty workload. A machine configured purely with dynamic teams would never
            // bootstrap if the cycle were skipped on an empty static list. Always run the cycle;
            // SyncService decides what an empty effective set means, once it actually knows.
            var teams = TeamConfigurationReader.GetTeams(configuration);
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
