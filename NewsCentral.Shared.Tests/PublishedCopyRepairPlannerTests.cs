using NewsCentral.Models;
using NewsCentral.Publishing;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class PublishedCopyRepairPlannerTests
{
    private static Assignment MakeAssignment(
        AssignmentStatus status = AssignmentStatus.Published,
        string? publishedBy = "alice",
        DateTime? publishedDate = null,
        List<string>? publishedPaths = null)
    {
        return new Assignment
        {
            AssignmentID = "a1",
            SourceTeam = "cz-its",
            TargetTeam = "load-001",
            Status = status,
            PublishedBy = publishedBy,
            PublishedDate = publishedDate ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PublishedPaths = publishedPaths ?? new List<string> { "load-001/content/presentations/pres_1.json" }
        };
    }

    [Fact]
    public void Plan_SourceMissing_ReturnsRemove()
    {
        var copy = MakeAssignment();

        var plan = PublishedCopyRepairPlanner.Plan(copy, source: null);

        Assert.Equal(RepairAction.Remove, plan.Action);
        Assert.False(string.IsNullOrEmpty(plan.Reason));
    }

    [Fact]
    public void Plan_SourceNotPublished_ReturnsLeave()
    {
        var copy = MakeAssignment(status: AssignmentStatus.Approved);
        var source = MakeAssignment(status: AssignmentStatus.Approved);

        var plan = PublishedCopyRepairPlanner.Plan(copy, source);

        Assert.Equal(RepairAction.Leave, plan.Action);
        Assert.Contains("Approved", plan.Reason);
    }

    [Fact]
    public void Plan_SourcePublished_CopyStale_ReturnsSync()
    {
        // The exact bug scenario: copy stuck at Approved, source already Published.
        var copy = MakeAssignment(status: AssignmentStatus.Approved, publishedBy: null, publishedDate: null, publishedPaths: new());
        var source = MakeAssignment(status: AssignmentStatus.Published);

        var plan = PublishedCopyRepairPlanner.Plan(copy, source);

        Assert.Equal(RepairAction.Sync, plan.Action);
    }

    [Fact]
    public void Plan_SourcePublished_CopyAlreadyMatches_ReturnsLeave_NoRewrite()
    {
        var publishedDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        var paths = new List<string> { "load-001/content/presentations/pres_1.json", "load-001/content/assignments/assign_a1.json" };

        var source = MakeAssignment(publishedDate: publishedDate, publishedPaths: paths);
        var copy = MakeAssignment(publishedDate: publishedDate, publishedPaths: new List<string>(paths));

        var plan = PublishedCopyRepairPlanner.Plan(copy, source);

        Assert.Equal(RepairAction.Leave, plan.Action);
        Assert.Contains("already matches", plan.Reason);
    }

    [Fact]
    public void Plan_SourcePublished_CopyMatchesExceptPathOrder_ReturnsLeave_NoRewrite()
    {
        var publishedDate = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        var source = MakeAssignment(publishedDate: publishedDate, publishedPaths: new List<string> { "a", "b" });
        var copy = MakeAssignment(publishedDate: publishedDate, publishedPaths: new List<string> { "b", "a" });

        var plan = PublishedCopyRepairPlanner.Plan(copy, source);

        Assert.Equal(RepairAction.Leave, plan.Action);
    }

    [Fact]
    public void Plan_SourcePublished_DifferentPublishedBy_ReturnsSync()
    {
        var source = MakeAssignment(publishedBy: "alice");
        var copy = MakeAssignment(publishedBy: "bob");

        var plan = PublishedCopyRepairPlanner.Plan(copy, source);

        Assert.Equal(RepairAction.Sync, plan.Action);
    }

    [Fact]
    public void Plan_SourcePublished_DifferentPublishedPaths_ReturnsSync()
    {
        var source = MakeAssignment(publishedPaths: new List<string> { "a" });
        var copy = MakeAssignment(publishedPaths: new List<string> { "a", "b" });

        var plan = PublishedCopyRepairPlanner.Plan(copy, source);

        Assert.Equal(RepairAction.Sync, plan.Action);
    }

    [Fact]
    public void Plan_NullCopy_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PublishedCopyRepairPlanner.Plan(null!, MakeAssignment()));
    }
}
