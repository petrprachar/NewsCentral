using System.Text.RegularExpressions;

namespace NewsCentral.Configuration;

/// <summary>
/// Pure, I/O-free decision for the Entra group-membership dynamic team (see
/// <c>docs/entra-group-team.md</c>): inclusion ∧ ¬exclusion → a team named after the inclusion
/// group. NewsService-only; no Graph / registry / Windows / Azure dependencies.
///
/// The group-membership lookups (<paramref name="deviceInInclusion"/>,
/// <paramref name="deviceInExclusion"/>) are performed by the caller (G2). This helper holds only
/// the inclusion/exclusion logic and the folder-name canonicalization.
/// </summary>
public static class GroupTeamDecision
{
    /// <summary>
    /// Returns the canonical team folder name when the device is in the inclusion group and not in
    /// the exclusion group; otherwise <c>null</c> (feature inactive / not included / excluded).
    /// </summary>
    public static string? Resolve(
        string? inclusionGroup, string? exclusionGroup,
        bool deviceInInclusion, bool deviceInExclusion)
    {
        // Feature inactive when no inclusion group is configured.
        if (string.IsNullOrWhiteSpace(inclusionGroup)) return null;

        if (!deviceInInclusion) return null;

        // Excluded only when an exclusion group is configured and the device is in it.
        if (!string.IsNullOrWhiteSpace(exclusionGroup) && deviceInExclusion) return null;

        return Canonicalize(inclusionGroup);
    }

    /// <summary>
    /// Mirrors <c>EntraTeamNameResolver.Canonicalize</c> / <c>TeamService.GenerateFolderName</c>
    /// (post prefix-removal) byte-for-byte: lower-invariant, ' ' and '_' → '-', then strip anything
    /// outside [a-z0-9-]. No hyphen collapsing or trimming; no prefix — a resolved name must equal an
    /// authored folder built from the same token. Replicated inline deliberately (consistent with how
    /// EntraTeamNameResolver mirrors GenerateFolderName); unifying the copies is a future cleanup.
    /// </summary>
    private static string Canonicalize(string raw)
    {
        var sanitized = raw.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("_", "-");

        return Regex.Replace(sanitized, @"[^a-z0-9\-]", "");
    }
}
