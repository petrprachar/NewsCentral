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
            var teams = TeamConfigurationReader.GetTeams(configuration);

            if (teams.Length == 0)
                logger.LogWarning(
                    "No teams configured. Add team folder names as REG_SZ values under " +
                    "HKLM\\Software\\{Company}\\{Solution}\\{Component}\\teams\\.",
                    SolutionConstants.Company, SolutionConstants.SolutionName, "NewsService");
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
