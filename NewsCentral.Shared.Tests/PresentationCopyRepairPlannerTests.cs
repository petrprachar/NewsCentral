using NewsCentral.Models;
using NewsCentral.Publishing;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class PresentationCopyRepairPlannerTests
{
    private static Presentation P(string teamFolder) => new() { PresentationID = "p1", TeamFolderName = teamFolder };

    [Fact]
    public void SourcePresent_NoRemove() =>
        Assert.Equal(RepairAction.InSync, PresentationCopyRepairPlanner.Plan(P("cz-its"), "de-prod", true).Action);

    [Fact]
    public void SourceMissing_Remove() =>
        Assert.Equal(RepairAction.Remove, PresentationCopyRepairPlanner.Plan(P("cz-its"), "de-prod", false).Action);

    [Theory]
    [InlineData("de-prod")]
    [InlineData("DE-PROD")]
    public void OwnTeam_NeverRemoved(string folder) =>
        Assert.NotEqual(RepairAction.Remove, PresentationCopyRepairPlanner.Plan(P(folder), "de-prod", false).Action);

    [Fact]
    public void EmptyTeamFolder_LeaveWithReason()
    {
        var plan = PresentationCopyRepairPlanner.Plan(P(""), "de-prod", false);
        Assert.Equal(RepairAction.Leave, plan.Action);
        Assert.Equal("no source team recorded", plan.Reason);
    }
}
