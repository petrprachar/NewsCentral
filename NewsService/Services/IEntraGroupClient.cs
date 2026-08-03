namespace NewsService.Services;

/// <summary>Status of one group-membership evaluation cycle.</summary>
public enum EntraGroupStatus
{
    /// <summary>Names resolved and membership evaluated; see NameStatus / MemberOfNames.</summary>
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

/// <summary>
/// Transport-level status plus per-name resolution status and membership for one evaluation cycle,
/// covering every distinct group display name referenced by any active instance (inclusion,
/// per-instance exclusion, and the fleet-wide global exclusion). A per-name failure
/// (<see cref="EntraGroupStatus.NameNotFound"/> / <see cref="EntraGroupStatus.NameAmbiguous"/> in
/// <see cref="NameStatus"/>) isolates to the instances referencing that name; a transport-level
/// failure (<see cref="Status"/> not <see cref="EntraGroupStatus.Success"/>) is global.
/// </summary>
/// <param name="Status">
/// Transport-level outcome only: <see cref="EntraGroupStatus.Success"/>,
/// <see cref="EntraGroupStatus.PermissionDenied"/>, or <see cref="EntraGroupStatus.Unreachable"/>.
/// <see cref="EntraGroupStatus.NameNotFound"/> / <see cref="EntraGroupStatus.NameAmbiguous"/> never
/// appear here — only in <see cref="NameStatus"/>.
/// </param>
/// <param name="NameStatus">
/// Per requested display name (keyed as configured, compared <c>OrdinalIgnoreCase</c>): whether it
/// resolved. Empty when <see cref="Status"/> is not <see cref="EntraGroupStatus.Success"/>.
/// </param>
/// <param name="MemberOfNames">
/// The subset of successfully-resolved display names the device is a transitive member of. Empty
/// when <see cref="Status"/> is not <see cref="EntraGroupStatus.Success"/>.
/// </param>
public sealed record EntraGroupSnapshot(
    EntraGroupStatus Status,
    IReadOnlyDictionary<string, EntraGroupStatus> NameStatus,
    IReadOnlySet<string> MemberOfNames);

/// <summary>
/// Seam over <see cref="EntraGroupClient"/> so the group-outcome mapping and the orchestrator can be
/// unit-tested offline with a fake (no Graph call, no credentials).
/// </summary>
public interface IEntraGroupClient
{
    /// <summary>
    /// Resolves each display name to an object id and evaluates the device's transitive membership
    /// across all of them. Names are resolved and evaluated as a set so N instances cost one
    /// checkMemberGroups call per 20 distinct groups, not one call per instance.
    /// </summary>
    Task<EntraGroupSnapshot> EvaluateAsync(
        string deviceObjectId, IReadOnlyCollection<string> groupNames, CancellationToken ct);
}
