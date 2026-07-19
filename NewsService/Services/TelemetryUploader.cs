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
/// <para>
/// Upload is gated by Telemetry:UploadEnabled (default true). Independently of the upload outcome,
/// a retention sweep deletes local files older than <see cref="TelemetryDefaults.RetentionDays"/> —
/// it runs on EVERY path (upload disabled, SharePath unconfigured, unreachable destination, failed
/// copies), which are precisely the cases where files previously accumulated without bound. The
/// rationale is data hygiene, not disk space: the records describe what a specific user saw and
/// when, and should not persist indefinitely on a workstation.
/// </para>
/// </summary>
public sealed class TelemetryUploader(
    CacheManager cache,
    ServiceConfiguration config,
    HmacService hmac,
    ILogger<TelemetryUploader> logger)
{
    // Test seams. The sweep decision reads time ONLY through these two funcs — never
    // DateTime.UtcNow or File.GetLastWriteTimeUtc directly — so tests inject both "now" and each
    // file's age without depending on real timestamps or the system clock.
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
    internal Func<string, DateTime> LastWriteUtc { get; init; } = File.GetLastWriteTimeUtc;

    public Task ProcessAsync(CancellationToken ct)
    {
        var files = cache.ListUploadFiles().ToList();
        if (files.Count == 0) return Task.CompletedTask;

        // Upload stage. Every precondition failure demotes to "skip upload" — it never skips the
        // retention sweep below. `removed` tracks files the upload stage already deleted (uploaded
        // or discarded) so the sweep only considers survivors.
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.Telemetry.UploadEnabled)
        {
            UploadFiles(files, removed, ct);
        }
        else
        {
            logger.LogDebug(
                "Telemetry upload disabled (Telemetry:UploadEnabled = false) — local retention still applies");
        }

        SweepExpired(files, removed, ct);
        return Task.CompletedTask;
    }

    private void UploadFiles(List<string> files, HashSet<string> removed, CancellationToken ct)
    {
        var sharePath = config.Repository.SharePath;
        if (string.IsNullOrWhiteSpace(sharePath))
        {
            logger.LogDebug("Telemetry upload skipped — Repository:SharePath is not configured");
            return;
        }

        var uploadsDest = Path.Combine(sharePath, "uploads");
        try { Directory.CreateDirectory(uploadsDest); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cannot create repository uploads folder: {Folder}", uploadsDest);
            return;
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
                    removed.Add(filePath);
                    discarded++;
                    continue;
                }

                var sigResult = hmac.Verify(record);
                if (sigResult == VerifyResult.Invalid)
                {
                    logger.LogWarning(
                        "Telemetry: {File} has invalid HMAC signature — discarding", fileName);
                    cache.DeleteFile(filePath);
                    removed.Add(filePath);
                    discarded++;
                    continue;
                }
                if (sigResult == VerifyResult.Unsigned)
                    logger.LogDebug("Telemetry: {File} carries no HMAC signature", fileName);

                var destPath = Path.Combine(uploadsDest, fileName);
                File.Copy(filePath, destPath, overwrite: true);
                cache.DeleteFile(filePath);
                removed.Add(filePath);
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
    }

    /// <summary>
    /// Unconditional retention safety net over the same enumeration the upload stage used: deletes
    /// survivors older than <see cref="TelemetryDefaults.RetentionDays"/> by file LastWriteTime
    /// (no deserialization, no HMAC — the files are written locally and never moved, so the
    /// filesystem timestamp is reliable). Upload runs first: in normal operation files are removed
    /// immediately after a successful copy and never reach the window at all. A failed delete logs
    /// Warning and continues — one locked file must never abort the sweep.
    /// </summary>
    private void SweepExpired(List<string> files, HashSet<string> alreadyRemoved, CancellationToken ct)
    {
        var cutoffUtc = UtcNow() - TimeSpan.FromDays(TelemetryDefaults.RetentionDays);

        int deleted = 0;
        foreach (var filePath in files)
        {
            if (ct.IsCancellationRequested) break;
            if (alreadyRemoved.Contains(filePath)) continue;
            try
            {
                if (LastWriteUtc(filePath) > cutoffUtc) continue;   // inside the window — keep
                cache.DeleteFile(filePath);
                deleted++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Telemetry retention: failed to delete {File} — continuing",
                    Path.GetFileName(filePath));
            }
        }

        if (deleted > 0)
            logger.LogInformation(
                "Telemetry retention: deleted {Count} local session file(s) older than {Days} days",
                deleted, TelemetryDefaults.RetentionDays);
        else
            logger.LogDebug("Telemetry retention: nothing outside the {Days}-day window",
                TelemetryDefaults.RetentionDays);
    }
}
