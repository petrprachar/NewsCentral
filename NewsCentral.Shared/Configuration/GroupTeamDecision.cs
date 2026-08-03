namespace NewsCentral.Configuration;

/// <summary>
/// Pure, I/O-free decision for an Entra group-membership dynamic team (see
/// <c>docs/entra-group-team.md</c>): inclusion ∧ ¬exclusion ∧ ¬globalExclusion → a team named after
/// the inclusion group. NewsService-only; no Graph / registry / Windows / Azure dependencies.
///
/// The group-membership lookups (<paramref name="deviceInInclusion"/>,
/// <paramref name="deviceInExclusion"/>, <paramref name="deviceInGlobalExclusion"/>) are performed
/// by the caller. This helper holds only the inclusion/exclusion logic and the folder-name
/// canonicalization.
/// </summary>
public static class GroupTeamDecision
{
    /// <summary>
    /// Returns the canonical team folder name when the device is in the inclusion group and not in
    /// the (per-instance or fleet-wide global) exclusion group; otherwise <c>null</c> (feature
    /// inactive / not included / excluded).
    /// </summary>
    public static string? Resolve(
        string? inclusionGroup, string? exclusionGroup,
        bool deviceInInclusion, bool deviceInExclusion,
        string? globalExclusionGroup = null, bool deviceInGlobalExclusion = false)
    {
        // Feature inactive when no inclusion group is configured.
        if (string.IsNullOrWhiteSpace(inclusionGroup)) return null;

        if (!deviceInInclusion) return null;

        // Excluded only when an exclusion group is configured and the device is in it.
        if (!string.IsNullOrWhiteSpace(exclusionGroup) && deviceInExclusion) return null;

        // Fleet-wide kill switch — same "configured and a member" gate as the per-instance exclusion.
        if (!string.IsNullOrWhiteSpace(globalExclusionGroup) && deviceInGlobalExclusion) return null;

        return TeamFolderNameCanonicalizer.Canonicalize(inclusionGroup);
    }
}
