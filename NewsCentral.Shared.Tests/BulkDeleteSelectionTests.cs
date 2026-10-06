using NewsCentral.Models;
using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class BulkDeleteSelectionTests
{
    [Fact]
    public void EligibleIds_MixedEligibility_ReturnsOnlyEligibleIds()
    {
        var items = new[] { "a", "b", "c", "d" };

        var result = BulkDeleteSelection.EligibleIds(items, x => x != "b" && x != "d", x => x);

        Assert.Equal(new[] { "a", "c" }, result);
    }

    [Fact]
    public void EligibleIds_NoneEligible_ReturnsEmpty()
    {
        var items = new[] { "a", "b" };

        var result = BulkDeleteSelection.EligibleIds(items, _ => false, x => x);

        Assert.Empty(result);
    }

    [Fact]
    public void EligibleIds_AllEligible_ReturnsAllIds()
    {
        var items = new[] { "a", "b" };

        var result = BulkDeleteSelection.EligibleIds(items, _ => true, x => x);

        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void EligibleIds_EmptySource_ReturnsEmpty()
    {
        var result = BulkDeleteSelection.EligibleIds(Array.Empty<string>(), _ => true, x => x);

        Assert.Empty(result);
    }

    private static Assignment MakeAssignment(string targetTeam) => new() { TargetTeam = targetTeam };

    [Fact]
    public void DistinctTargetTeamCount_NoItems_ReturnsZero()
    {
        Assert.Equal(0, BulkDeleteSelection.DistinctTargetTeamCount(Array.Empty<Assignment>()));
    }

    [Fact]
    public void DistinctTargetTeamCount_SameTeamRepeated_ReturnsOne()
    {
        var assignments = new[] { MakeAssignment("load-001"), MakeAssignment("load-001"), MakeAssignment("load-001") };

        Assert.Equal(1, BulkDeleteSelection.DistinctTargetTeamCount(assignments));
    }

    [Fact]
    public void DistinctTargetTeamCount_MultipleDistinctTeams_ReturnsCount()
    {
        var assignments = new[] { MakeAssignment("load-001"), MakeAssignment("load-002"), MakeAssignment("load-001") };

        Assert.Equal(2, BulkDeleteSelection.DistinctTargetTeamCount(assignments));
    }

    [Fact]
    public void DistinctTargetTeamCount_CaseInsensitive_TreatsAsSameTeam()
    {
        var assignments = new[] { MakeAssignment("Load-001"), MakeAssignment("load-001") };

        Assert.Equal(1, BulkDeleteSelection.DistinctTargetTeamCount(assignments));
    }
}
