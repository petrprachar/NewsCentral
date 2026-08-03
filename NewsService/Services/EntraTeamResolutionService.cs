using System.Text.Json;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Orchestrates one Entra resolution cycle across two independent sources — the device
/// extensionAttributes (attribute source) and group membership (group source) — applies the
/// per-source grace state machine, and atomically writes {CacheRootPath}\resolved-teams.json.
///
/// Per-source / persistent-vs-transient grace: a 403 on either read is persistent (clean removal +
/// Error), a device-not-found is authoritative (removal), and transient failures ride the grace
/// window per source. Both sources are evaluated from the single device fetch (the group source
/// adds one checkMemberGroups call).
///
/// Never throws to the caller: any failure is logged and treated as Unreachable so a Graph problem
/// can never stall or fail blob sync.
/// </summary>
public sealed class EntraTeamResolutionService(
    ServiceConfiguration config,
    IDeviceIdentityProvider deviceIdentity,
    IEntraDeviceClient deviceClient,
    IEntraGroupClient groupClient,
    ILogger<EntraTeamResolutionService> logger)
{
    private const string ResolvedTeamsFileName = "resolved-teams.json";

    private static readonly JsonSerializerOptions Json = JsonDefaults.Options;

    // The configured instance set. Fixed to the two legacy singletons for now; a later change
    // derives it from Entra:AttributeSchemes / Entra:GroupTeams so removing an instance from
    // configuration prunes its entries.
    private static readonly IReadOnlySet<EntraSourceKey> ActiveKeys =
        new HashSet<EntraSourceKey>
        {
            EntraSourceKey.Legacy(ResolvedTeamSource.Attribute),
            EntraSourceKey.Legacy(ResolvedTeamSource.Group)
        };

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

            var outcomes = await DetermineOutcomesAsync(ct);

            // 5. Read existing entries (empty if absent/unreadable).
            var existing = ReadExisting();

            // 6. Merge both per-source outcomes through the grace state machine.
            var merged = EntraResolvedTeamsMerger.Merge(
                existing, outcomes, ActiveKeys,
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

    // ── Determine this cycle's per-source outcomes ────────────────────────────

    private async Task<IReadOnlyList<EntraSourceOutcome>> DetermineOutcomesAsync(CancellationToken ct)
    {
        // 2. Device id. Must be present AND a GUID — otherwise skip Graph entirely (both Unreachable).
        string? deviceId;
        try
        {
            deviceId = deviceIdentity.TryGetAzureAdDeviceId();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Device identity lookup failed — both sources Unreachable.");
            return BothUnreachable();
        }

        if (string.IsNullOrWhiteSpace(deviceId) || !Guid.TryParse(deviceId, out _))
        {
            logger.LogWarning("Azure AD DeviceId missing or not a GUID — both sources Unreachable (skipping Graph).");
            return BothUnreachable();
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
            logger.LogError(ex, "Entra device client unavailable (credentials?) — both sources Unreachable.");
            return BothUnreachable();
        }

        // 4. Map the device-level result to BOTH sources.
        switch (fetch.Outcome)
        {
            case EntraFetchOutcome.Found:
                var attribute = ResolveAttributeOutcome(fetch);
                var group     = await ResolveGroupOutcomeAsync(fetch, ct);
                return [attribute, group];

            case EntraFetchOutcome.NotFound:
                logger.LogWarning("Entra device object not found — removing any dynamic team (both sources).");
                return BothNoTeam();

            case EntraFetchOutcome.PermissionDenied:
                logger.LogError("Entra device read denied (403) — removing any dynamic team (both sources).");
                return BothNoTeam();

            default: // Unreachable
                logger.LogWarning("Entra device unreachable — grace window applies to both sources.");
                return BothUnreachable();
        }
    }

    // ── Attribute source ──────────────────────────────────────────────────────

    private static readonly EntraSourceKey AttributeKey = EntraSourceKey.Legacy(ResolvedTeamSource.Attribute);
    private static readonly EntraSourceKey GroupKey     = EntraSourceKey.Legacy(ResolvedTeamSource.Group);

    private EntraSourceOutcome ResolveAttributeOutcome(EntraDeviceFetch fetch)
    {
        var outcome = EntraTeamNameResolver.Resolve(fetch.Attributes!, config.Entra.Mappings);
        switch (outcome.Reason)
        {
            case EntraResolutionReason.Resolved:
                logger.LogInformation("Entra attribute team resolved {Team}.", outcome.TeamFolderName);
                return new(AttributeKey, EntraCycleResult.ResolvedTeam, outcome.TeamFolderName);

            case EntraResolutionReason.UnknownSelector:
            case EntraResolutionReason.InvalidRule:
            case EntraResolutionReason.EmptyRequiredAttribute:
                logger.LogWarning("Entra device read OK but no attribute team — {Reason}.", outcome.Reason);
                return new(AttributeKey, EntraCycleResult.NoTeam, null);

            default: // NoSelector
                logger.LogInformation("Entra device has no attribute selector — {Reason}.", outcome.Reason);
                return new(AttributeKey, EntraCycleResult.NoTeam, null);
        }
    }

    // ── Group source ────────────────────────────────────────────────────────────

    private async Task<EntraSourceOutcome> ResolveGroupOutcomeAsync(EntraDeviceFetch fetch, CancellationToken ct)
    {
        var inclusion = config.Entra.GroupTeam.InclusionGroup;
        var exclusion = config.Entra.GroupTeam.ExclusionGroup;

        // Disabled/cleared group feature emits Group NoTeam (not omit the source) so any stale group
        // team is removed promptly.
        if (string.IsNullOrWhiteSpace(inclusion))
        {
            logger.LogDebug("Entra group team disabled (no inclusion group) — emitting Group NoTeam.");
            return new(GroupKey, EntraCycleResult.NoTeam, null);
        }

        // checkMemberGroups needs the device object id; absent on Found is unexpected → transient.
        if (string.IsNullOrWhiteSpace(fetch.DeviceObjectId))
        {
            logger.LogWarning("Entra device found but object id missing — group check treated as Unreachable.");
            return new(GroupKey, EntraCycleResult.Unreachable, null);
        }

        var eval   = await groupClient.EvaluateAsync(fetch.DeviceObjectId, inclusion, exclusion, ct);
        var mapped = GroupOutcomeMapper.Map(eval, GroupKey, inclusion, exclusion);

        switch (eval.Status)
        {
            case EntraGroupStatus.Success when mapped.Result == EntraCycleResult.ResolvedTeam:
                logger.LogDebug("Entra group team resolved {Team}.", mapped.Team);
                break;
            case EntraGroupStatus.Success:
                logger.LogInformation(
                    "Entra group evaluated — no team (inInclusion={Inc}, inExclusion={Exc}).",
                    eval.InInclusion, eval.InExclusion);
                break;
            case EntraGroupStatus.PermissionDenied:
                logger.LogError("Entra group check denied (403) — removing group team.");
                break;
            case EntraGroupStatus.NameAmbiguous:
            case EntraGroupStatus.NameNotFound:
                logger.LogWarning("Entra group name unresolved ({Status}) — removing group team.", eval.Status);
                break;
            default: // Unreachable
                logger.LogWarning("Entra group check unreachable — grace window applies.");
                break;
        }

        return mapped;
    }

    private static IReadOnlyList<EntraSourceOutcome> BothUnreachable() =>
    [
        new(AttributeKey, EntraCycleResult.Unreachable, null),
        new(GroupKey,     EntraCycleResult.Unreachable, null)
    ];

    private static IReadOnlyList<EntraSourceOutcome> BothNoTeam() =>
    [
        new(AttributeKey, EntraCycleResult.NoTeam, null),
        new(GroupKey,     EntraCycleResult.NoTeam, null)
    ];

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
