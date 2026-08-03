using System.Text.Json;
using System.Text.RegularExpressions;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Orchestrates one Entra resolution cycle across two independent sources — the device
/// extensionAttributes, resolved per **named attribute scheme** (<see cref="EntraOptions.AttributeSchemes"/>),
/// and group membership, resolved per **named group instance** (<see cref="GroupTeamsOptions.Instances"/>)
/// plus a fleet-wide global exclusion — applies the per-key grace state machine, and atomically
/// writes {CacheRootPath}\resolved-teams.json.
///
/// Per-key / persistent-vs-transient grace: a 403 on either read is persistent (clean removal +
/// Error), a device-not-found is authoritative (removal), and transient failures ride the grace
/// window per key. All keys are evaluated from the single device fetch; all active group instances
/// are evaluated from one batched Graph membership check. The device fetch is skipped entirely when
/// nothing is configured to resolve (no attribute schemes and no group instances).
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
    // exist as a registry subkey — so an attribute scheme's instance id is restricted to a safe
    // charset. (Group instances need no such check — their id is DERIVED via canonicalization, not
    // authored; the dictionary key there is an operator-facing label only.)
    private static readonly Regex SchemeNamePattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    /// <summary>
    /// One validated, active group instance: the operator-facing label (diagnostics only), the
    /// derived (Source, SourceId) instance id, and the resolved inclusion/exclusion group names.
    /// </summary>
    private readonly record struct GroupInstance(string Label, string Id, string InclusionGroup, string? ExclusionGroup);

    // Previous cycle's written-set summary. EntraTeamResolutionService is a singleton and cycles run
    // sequentially, so a plain field is safe and survives across cycles — same pattern as
    // SyncService._lastVerifyResult. Drives the change-gated Information/Debug summary line.
    private string? _lastSummary;

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
                _lastSummary = null;   // next enable is always reported fresh, not "unchanged"
                return;
            }

            var (activeKeys, schemeNames, groupInstances) = ComputeActiveKeys();

            IReadOnlyList<EntraSourceOutcome> outcomes;
            if (schemeNames.Count == 0 && groupInstances.Count == 0)
            {
                // Nothing configured to resolve — skip the Graph round trip entirely. activeKeys is
                // empty in this case, so the merge below prunes every existing entry regardless of
                // source with no special-casing needed here.
                logger.LogDebug("Entra enabled but no attribute scheme and no group instance configured — skipping device fetch.");
                outcomes = [];
            }
            else
            {
                outcomes = await DetermineOutcomesAsync(schemeNames, groupInstances, activeKeys, ct);
            }

            // Read existing entries (empty if absent/unreadable).
            var existing = ReadExisting();

            // Merge all per-key outcomes through the grace state machine.
            var merged = EntraResolvedTeamsMerger.Merge(
                existing, outcomes, activeKeys,
                DateTime.UtcNow, TimeSpan.FromMinutes(config.Entra.GracePeriodMinutes));

            // Policy cap — deliberately applied here, not inside the merger, which stays a pure
            // grace state machine with no truncation or ordering policy of its own.
            var capped = ApplyCap(merged);

            LogSummary(capped);

            // Atomic write.
            WriteAtomically(new ResolvedTeamsFile
            {
                GeneratedUtc = DateTime.UtcNow,
                Teams        = capped
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
    /// validly-named scheme, plus one Group key per validated group instance (id derived from
    /// <c>InclusionGroup</c> via <see cref="TeamFolderNameCanonicalizer"/>). Computed fresh each
    /// cycle, before the device fetch, so it is available even when Graph is unreachable — an
    /// instance removed from (or retargeted in) configuration prunes its old entries without grace
    /// on the very next cycle. Both scheme and instance ordering is deterministic
    /// (ordinal-ignore-case by label), which matters for collision tie-breaking.
    /// </summary>
    private (IReadOnlySet<EntraSourceKey> ActiveKeys, IReadOnlyList<string> SchemeNames, IReadOnlyList<GroupInstance> GroupInstances)
        ComputeActiveKeys()
    {
        var keys = new HashSet<EntraSourceKey>();
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

        var groupInstances = new List<GroupInstance>();
        var claimedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // id -> claiming label

        foreach (var (label, options) in config.Entra.GroupTeams.Instances
                     .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(kv => (kv.Key, kv.Value)))
        {
            if (string.IsNullOrWhiteSpace(options.InclusionGroup))
            {
                logger.LogWarning("Entra group instance '{Label}' has no InclusionGroup — skipped.", label);
                continue;
            }

            var id = TeamFolderNameCanonicalizer.Canonicalize(options.InclusionGroup);
            if (string.IsNullOrEmpty(id))
            {
                logger.LogWarning(
                    "Entra group instance '{Label}': InclusionGroup '{Group}' canonicalizes to an empty id — skipped.",
                    label, options.InclusionGroup);
                continue;
            }

            if (claimedIds.TryGetValue(id, out var firstLabel))
            {
                logger.LogWarning(
                    "Entra group instance '{Label}' derives the same id '{Id}' as an already-active instance ('{ClaimingLabel}' wins by label order) — skipped.",
                    label, id, firstLabel);
                continue;
            }

            claimedIds[id] = label;
            groupInstances.Add(new GroupInstance(label, id, options.InclusionGroup, options.ExclusionGroup));
            keys.Add(new EntraSourceKey(ResolvedTeamSource.Group, id));
        }

        return (keys, schemeNames, groupInstances);
    }

    // ── Determine this cycle's per-key outcomes ───────────────────────────────

    private async Task<IReadOnlyList<EntraSourceOutcome>> DetermineOutcomesAsync(
        IReadOnlyList<string> schemeNames, IReadOnlyList<GroupInstance> groupInstances,
        IReadOnlySet<EntraSourceKey> activeKeys, CancellationToken ct)
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
                var outcomes = new List<EntraSourceOutcome>(schemeNames.Count + groupInstances.Count);
                foreach (var name in schemeNames)
                    outcomes.Add(ResolveAttributeOutcome(fetch, name));
                outcomes.AddRange(await ResolveGroupOutcomesAsync(fetch, groupInstances, ct));
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

    // ── Group source (per instance, one batched Graph call) ──────────────────

    private async Task<IReadOnlyList<EntraSourceOutcome>> ResolveGroupOutcomesAsync(
        EntraDeviceFetch fetch, IReadOnlyList<GroupInstance> activeInstances, CancellationToken ct)
    {
        if (activeInstances.Count == 0) return [];

        // checkMemberGroups needs the device object id; absent on Found is unexpected → transient
        // for every active group instance.
        if (string.IsNullOrWhiteSpace(fetch.DeviceObjectId))
        {
            logger.LogWarning("Entra device found but object id missing — group check treated as Unreachable.");
            return activeInstances
                .Select(i => new EntraSourceOutcome(
                    new EntraSourceKey(ResolvedTeamSource.Group, i.Id), EntraCycleResult.Unreachable, null))
                .ToList();
        }

        var globalExclusion = config.Entra.GroupTeams.ExclusionGroup;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in activeInstances)
        {
            names.Add(inst.InclusionGroup);
            if (!string.IsNullOrWhiteSpace(inst.ExclusionGroup))
                names.Add(inst.ExclusionGroup);
        }
        if (!string.IsNullOrWhiteSpace(globalExclusion))
            names.Add(globalExclusion);

        var snapshot = await groupClient.EvaluateAsync(fetch.DeviceObjectId, names, ct);

        // Fail-closed mitigation: exactly one Error line per cycle when the fleet-wide kill switch's
        // own name can't be resolved — never Debug, never gated on change, never repeated per instance.
        var globalExclusionUnresolved = NameUnresolved(snapshot, globalExclusion);
        if (globalExclusionUnresolved)
            logger.LogError(
                "Entra global group exclusion '{Group}' could not be resolved — suppressing ALL group instances this cycle.",
                globalExclusion);

        var outcomes = new List<EntraSourceOutcome>(activeInstances.Count);
        foreach (var inst in activeInstances)
        {
            var key    = new EntraSourceKey(ResolvedTeamSource.Group, inst.Id);
            var mapped = GroupOutcomeMapper.Map(snapshot, key, inst.InclusionGroup, inst.ExclusionGroup, globalExclusion);
            LogGroupInstanceOutcome(inst, mapped, snapshot, globalExclusionUnresolved);
            outcomes.Add(mapped);
        }

        return outcomes;
    }

    private void LogGroupInstanceOutcome(
        GroupInstance inst, EntraSourceOutcome mapped, EntraGroupSnapshot snapshot, bool globalExclusionUnresolved)
    {
        switch (mapped.Result)
        {
            case EntraCycleResult.ResolvedTeam:
                logger.LogDebug("Entra group instance '{Label}' → team '{Team}'.", inst.Label, mapped.Team);
                break;

            case EntraCycleResult.Unreachable:
                logger.LogDebug("Entra group instance '{Label}': Unreachable this cycle (grace applies).", inst.Label);
                break;

            case EntraCycleResult.NoTeam:
                if (globalExclusionUnresolved)
                    logger.LogDebug("Entra group instance '{Label}': suppressed by the unresolved global exclusion.", inst.Label);
                else if (NameUnresolved(snapshot, inst.InclusionGroup))
                    logger.LogWarning("Entra group instance '{Label}': inclusion group unresolved — removing.", inst.Label);
                else if (NameUnresolved(snapshot, inst.ExclusionGroup))
                    logger.LogWarning("Entra group instance '{Label}': exclusion group unresolved — removing.", inst.Label);
                else if (snapshot.Status == EntraGroupStatus.PermissionDenied)
                    logger.LogDebug("Entra group instance '{Label}': no team (permission denied — see Error above).", inst.Label);
                else
                    logger.LogDebug("Entra group instance '{Label}': no team (clean answer).", inst.Label);
                break;
        }
    }

    private static bool NameUnresolved(EntraGroupSnapshot snapshot, string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        snapshot.NameStatus.TryGetValue(name, out var status) &&
        status is EntraGroupStatus.NameNotFound or EntraGroupStatus.NameAmbiguous;

    private static IReadOnlyList<EntraSourceOutcome> AllUnreachable(IReadOnlySet<EntraSourceKey> keys) =>
        keys.Select(k => new EntraSourceOutcome(k, EntraCycleResult.Unreachable, null)).ToList();

    private static IReadOnlyList<EntraSourceOutcome> AllNoTeam(IReadOnlySet<EntraSourceKey> keys) =>
        keys.Select(k => new EntraSourceOutcome(k, EntraCycleResult.NoTeam, null)).ToList();

    // ── Team-count cap ───────────────────────────────────────────────────────

    /// <summary>
    /// Truncates to <see cref="EntraOptions.MaxDynamicTeams"/> (0 = no cap) in a deterministic order —
    /// Attribute entries before Group entries (the <see cref="ResolvedTeamSource"/> declaration
    /// order), each ordered by <c>SourceId</c> (ordinal-ignore-case) — since the merger's output
    /// order is not contractual. Dropped entries are simply absent from the written file; their
    /// grace state is not preserved, so if fewer teams resolve on a later cycle they reappear
    /// normally with a fresh LastConfirmedUtc rather than resuming a stale grace window.
    /// </summary>
    private List<ResolvedTeamEntry> ApplyCap(List<ResolvedTeamEntry> merged)
    {
        var cap = config.Entra.MaxDynamicTeams;
        if (cap <= 0 || merged.Count <= cap) return merged;

        var ordered = merged
            .OrderBy(e => e.Source)
            .ThenBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var kept    = ordered.Take(cap).ToList();
        var dropped = ordered.Skip(cap).Select(e => $"{e.Source}:{e.SourceId}").ToList();

        logger.LogWarning(
            "Entra dynamic team cap ({Cap}) exceeded — {Resolved} resolved, dropping: {Dropped}.",
            cap, ordered.Count, string.Join(", ", dropped));

        return kept;
    }

    // ── Change-gated summary log ────────────────────────────────────────────

    /// <summary>
    /// One line per cycle summarizing the written team set, in the same deterministic order as
    /// <see cref="ApplyCap"/>. Logged at Information when the set (including each entry's
    /// <see cref="ResolvedTeamState"/>) differs from the previous cycle, Debug otherwise — this is
    /// the primary operator-facing signal; per-instance Debug lines and all Warning/Error logging
    /// are unaffected. The empty set is a legitimate state: transitioning to zero dynamic teams
    /// still logs once at Information.
    /// </summary>
    private void LogSummary(List<ResolvedTeamEntry> written)
    {
        var summary = string.Join(", ", written
            .OrderBy(e => e.Source)
            .ThenBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(e => $"{e.Source}/{e.SourceId}={e.TeamFolderName} ({e.State})"));

        var display = summary.Length == 0 ? "(none)" : summary;
        var changed = !string.Equals(_lastSummary, summary, StringComparison.Ordinal);
        _lastSummary = summary;

        if (changed)
            logger.LogInformation("Entra dynamic teams: {Summary}", display);
        else
            logger.LogDebug("Entra dynamic teams (unchanged): {Summary}", display);
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
