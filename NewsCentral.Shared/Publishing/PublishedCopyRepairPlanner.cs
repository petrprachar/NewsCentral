using NewsCentral.Models;

namespace NewsCentral.Publishing;

/// <summary>PUB-1: what to do about one target team's copy of a cross-team assignment.</summary>
public enum RepairAction
{
    /// <summary>The source is Published — rewrite the copy from the source record.</summary>
    Sync,

    /// <summary>The source record is missing (deleted) — move the copy to the target's deleted folder.</summary>
    Remove,

    /// <summary>Report only — the source exists but isn't Published.</summary>
    Leave,

    /// <summary>
    /// UI-2.3: the source is Published and the copy already matches it — nothing to write. Kept
    /// distinct from <see cref="Leave"/> so a clean re-run (the normal case) doesn't read as a
    /// pile of "left untouched" warnings; it is simply the expected steady state.
    /// </summary>
    InSync
}

/// <summary>A planned action plus a human-readable reason (always set, for logging/reporting).</summary>
public sealed record RepairPlan(RepairAction Action, string Reason);

/// <summary>
/// PUB-1: pure decision logic for repairing a target team's copy of a cross-team assignment —
/// one half of the fix for the bug where a cross-team publish wrote the copy BEFORE the source
/// record's status flipped to Published, leaving the copy stuck at Approved forever (so it never
/// appeared in the target team's index). No I/O here; <c>PublishingService.RepairPublishedCopiesAsync</c>
/// is the imperative shell that reads/writes storage and calls this for each candidate copy.
/// </summary>
public static class PublishedCopyRepairPlanner
{
    /// <param name="copy">The target team's own on-disk copy (SourceTeam != that team's folder).</param>
    /// <param name="source">
    /// The authoritative record at <c>copy.SourceTeam</c>, or null if it no longer exists there
    /// (soft-deleted).
    /// </param>
    public static RepairPlan Plan(Assignment copy, Assignment? source)
    {
        if (copy is null)
            throw new ArgumentNullException(nameof(copy));

        if (source is null)
            return new RepairPlan(RepairAction.Remove, "Source record is missing (deleted)");

        if (source.Status != AssignmentStatus.Published)
            return new RepairPlan(RepairAction.Leave, $"Source is not Published (status: {source.Status})");

        if (IsCopyUpToDate(copy, source))
            return new RepairPlan(RepairAction.InSync, "Copy already matches the source's Published state");

        return new RepairPlan(RepairAction.Sync, "Source is Published and the copy is stale");
    }

    /// <summary>
    /// Compares exactly the fields PublishingService builds as "the final Published state"
    /// (Status, PublishedBy, PublishedDate, PublishedPaths) — a match means the copy needs no
    /// rewrite even though the source is Published.
    /// </summary>
    private static bool IsCopyUpToDate(Assignment copy, Assignment source) =>
        copy.Status == source.Status &&
        copy.PublishedBy == source.PublishedBy &&
        copy.PublishedDate == source.PublishedDate &&
        PublishedPathsEqual(copy.PublishedPaths, source.PublishedPaths);

    private static bool PublishedPathsEqual(List<string>? a, List<string>? b)
    {
        a ??= new List<string>();
        b ??= new List<string>();

        if (a.Count != b.Count)
            return false;

        return a.OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);
    }
}
