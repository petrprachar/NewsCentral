using NewsCentral.Configuration;
using NewsCentral.Models;

namespace NewsService.Services;

/// <summary>
/// Pure mapping from a group-membership snapshot to the per-instance <see cref="EntraSourceOutcome"/>
/// consumed by the merger. No I/O. Persistent failures (name not found / ambiguous / 403) map to
/// <c>NoTeam</c> (clean removal); transient unreachability maps to <c>Unreachable</c> (grace).
///
/// Evaluated in order, first match wins: transport-level Unreachable, transport-level
/// PermissionDenied, an unresolvable GLOBAL exclusion (fail-closed — suppresses every instance),
/// an unresolvable inclusion, an unresolvable per-instance exclusion, then the membership decision
/// itself (<see cref="GroupTeamDecision.Resolve"/>).
/// </summary>
public static class GroupOutcomeMapper
{
    public static EntraSourceOutcome Map(
        EntraGroupSnapshot snapshot,
        EntraSourceKey key,
        string inclusionName,
        string? instanceExclusionName,
        string? globalExclusionName)
    {
        if (snapshot.Status == EntraGroupStatus.Unreachable)
            return new EntraSourceOutcome(key, EntraCycleResult.Unreachable, null);

        if (snapshot.Status == EntraGroupStatus.PermissionDenied)
            return new EntraSourceOutcome(key, EntraCycleResult.NoTeam, null);

        // Fail-closed: an unresolvable fleet-wide exclusion suppresses EVERY group instance — the
        // kill switch must not silently stop killing just because its own name went bad.
        if (IsUnresolvable(snapshot, globalExclusionName))
            return new EntraSourceOutcome(key, EntraCycleResult.NoTeam, null);

        if (IsUnresolvable(snapshot, inclusionName))
            return new EntraSourceOutcome(key, EntraCycleResult.NoTeam, null);

        if (IsUnresolvable(snapshot, instanceExclusionName))
            return new EntraSourceOutcome(key, EntraCycleResult.NoTeam, null);

        var inInclusion         = snapshot.MemberOfNames.Contains(inclusionName);
        var inInstanceExclusion = !string.IsNullOrWhiteSpace(instanceExclusionName) &&
                                   snapshot.MemberOfNames.Contains(instanceExclusionName);
        var inGlobalExclusion   = !string.IsNullOrWhiteSpace(globalExclusionName) &&
                                   snapshot.MemberOfNames.Contains(globalExclusionName);

        var team = GroupTeamDecision.Resolve(
            inclusionName, instanceExclusionName,
            inInclusion, inInstanceExclusion,
            globalExclusionName, inGlobalExclusion);

        return team is not null
            ? new EntraSourceOutcome(key, EntraCycleResult.ResolvedTeam, team)
            : new EntraSourceOutcome(key, EntraCycleResult.NoTeam, null);
    }

    /// <summary>
    /// A configured (non-blank) name whose resolution status is a persistent failure. A name that
    /// is blank/not configured, or entirely absent from <see cref="EntraGroupSnapshot.NameStatus"/>,
    /// is not a failure — it simply doesn't participate.
    /// </summary>
    private static bool IsUnresolvable(EntraGroupSnapshot snapshot, string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        snapshot.NameStatus.TryGetValue(name, out var status) &&
        status is EntraGroupStatus.NameNotFound or EntraGroupStatus.NameAmbiguous;
}
