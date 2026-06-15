using NewsCentral.Models;

namespace NewsCentral.Configuration;

/// <summary>
/// Outcome of a single NewsService Entra resolution cycle, fed into the grace merger.
/// </summary>
public enum EntraCycleResult
{
    /// <summary>A dynamic team was authoritatively resolved this cycle.</summary>
    ResolvedTeam,

    /// <summary>An authoritative answer of "no dynamic team" (device read OK, but no team).</summary>
    NoTeam,

    /// <summary>The device/Graph could not be reached — prior entries ride the grace window.</summary>
    Unreachable
}

/// <summary>
/// The per-source outcome of one resolution cycle. At most one outcome per
/// <see cref="ResolvedTeamSource"/> is supplied to <see cref="EntraResolvedTeamsMerger.Merge"/>.
/// </summary>
public readonly record struct EntraSourceOutcome(
    ResolvedTeamSource Source, EntraCycleResult Result, string? Team);

/// <summary>
/// Pure grace state machine merging the previous <c>resolved-teams.json</c> entries with the
/// current cycle's per-source outcomes. No I/O, no logging.
///
/// Each source (Attribute, Group) is resolved independently: an outcome only affects entries of
/// its own <see cref="ResolvedTeamSource"/>, so each source carries its own grace state. Grace
/// applies ONLY to transient unreachability — an authoritative "no team" removes that source's
/// entries immediately (clean answers remove promptly; only failures get the benefit of the doubt).
///
/// A source with no outcome this cycle is left untouched (its existing entries pass through). The
/// same TeamFolderName may appear under two sources (rare); both are kept — the consumer
/// (ResolvedTeamsReader → HashSet) de-dups by name, while collapsing here would lose a source's
/// independent grace state.
/// </summary>
public static class EntraResolvedTeamsMerger
{
    public static List<ResolvedTeamEntry> Merge(
        IReadOnlyList<ResolvedTeamEntry> existing,
        IReadOnlyList<EntraSourceOutcome> outcomes,   // at most one per source this cycle
        DateTime nowUtc, TimeSpan gracePeriod)
    {
        var handled = new HashSet<ResolvedTeamSource>();
        var result  = new List<ResolvedTeamEntry>();

        foreach (var outcome in outcomes)
        {
            handled.Add(outcome.Source);
            result.AddRange(MergeSource(existing, outcome, nowUtc, gracePeriod));
        }

        // Sources without an outcome this cycle are left as-is.
        foreach (var e in existing)
            if (!handled.Contains(e.Source))
                result.Add(e);

        return result;
    }

    private static IEnumerable<ResolvedTeamEntry> MergeSource(
        IReadOnlyList<ResolvedTeamEntry> existing,
        EntraSourceOutcome outcome,
        DateTime nowUtc, TimeSpan gracePeriod)
    {
        switch (outcome.Result)
        {
            case EntraCycleResult.ResolvedTeam:
                // Authoritative team for this source — replaces this source's prior entry.
                return
                [
                    new ResolvedTeamEntry
                    {
                        TeamFolderName   = outcome.Team ?? "",
                        LastConfirmedUtc = nowUtc,
                        State            = ResolvedTeamState.Active,
                        Source           = outcome.Source
                    }
                ];

            case EntraCycleResult.NoTeam:
                // Authoritative clean removal — no grace.
                return [];

            case EntraCycleResult.Unreachable:
                // Carry forward this source's entries still inside the grace window; mark Grace;
                // preserve LastConfirmedUtc.
                return existing
                    .Where(e => e.Source == outcome.Source &&
                                nowUtc - e.LastConfirmedUtc <= gracePeriod)
                    .Select(e => new ResolvedTeamEntry
                    {
                        TeamFolderName   = e.TeamFolderName,
                        LastConfirmedUtc = e.LastConfirmedUtc,
                        State            = ResolvedTeamState.Grace,
                        Source           = e.Source
                    });

            default:
                return [];
        }
    }
}
