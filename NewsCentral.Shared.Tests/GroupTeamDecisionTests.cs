using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// Covers GroupTeamDecision.Resolve — inclusion ∧ ¬exclusion → canonical inclusion-group name.
/// Canonicalization mirrors EntraTeamNameResolver / TeamService.GenerateFolderName exactly.
/// </summary>
public sealed class GroupTeamDecisionTests
{
    [Fact]
    public void InclusionEmpty_ReturnsNull()
    {
        Assert.Null(GroupTeamDecision.Resolve(
            inclusionGroup: "", exclusionGroup: "",
            deviceInInclusion: true, deviceInExclusion: false));
    }

    [Fact]
    public void InclusionWhitespace_ReturnsNull()
    {
        Assert.Null(GroupTeamDecision.Resolve(
            "   ", "", deviceInInclusion: true, deviceInExclusion: false));
    }

    [Fact]
    public void NotInInclusion_ReturnsNull()
    {
        Assert.Null(GroupTeamDecision.Resolve(
            "NewsCentral Prague ITS", "", deviceInInclusion: false, deviceInExclusion: false));
    }

    [Fact]
    public void InInclusion_NotExcluded_ReturnsCanonicalName()
    {
        var team = GroupTeamDecision.Resolve(
            "NewsCentral Prague ITS", "Excluded Devices",
            deviceInInclusion: true, deviceInExclusion: false);

        Assert.Equal("newscentral-prague-its", team);
    }

    [Fact]
    public void InInclusion_AndExcluded_ReturnsNull()
    {
        Assert.Null(GroupTeamDecision.Resolve(
            "NewsCentral Prague ITS", "Excluded Devices",
            deviceInInclusion: true, deviceInExclusion: true));
    }

    [Fact]
    public void ExclusionEmpty_InInclusion_ReturnsName_EvenIfDeviceInExclusionFlagSet()
    {
        // No exclusion group configured → the exclusion membership flag is irrelevant.
        var team = GroupTeamDecision.Resolve(
            "NewsCentral Prague ITS", "",
            deviceInInclusion: true, deviceInExclusion: true);

        Assert.Equal("newscentral-prague-its", team);
    }

    [Fact]
    public void Canonicalization_MirrorsGenerateFolderName()
    {
        // Spaces and underscores → '-'; lower-cased; out-of-range chars stripped; no collapse/trim.
        var team = GroupTeamDecision.Resolve(
            "NewsCentral Prague ITS", null,
            deviceInInclusion: true, deviceInExclusion: false);

        Assert.Equal("newscentral-prague-its", team);
    }
}
