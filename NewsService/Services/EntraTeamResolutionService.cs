using System.Text.Json;
using System.Text.RegularExpressions;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Orchestrates one Entra resolution cycle across two independent sources — the device
/// extensionAttributes, resolved per **named attribute scheme** (<see cref="EntraOptions.AttributeSchemes"/>),
/// and group membership (group source) — applies the per-key grace state machine, and atomically
/// writes {CacheRootPath}\resolved-teams.json.
///
/// Per-key / persistent-vs-transient grace: a 403 on either read is persistent (clean removal +
/// Error), a device-not-found is authoritative (removal), and transient failures ride the grace
/// window per key. All keys are evaluated from the single device fetch (the group source adds one
/// checkMemberGroups call). The device fetch is skipped entirely when nothing is configured to
/// resolve (no attribute schemes and no group inclusion).
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

    // A name containing ':' would corrupt the configuration path, and one containing '\' cannot
    // exist as a registry subkey — so a scheme's instance id is restricted to a safe charset.
    private static readonly Regex SchemeNamePattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private static readonly EntraSourceKey GroupKey = EntraSourceKey.Legacy(ResolvedTeamSource.Group);

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

            var (activeKeys, schemeNames) = ComputeActiveKeys();
            var hasGroupSource = !string.IsNullOrWhiteSpace(config.Entra.GroupTeam.InclusionGroup);

            IReadOnlyList<EntraSourceOutcome> outcomes;
            if (schemeNames.Count == 0 && !hasGroupSource)
            {
                // Nothing configured to resolve — skip the Graph round trip entirely. The group key
                // is still active (it's a configured, if inactive, instance), so it gets an explicit
                // clean NoTeam rather than being left to pass through unchanged; any stale attribute
                // entries are pruned by the activeKeys filter in the merger (no attribute key is active).
                logger.LogDebug("Entra enabled but no attribute scheme and no group configured — skipping device fetch.");
                outcomes = [new EntraSourceOutcome(GroupKey, EntraCycleResult.NoTeam, null)];
            }
            else
            {
                outcomes = await DetermineOutcomesAsync(schemeNames, activeKeys, ct);
            }

            // Read existing entries (empty if absent/unreadable).
            var existing = ReadExisting();

            // Merge all per-key outcomes through the grace state machine.
            var merged = EntraResolvedTeamsMerger.Merge(
                existing, outcomes, activeKeys,
                DateTime.UtcNow, TimeSpan.FromMinutes(config.Entra.GracePeriodMinutes));

            // Atomic write.
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

    // ── Configured instance set ─────────────────────────────────────────────────

    /// <summary>
    /// Derives the active (Source, SourceId) key set from configuration: one Attribute key per
    /// validly-named scheme, plus the Group legacy singleton key (the group source is not yet
    /// multi-instance). Computed fresh each cycle, before the device fetch, so it is available even
    /// when Graph is unreachable — a scheme removed from configuration prunes its entries without
    /// grace on the very next cycle. Schemes are returned in a deterministic (ordinal-ignore-case)
    /// order.
    /// </summary>
    private (IReadOnlySet<EntraSourceKey> ActiveKeys, IReadOnlyList<string> SchemeNames) ComputeActiveKeys()
    {
        var keys = new HashSet<EntraSourceKey> { GroupKey };
        var schemeNames = new List<string>();

        foreach (var name in config.Entra.AttributeSchemes.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(name) || !SchemeNamePattern.IsMatch(name))
            {
                logger.LogWarning(
                    "Entra attribute scheme name '{Name}' is invalid ([A-Za-z0-9._-]+ required) — skipped.", name);
                continue;
            }

            schemeNames.Add(name);
            keys.Add(new EntraSourceKey(ResolvedTeamSource.Attribute, name));
        }

        return (keys, schemeNames);
    }

    // ── Determine this cycle's per-key outcomes ───────────────────────────────

    private async Task<IReadOnlyList<EntraSourceOutcome>> DetermineOutcomesAsync(
        IReadOnlyList<string> schemeNames, IReadOnlySet<EntraSourceKey> activeKeys, CancellationToken ct)
    {
        // 2. Device id. Must be present AND a GUID — otherwise skip Graph entirely (all keys Unreachable).
        string? deviceId;
        try
        {
            deviceId = deviceIdentity.TryGetAzureAdDeviceId();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Device identity lookup failed — all sources Unreachable.");
            return AllUnreachable(activeKeys);
        }

        if (string.IsNullOrWhiteSpace(deviceId) || !Guid.TryParse(deviceId, out _))
        {
            logger.LogWarning("Azure AD DeviceId missing or not a GUID — all sources Unreachable (skipping Graph).");
            return AllUnreachable(activeKeys);
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
            logger.LogError(ex, "Entra device client unavailable (credentials?) — all sources Unreachable.");
            return AllUnreachable(activeKeys);
        }

        // 4. Map the device-level result to ALL keys.
        switch (fetch.Outcome)
        {
            case EntraFetchOutcome.Found:
                var outcomes = new List<EntraSourceOutcome>(schemeNames.Count + 1);
                foreach (var name in schemeNames)
                    outcomes.Add(ResolveAttributeOutcome(fetch, name));
                outcomes.Add(await ResolveGroupOutcomeAsync(fetch, ct));
                return outcomes;

            case EntraFetchOutcome.NotFound:
                logger.LogWarning("Entra device object not found — removing any dynamic team (all sources).");
                return AllNoTeam(activeKeys);

            case EntraFetchOutcome.PermissionDenied:
                logger.LogError("Entra device read denied (403) — removing any dynamic team (all sources).");
                return AllNoTeam(activeKeys);

            default: // Unreachable
                logger.LogWarning("Entra device unreachable — grace window applies to all sources.");
                return AllUnreachable(activeKeys);
        }
    }

    // ── Attribute source (per scheme) ─────────────────────────────────────────

    private EntraSourceOutcome ResolveAttributeOutcome(EntraDeviceFetch fetch, string schemeName)
    {
        var scheme = config.Entra.AttributeSchemes[schemeName];
        var key    = new EntraSourceKey(ResolvedTeamSource.Attribute, schemeName);

        var outcome = EntraTeamNameResolver.Resolve(fetch.Attributes!, scheme.Mappings, scheme.Selector);
        switch (outcome.Reason)
        {
            case EntraResolutionReason.Resolved:
                logger.LogDebug("Entra attribute scheme {Scheme} resolved {Team}.", schemeName, outcome.TeamFolderName);
                return new(key, EntraCycleResult.ResolvedTeam, outcome.TeamFolderName);

            case EntraResolutionReason.InvalidScheme:
                logger.LogWarning(
                    "Entra attribute scheme {Scheme} has an invalid selector configuration — no team.", schemeName);
                return new(key, EntraCycleResult.NoTeam, null);

            case EntraResolutionReason.UnknownSelector:
            case EntraResolutionReason.InvalidRule:
            case EntraResolutionReason.EmptyRequiredAttribute:
                logger.LogDebug(
                    "Entra attribute scheme {Scheme}: device read OK but no team — {Reason}.", schemeName, outcome.Reason);
                return new(key, EntraCycleResult.NoTeam, null);

            default: // NoSelector
                logger.LogDebug(
                    "Entra attribute scheme {Scheme}: device has no selector value — {Reason}.", schemeName, outcome.Reason);
                return new(key, EntraCycleResult.NoTeam, null);
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

    private static IReadOnlyList<EntraSourceOutcome> AllUnreachable(IReadOnlySet<EntraSourceKey> keys) =>
        keys.Select(k => new EntraSourceOutcome(k, EntraCycleResult.Unreachable, null)).ToList();

    private static IReadOnlyList<EntraSourceOutcome> AllNoTeam(IReadOnlySet<EntraSourceKey> keys) =>
        keys.Select(k => new EntraSourceOutcome(k, EntraCycleResult.NoTeam, null)).ToList();

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
