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
/// Identifies which configured instance of an Entra <see cref="ResolvedTeamSource"/> produced (or
/// owns) a resolved-team entry, so several configured instances of the same source — several
/// attribute mapping schemes, several inclusion/exclusion group pairs — can each carry an
/// independent grace window.
/// </summary>
public readonly record struct EntraSourceKey(ResolvedTeamSource Source, string Id) : IEquatable<EntraSourceKey>
{
    /// <summary>The instance id of the legacy single-instance sources.</summary>
    public const string LegacyId = "";

    public static EntraSourceKey Legacy(ResolvedTeamSource source) => new(source, LegacyId);

    // Instance ids originate from configuration section names, which are case-insensitive in both
    // the registry walk and the .NET configuration binder — so Id comparison is ordinal-ignore-case.
    // The authored casing itself is NOT normalized (preserved in resolved-teams.json for
    // diagnostics); only comparison treats it case-insensitively.
    public bool Equals(EntraSourceKey other) =>
        Source == other.Source &&
        StringComparer.OrdinalIgnoreCase.Equals(Id ?? "", other.Id ?? "");

    public override int GetHashCode() =>
        HashCode.Combine(Source, StringComparer.OrdinalIgnoreCase.GetHashCode(Id ?? ""));
}

/// <summary>
/// The per-instance outcome of one resolution cycle. At most one outcome per
/// <see cref="EntraSourceKey"/> is supplied to <see cref="EntraResolvedTeamsMerger.Merge"/>.
/// </summary>
public readonly record struct EntraSourceOutcome(
    EntraSourceKey Key, EntraCycleResult Result, string? Team);

/// <summary>
/// Pure grace state machine merging the previous <c>resolved-teams.json</c> entries with the
/// current cycle's per-instance outcomes. No I/O, no logging.
///
/// Each configured instance — keyed by (Source, SourceId) — is resolved independently: an outcome
/// only affects entries of its own <see cref="EntraSourceKey"/>, so each instance carries its own
/// grace state. Grace applies ONLY to transient unreachability — an authoritative "no team" removes
/// that instance's entries immediately (clean answers remove promptly; only failures get the
/// benefit of the doubt).
///
/// An instance with no outcome this cycle is left untouched (its existing entries pass through),
/// PROVIDED its key is still in <c>activeKeys</c> — an instance no longer configured is dropped
/// immediately, with no grace: the configured instance set is read locally and is therefore
/// authoritative even when Graph is unreachable, so removing an instance from configuration is a
/// clean removal, not an outage.
///
/// The same TeamFolderName may legitimately appear under several keys (rare); the consumer
/// (ResolvedTeamsReader → HashSet) de-dups by name, while collapsing here would lose an instance's
/// independent grace state.
/// </summary>
public static class EntraResolvedTeamsMerger
{
    public static List<ResolvedTeamEntry> Merge(
        IReadOnlyList<ResolvedTeamEntry> existing,
        IReadOnlyList<EntraSourceOutcome> outcomes,   // at most one per key this cycle
        IReadOnlySet<EntraSourceKey> activeKeys,
        DateTime nowUtc, TimeSpan gracePeriod)
    {
        // Instances no longer configured are dropped immediately, with no grace — the configured
        // instance set is authoritative regardless of Graph reachability.
        var activeExisting = existing.Where(e => activeKeys.Contains(new EntraSourceKey(e.Source, e.SourceId))).ToList();

        var handled = new HashSet<EntraSourceKey>();
        var result  = new List<ResolvedTeamEntry>();
        var seenKeys = new HashSet<EntraSourceKey>();

        foreach (var outcome in outcomes)
        {
            if (!activeKeys.Contains(outcome.Key))
                continue;   // defensive — caller bug, do not throw

            if (!seenKeys.Add(outcome.Key))
                continue;   // duplicate outcome for the same key this cycle — first wins

            handled.Add(outcome.Key);
            result.AddRange(MergeKey(activeExisting, outcome, nowUtc, gracePeriod));
        }

        // Keys without an outcome this cycle are left as-is (still subject to the activeKeys filter above).
        foreach (var e in activeExisting)
            if (!handled.Contains(new EntraSourceKey(e.Source, e.SourceId)))
                result.Add(e);

        return result;
    }

    private static IEnumerable<ResolvedTeamEntry> MergeKey(
        IReadOnlyList<ResolvedTeamEntry> existing,
        EntraSourceOutcome outcome,
        DateTime nowUtc, TimeSpan gracePeriod)
    {
        switch (outcome.Result)
        {
            case EntraCycleResult.ResolvedTeam:
                // Authoritative team for this key — replaces this key's prior entry.
                return
                [
                    new ResolvedTeamEntry
                    {
                        TeamFolderName   = outcome.Team ?? "",
                        LastConfirmedUtc = nowUtc,
                        State            = ResolvedTeamState.Active,
                        Source           = outcome.Key.Source,
                        SourceId         = outcome.Key.Id
                    }
                ];

            case EntraCycleResult.NoTeam:
                // Authoritative clean removal — no grace.
                return [];

            case EntraCycleResult.Unreachable:
                // Carry forward this key's entries still inside the grace window; mark Grace;
                // preserve LastConfirmedUtc.
                return existing
                    .Where(e => new EntraSourceKey(e.Source, e.SourceId).Equals(outcome.Key) &&
                                nowUtc - e.LastConfirmedUtc <= gracePeriod)
                    .Select(e => new ResolvedTeamEntry
                    {
                        TeamFolderName   = e.TeamFolderName,
                        LastConfirmedUtc = e.LastConfirmedUtc,
                        State            = ResolvedTeamState.Grace,
                        Source           = e.Source,
                        SourceId         = e.SourceId
                    });

            default:
                return [];
        }
    }
}
