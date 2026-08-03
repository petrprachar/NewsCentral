using NewsCentral.Configuration;
using NewsCentral.Models;

namespace NewsService.Services;

/// <summary>
/// Pure mapping from a group evaluation to the per-source <see cref="EntraSourceOutcome"/> consumed
/// by the G1 set merger. No I/O. Persistent failures (name not found / ambiguous / 403) map to
/// <c>NoTeam</c> (clean removal); transient unreachability maps to <c>Unreachable</c> (grace).
/// </summary>
public static class GroupOutcomeMapper
{
    public static EntraSourceOutcome Map(
        EntraGroupEvaluation eval, EntraSourceKey key, string? inclusion, string? exclusion)
    {
        switch (eval.Status)
        {
            case EntraGroupStatus.Success:
                var team = GroupTeamDecision.Resolve(
                    inclusion, exclusion, eval.InInclusion, eval.InExclusion);
                return team is not null
                    ? new EntraSourceOutcome(key, EntraCycleResult.ResolvedTeam, team)
                    : new EntraSourceOutcome(key, EntraCycleResult.NoTeam, null);

            case EntraGroupStatus.Unreachable:
                return new EntraSourceOutcome(key, EntraCycleResult.Unreachable, null);

            // NameNotFound / NameAmbiguous / PermissionDenied — all persistent → remove promptly.
            default:
                return new EntraSourceOutcome(key, EntraCycleResult.NoTeam, null);
        }
    }
}
