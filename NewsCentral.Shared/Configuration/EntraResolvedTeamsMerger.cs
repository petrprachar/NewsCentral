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
/// Pure grace state machine merging the previous <c>resolved-teams.json</c> entries with the
/// current cycle's outcome. No I/O, no logging.
///
/// Grace applies ONLY to transient unreachability — an authoritative "no team" removes entries
/// immediately (clean answers remove promptly; only failures get the benefit of the doubt).
/// </summary>
public static class EntraResolvedTeamsMerger
{
    public static List<ResolvedTeamEntry> Merge(
        IReadOnlyList<ResolvedTeamEntry> existing,
        EntraCycleResult result, string? resolvedTeam,
        DateTime nowUtc, TimeSpan gracePeriod)
    {
        switch (result)
        {
            case EntraCycleResult.ResolvedTeam:
                // Single authoritative team — replaces whatever was there before.
                return
                [
                    new ResolvedTeamEntry
                    {
                        TeamFolderName   = resolvedTeam ?? "",
                        LastConfirmedUtc = nowUtc,
                        State            = ResolvedTeamState.Active
                    }
                ];

            case EntraCycleResult.NoTeam:
                // Authoritative clean removal — no grace.
                return [];

            case EntraCycleResult.Unreachable:
                // Keep entries still inside the grace window; mark Grace; preserve LastConfirmedUtc.
                return existing
                    .Where(e => nowUtc - e.LastConfirmedUtc <= gracePeriod)
                    .Select(e => new ResolvedTeamEntry
                    {
                        TeamFolderName   = e.TeamFolderName,
                        LastConfirmedUtc = e.LastConfirmedUtc,
                        State            = ResolvedTeamState.Grace
                    })
                    .ToList();

            default:
                return [];
        }
    }
}
