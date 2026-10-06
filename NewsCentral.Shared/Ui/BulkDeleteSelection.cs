using NewsCentral.Models;

namespace NewsCentral.Ui;

/// <summary>
/// UI-2.3: pure helpers for bulk delete on Assignments.
/// </summary>
public static class BulkDeleteSelection
{
    /// <summary>
    /// The ids of the items in <paramref name="items"/> that satisfy <paramref name="isEligible"/>
    /// — the subset a "select all"/batch tri-state checkbox should operate over when ineligible
    /// rows must be excluded from bulk selection entirely (rather than merely hidden). Feed the
    /// result into <see cref="SelectionModel{TKey}.StateOf"/> / <c>ToggleAll</c> as the "shown" set.
    /// </summary>
    public static List<string> EligibleIds<T>(
        IEnumerable<T> items, Func<T, bool> isEligible, Func<T, string> idOf) =>
        items.Where(isEligible).Select(idOf).ToList();

    /// <summary>
    /// Count of distinct <see cref="Assignment.TargetTeam"/> values among the given assignments
    /// (ordinal, case-insensitive) — the "M team(s)" count named in the Published bulk-delete
    /// confirmation ("withdrawn from the devices of M team(s)").
    /// </summary>
    public static int DistinctTargetTeamCount(IEnumerable<Assignment> assignments) =>
        assignments.Select(a => a.TargetTeam).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}
