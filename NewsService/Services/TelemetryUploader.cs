using System.Text.Json;
using NewsCentral.Models;
using NewsCentral.Security;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Picks up session-*.json files written by NewsViewer to the local uploads
/// folder and copies them to the repository for centralised collection.
/// Files with an invalid HMAC signature are discarded locally and not uploaded.
/// Files are deleted from the local uploads folder after a successful copy.
/// </summary>
public sealed class TelemetryUploader(
    CacheManager cache,
    ServiceConfiguration config,
    HmacService hmac,
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
        int discarded = 0;
        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var fileName = Path.GetFileName(filePath);

                // Verify HMAC before forwarding to the repository.
                var json   = File.ReadAllText(filePath);
                var record = JsonSerializer.Deserialize<SessionTelemetry>(json, JsonDefaults.Options);

                if (record is null)
                {
                    logger.LogWarning("Telemetry: {File} could not be parsed — discarding", fileName);
                    cache.DeleteFile(filePath);
                    discarded++;
                    continue;
                }

                var sigResult = hmac.Verify(record);
                if (sigResult == VerifyResult.Invalid)
                {
                    logger.LogWarning(
                        "Telemetry: {File} has invalid HMAC signature — discarding", fileName);
                    cache.DeleteFile(filePath);
                    discarded++;
                    continue;
                }
                if (sigResult == VerifyResult.Unsigned)
                    logger.LogDebug("Telemetry: {File} carries no HMAC signature", fileName);

                var destPath = Path.Combine(uploadsDest, fileName);
                File.Copy(filePath, destPath, overwrite: true);
                cache.DeleteFile(filePath);
                uploaded++;
                logger.LogDebug("Telemetry uploaded: {File}", fileName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to upload telemetry: {File}",
                    Path.GetFileName(filePath));
            }
        }

        if (uploaded > 0 || discarded > 0)
            logger.LogInformation(
                "Telemetry: {Uploaded}/{Total} uploaded, {Discarded} discarded",
                uploaded, files.Count, discarded);

        return Task.CompletedTask;
    }
}
