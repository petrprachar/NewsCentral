namespace NewsService.Services;

/// <summary>Status of one group-membership evaluation cycle.</summary>
public enum EntraGroupStatus
{
    /// <summary>Names resolved and membership evaluated; see InInclusion / InExclusion.</summary>
    Success,

    /// <summary>A configured group display name resolved to zero groups — persistent → remove.</summary>
    NameNotFound,

    /// <summary>A configured group display name resolved to more than one group — persistent → remove.</summary>
    NameAmbiguous,

    /// <summary>403 from Graph — persistent (missing consent) → remove + Error log, never grace.</summary>
    PermissionDenied,

    /// <summary>Throttling / network / timeout / unexpected — transient → grace.</summary>
    Unreachable
}

/// <summary>Result of evaluating the device's membership in the inclusion / exclusion groups.</summary>
public sealed record EntraGroupEvaluation(
    EntraGroupStatus Status, bool InInclusion = false, bool InExclusion = false);

/// <summary>
/// Seam over <see cref="EntraGroupClient"/> so the group-outcome mapping and the orchestrator can be
/// unit-tested offline with a fake (no Graph call, no credentials).
/// </summary>
public interface IEntraGroupClient
{
    /// <summary>
    /// Resolves the inclusion (and exclusion, if non-empty) group display names to object ids and
    /// evaluates the device's transitive membership via one <c>checkMemberGroups</c> call.
    /// </summary>
    Task<EntraGroupEvaluation> EvaluateAsync(
        string deviceObjectId, string? inclusionName, string? exclusionName, CancellationToken ct);
}
