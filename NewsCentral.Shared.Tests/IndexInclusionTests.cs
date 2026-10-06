using NewsCentral.Models;
using NewsCentral.Publishing;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class IndexInclusionTests
{
    private static Assignment MakeAssignment(
        string sourceTeam, string targetTeam, AssignmentStatus status) =>
        new()
        {
            AssignmentID = "a1",
            SourceTeam = sourceTeam,
            TargetTeam = targetTeam,
            Status = status
        };

    [Fact]
    public void Includes_OwnTeamPublished_ReturnsTrue()
    {
        var assignment = MakeAssignment("cz-its", "cz-its", AssignmentStatus.Published);

        Assert.True(IndexInclusion.Includes(assignment, "cz-its"));
    }

    [Fact]
    public void Includes_IncomingCrossTeamCopyPublished_ReturnsTrue()
    {
        var assignment = MakeAssignment("cz-its", "load-001", AssignmentStatus.Published);

        Assert.True(IndexInclusion.Includes(assignment, "load-001"));
    }

    [Fact]
    public void Includes_SourceRecordTargetingAnotherTeam_ReturnsFalse()
    {
        var assignment = MakeAssignment("cz-its", "load-001", AssignmentStatus.Published);

        Assert.False(IndexInclusion.Includes(assignment, "cz-its"));
    }

    [Fact]
    public void Includes_NotPublished_ReturnsFalse()
    {
        var assignment = MakeAssignment("cz-its", "cz-its", AssignmentStatus.Approved);

        Assert.False(IndexInclusion.Includes(assignment, "cz-its"));
    }

    [Fact]
    public void Includes_CaseDifferenceInFolderName_ReturnsTrue()
    {
        var assignment = MakeAssignment("cz-its", "Load-001", AssignmentStatus.Published);

        Assert.True(IndexInclusion.Includes(assignment, "load-001"));
    }
}
