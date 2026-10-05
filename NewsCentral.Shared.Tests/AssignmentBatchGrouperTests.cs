using NewsCentral.Models;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class AssignmentBatchGrouperTests
{
    private static Assignment MakeAssignment(
        string sourceTeam, string presentationId, string scheduleId, string targetTeam, DateTime created) => new()
    {
        AssignmentID = Guid.NewGuid().ToString(),
        SourceTeam = sourceTeam,
        PresentationID = presentationId,
        ScheduleID = scheduleId,
        TargetTeam = targetTeam,
        DateCreated = created
    };

    [Fact]
    public void Group_SameKey_ProducesOneBatch()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var assignments = new[]
        {
            MakeAssignment("cz-its", "pres1", "sched1", "team-a", created),
            MakeAssignment("cz-its", "pres1", "sched1", "team-b", created),
        };

        var batches = AssignmentBatchGrouper.Group(assignments);

        Assert.Single(batches);
        Assert.Equal(2, batches[0].Count);
    }

    [Fact]
    public void Group_DifferentScheduleId_ProducesSeparateBatches()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var assignments = new[]
        {
            MakeAssignment("cz-its", "pres1", "sched1", "team-a", created),
            MakeAssignment("cz-its", "pres1", "sched2", "team-a", created),
        };

        var batches = AssignmentBatchGrouper.Group(assignments);

        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Group_DifferentSourceTeamOrPresentation_ProducesSeparateBatches()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var assignments = new[]
        {
            MakeAssignment("cz-its", "pres1", "sched1", "team-a", created),
            MakeAssignment("de-prod", "pres1", "sched1", "team-a", created),
            MakeAssignment("cz-its", "pres2", "sched1", "team-a", created),
        };

        var batches = AssignmentBatchGrouper.Group(assignments);

        Assert.Equal(3, batches.Count);
    }

    [Fact]
    public void Group_EmptyScheduleId_StillGroupsByOtherTwoFields()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var assignments = new[]
        {
            MakeAssignment("cz-its", "pres1", "", "team-a", created),
            MakeAssignment("cz-its", "pres1", "", "team-b", created),
        };

        var batches = AssignmentBatchGrouper.Group(assignments);

        Assert.Single(batches);
        Assert.Equal(2, batches[0].Count);
        Assert.Equal("", batches[0].Key.ScheduleID);
    }

    [Fact]
    public void Group_AssignmentsWithinABatch_AreOrderedByTargetTeam()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var assignments = new[]
        {
            MakeAssignment("cz-its", "pres1", "sched1", "zebra", created),
            MakeAssignment("cz-its", "pres1", "sched1", "alpha", created),
            MakeAssignment("cz-its", "pres1", "sched1", "mango", created),
        };

        var batches = AssignmentBatchGrouper.Group(assignments);

        Assert.Equal(new[] { "alpha", "mango", "zebra" }, batches[0].Assignments.Select(a => a.TargetTeam));
    }

    [Fact]
    public void Group_Batches_AreOrderedByCreatedUtcDescending()
    {
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var assignments = new[]
        {
            MakeAssignment("cz-its", "pres1", "sched1", "team-a", older),
            MakeAssignment("cz-its", "pres2", "sched2", "team-a", newer),
        };

        var batches = AssignmentBatchGrouper.Group(assignments);

        Assert.Equal(newer, batches[0].CreatedUtc);
        Assert.Equal(older, batches[1].CreatedUtc);
    }

    [Fact]
    public void Group_CreatedUtc_IsTheEarliestDateCreatedInTheBatch()
    {
        var earliest = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var later = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc);

        var assignments = new[]
        {
            MakeAssignment("cz-its", "pres1", "sched1", "team-a", later),
            MakeAssignment("cz-its", "pres1", "sched1", "team-b", earliest),
        };

        var batches = AssignmentBatchGrouper.Group(assignments);

        Assert.Equal(earliest, batches[0].CreatedUtc);
    }

    [Fact]
    public void Group_EmptyInput_ReturnsNoBatches()
    {
        var batches = AssignmentBatchGrouper.Group(Array.Empty<Assignment>());

        Assert.Empty(batches);
    }
}
