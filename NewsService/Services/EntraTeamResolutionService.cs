using System.Text.Json;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Orchestrates one Entra resolution cycle: read this machine's device, resolve at most one
/// dynamic team, apply the grace state machine, and atomically write
/// {CacheRootPath}\resolved-teams.json. Phase 2 produces the file only — consuming it
/// (dynamic-team content sync, NewsViewer display) is Phase 3.
///
/// Never throws to the caller: any failure is logged and treated as an Unreachable cycle so a
/// Graph problem can never stall or fail blob sync.
/// </summary>
public sealed class EntraTeamResolutionService(
    ServiceConfiguration config,
    IDeviceIdentityProvider deviceIdentity,
    IEntraDeviceClient deviceClient,
    ILogger<EntraTeamResolutionService> logger)
{
    private const string ResolvedTeamsFileName = "resolved-teams.json";

    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    private string FilePath =>
        Path.Combine(config.Service.CacheRootPath, ResolvedTeamsFileName);

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            // 1. Disabled → remove any stale file and return.
            if (!config.Entra.Enabled)
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                    logger.LogInformation("Entra disabled — removed stale {File}.", ResolvedTeamsFileName);
                }
                return;
            }

            var (result, resolvedTeam) = await DetermineOutcomeAsync(ct);

            // 5. Read existing entries (empty if absent/unreadable).
            var existing = ReadExisting();

            // 6. Merge through the grace state machine.
            var merged = EntraResolvedTeamsMerger.Merge(
                existing, result, resolvedTeam,
                DateTime.UtcNow, TimeSpan.FromMinutes(config.Entra.GracePeriodMinutes));

            // 7. Atomic write.
            WriteAtomically(new ResolvedTeamsFile
            {
                GeneratedUtc = DateTime.UtcNow,
                Teams        = merged
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Entra resolution cycle failed.");
        }
    }

    // ── Determine this cycle's outcome ───────────────────────────────────────

    private async Task<(EntraCycleResult Result, string? Team)> DetermineOutcomeAsync(CancellationToken ct)
    {
        // 2. Device id.
        string? deviceId;
        try
        {
            deviceId = deviceIdentity.TryGetAzureAdDeviceId();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Device identity lookup failed — treating cycle as Unreachable.");
            return (EntraCycleResult.Unreachable, null);
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            logger.LogWarning("Could not determine Azure AD DeviceId — treating cycle as Unreachable.");
            return (EntraCycleResult.Unreachable, null);
        }

        // 3. Fetch device. Missing creds (factory throws) → Error, treat as Unreachable.
        EntraDeviceFetch fetch;
        try
        {
            fetch = await deviceClient.FetchAsync(deviceId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Entra device client unavailable (credentials?) — treating cycle as Unreachable.");
            return (EntraCycleResult.Unreachable, null);
        }

        // 4. Map fetch outcome to a cycle result.
        switch (fetch.Outcome)
        {
            case EntraFetchOutcome.Found:
                var outcome = EntraTeamNameResolver.Resolve(fetch.Attributes!, config.Entra.Mappings);
                switch (outcome.Reason)
                {
                    case EntraResolutionReason.Resolved:
                        logger.LogInformation("Entra resolved dynamic team {Team}.", outcome.TeamFolderName);
                        return (EntraCycleResult.ResolvedTeam, outcome.TeamFolderName);

                    case EntraResolutionReason.UnknownSelector:
                    case EntraResolutionReason.InvalidRule:
                    case EntraResolutionReason.EmptyRequiredAttribute:
                        logger.LogWarning("Entra device read OK but no team resolved — {Reason}.", outcome.Reason);
                        return (EntraCycleResult.NoTeam, null);

                    default: // NoSelector
                        logger.LogInformation("Entra device has no team selector — {Reason}.", outcome.Reason);
                        return (EntraCycleResult.NoTeam, null);
                }

            case EntraFetchOutcome.NotFound:
                logger.LogWarning("Entra device object not found — removing any dynamic team.");
                return (EntraCycleResult.NoTeam, null);

            default: // Unreachable
                logger.LogWarning("Entra device unreachable — grace window applies.");
                return (EntraCycleResult.Unreachable, null);
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private IReadOnlyList<ResolvedTeamEntry> ReadExisting()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var json = File.ReadAllText(FilePath);
            var file = JsonSerializer.Deserialize<ResolvedTeamsFile>(json, Json);
            return file?.Teams ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read existing {File} — treating as empty.", ResolvedTeamsFileName);
            return [];
        }
    }

    private void WriteAtomically(ResolvedTeamsFile file)
    {
        Directory.CreateDirectory(config.Service.CacheRootPath);

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        File.Move(tmp, FilePath, overwrite: true);

        logger.LogDebug("Wrote {File} with {Count} team(s).", ResolvedTeamsFileName, file.Teams.Count);
    }
}
