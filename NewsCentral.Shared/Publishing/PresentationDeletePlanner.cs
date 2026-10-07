using NewsCentral.Models;

namespace NewsCentral.Publishing;

/// <summary>One authoring-tier soft-delete move in a target team's folder.</summary>
public sealed record PlannedMove(string TargetTeam, string From, string To);

/// <summary>The moves to execute plus the distinct target teams they touch (sorted, ordinal-ignore-case).</summary>
public sealed record PresentationDeletePlan(
    IReadOnlyList<PlannedMove> Moves,
    IReadOnlyList<string> AffectedTargetTeams);

/// <summary>
/// PUB-1: pure planning for deleting a presentation from its SOURCE team — which of the TARGET teams'
/// own authoring-tier copies (presentation, schedules, assignments written by a cross-team publish)
/// must be soft-deleted too. No I/O: <c>PresentationService.DeletePresentationAsync</c> executes the
/// moves, treating a missing file as a no-op, so assignments in any status are included.
/// </summary>
public static class PresentationDeletePlanner
{
    public static PresentationDeletePlan Plan(
        string sourceTeamFolder,
        string presentationId,
        IEnumerable<Assignment> relatedAssignments)
    {
        ArgumentNullException.ThrowIfNull(relatedAssignments);

        var crossTeam = relatedAssignments
            .Where(a => !string.IsNullOrEmpty(a.TargetTeam) &&
                        !string.Equals(a.TargetTeam, sourceTeamFolder, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var moves = new List<PlannedMove>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var teams = new List<string>();

        void Add(string team, string from, string to)
        {
            if (seen.Add(from))
                moves.Add(new PlannedMove(team, from, to));
        }

        foreach (var group in crossTeam
                     .GroupBy(a => a.TargetTeam, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var team = group.Key;
            teams.Add(team);

            Add(team,
                $"{team}/content/presentations/pres_{presentationId}.json",
                $"{team}/deleted/pres_{presentationId}.json");

            foreach (var scheduleId in group
                         .Select(a => a.ScheduleID)
                         .Where(id => !string.IsNullOrEmpty(id))
                         .Distinct(StringComparer.Ordinal))
            {
                Add(team,
                    $"{team}/content/schedules/sched_{scheduleId}.json",
                    $"{team}/deleted/sched_{scheduleId}.json");
            }

            foreach (var assignmentId in group.Select(a => a.AssignmentID).Distinct(StringComparer.Ordinal))
            {
                Add(team,
                    $"{team}/content/assignments/assign_{assignmentId}.json",
                    $"{team}/deleted/assign_{assignmentId}.json");
            }
        }

        return new PresentationDeletePlan(moves, teams);
    }
}
