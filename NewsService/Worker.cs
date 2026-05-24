using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewsService;

public sealed class Worker(ILogger<Worker> logger, IConfiguration configuration) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int>("Service:PollIntervalSeconds", 60));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NewsService starting. Poll interval: {Interval}s", _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPollCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in poll cycle");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        logger.LogInformation("NewsService stopped.");
    }

    private Task RunPollCycleAsync(CancellationToken cancellationToken)
    {
        // TODO: implement sync logic
        // 1. Read index.json from repository for each configured team
        // 2. Retrieve updated content to local cache if changes detected
        // 3. Apply wallpaper / lockscreen if indicated by presentation flags
        // 4. Write status.json to cache root
        // 5. Process and upload session telemetry from uploads\

        logger.LogInformation("Poll cycle at {Time} — sync not yet implemented", DateTimeOffset.Now);
        return Task.CompletedTask;
    }
}
