using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Picks up session-*.json files written by NewsViewer to the local uploads
/// folder and copies them to the repository for centralised collection.
/// Files are deleted from the local uploads folder after a successful copy.
/// </summary>
public sealed class TelemetryUploader(
    CacheManager cache,
    ServiceConfiguration config,
    ILogger<TelemetryUploader> logger)
{
    public Task ProcessAsync(CancellationToken ct)
    {
        var files = cache.ListUploadFiles().ToList();
        if (files.Count == 0) return Task.CompletedTask;

        var sharePath = config.Repository.SharePath;
        if (string.IsNullOrWhiteSpace(sharePath))
        {
            logger.LogDebug("Telemetry upload skipped — Repository:SharePath is not configured");
            return Task.CompletedTask;
        }

        var uploadsDest = Path.Combine(sharePath, "uploads");
        try { Directory.CreateDirectory(uploadsDest); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot create repository uploads folder: {Folder}", uploadsDest);
            return Task.CompletedTask;
        }

        int uploaded = 0;
        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var destPath = Path.Combine(uploadsDest, Path.GetFileName(filePath));
                File.Copy(filePath, destPath, overwrite: true);
                cache.DeleteFile(filePath);
                uploaded++;
                logger.LogDebug("Telemetry uploaded: {File}", Path.GetFileName(filePath));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to upload telemetry: {File}", Path.GetFileName(filePath));
            }
        }

        if (uploaded > 0)
            logger.LogInformation("Telemetry: {Count}/{Total} file(s) uploaded", uploaded, files.Count);

        return Task.CompletedTask;
    }
}
