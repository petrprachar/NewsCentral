using NewsCentral.Models;
using NewsCentral.Publishing;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class PresentationDeletePlannerTests
{
    private static Assignment A(string id, string target, string sched, AssignmentStatus status = AssignmentStatus.Published) =>
        new() { AssignmentID = id, TargetTeam = target, ScheduleID = sched, PresentationID = "p1", SourceTeam = "cz-its", Status = status };

    [Fact]
    public void SameTeamOnly_NoMoves()
    {
        var plan = PresentationDeletePlanner.Plan("cz-its", "p1", new[] { A("a1", "cz-its", "s1") });
        Assert.Empty(plan.Moves);
        Assert.Empty(plan.AffectedTargetTeams);
    }

    [Fact]
    public void CaseOnlyDifference_TreatedAsSameTeam()
    {
        var plan = PresentationDeletePlanner.Plan("cz-its", "p1", new[] { A("a1", "CZ-ITS", "s1") });
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void OneCrossTeamAssignment_PresSchedAssign()
    {
        var plan = PresentationDeletePlanner.Plan("cz-its", "p1", new[] { A("a1", "de-prod", "s1") });
        Assert.Equal(new[]
        {
            new PlannedMove("de-prod", "de-prod/content/presentations/pres_p1.json", "de-prod/deleted/pres_p1.json"),
            new PlannedMove("de-prod", "de-prod/content/schedules/sched_s1.json", "de-prod/deleted/sched_s1.json"),
            new PlannedMove("de-prod", "de-prod/content/assignments/assign_a1.json", "de-prod/deleted/assign_a1.json"),
        }, plan.Moves);
        Assert.Equal(new[] { "de-prod" }, plan.AffectedTargetTeams);
    }

    [Fact]
    public void TwoAssignmentsSameTarget_OnePresTwoSchedTwoAssign()
    {
        var plan = PresentationDeletePlanner.Plan("cz-its", "p1",
            new[] { A("a1", "de-prod", "s1"), A("a2", "de-prod", "s2", AssignmentStatus.Draft) });
        Assert.Equal(5, plan.Moves.Count);
        Assert.Single(plan.Moves, m => m.From.Contains("/presentations/"));
        Assert.Equal(2, plan.Moves.Count(m => m.From.Contains("/schedules/")));
        Assert.Equal(2, plan.Moves.Count(m => m.From.Contains("/assignments/")));
    }

    [Fact]
    public void TwoTargets_DeterministicOrder()
    {
        var plan = PresentationDeletePlanner.Plan("cz-its", "p1",
            new[] { A("a2", "z-team", "s2"), A("a1", "b-team", "s1") });
        Assert.Equal(new[] { "b-team", "z-team" }, plan.AffectedTargetTeams);
        Assert.Equal(6, plan.Moves.Count);
        Assert.All(plan.Moves.Take(3), m => Assert.Equal("b-team", m.TargetTeam));
        Assert.Contains("/presentations/", plan.Moves[0].From);
        Assert.Contains("/schedules/", plan.Moves[1].From);
        Assert.Contains("/assignments/", plan.Moves[2].From);
    }

    [Fact]
    public void EmptyScheduleId_NoScheduleMove()
    {
        var plan = PresentationDeletePlanner.Plan("cz-its", "p1", new[] { A("a1", "de-prod", "") });
        Assert.DoesNotContain(plan.Moves, m => m.From.Contains("/schedules/"));
        Assert.Equal(2, plan.Moves.Count);
    }
}
