using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers GroupOutcomeMapper — evaluation → per-source EntraSourceOutcome. Persistent failures
/// (name not found / ambiguous / 403) → NoTeam; transient → Unreachable; Success → the
/// GroupTeamDecision result.
/// </summary>
public sealed class GroupOutcomeMapperTests
{
    private const string Inclusion = "NewsCentral Prague ITS";
    private const string Exclusion = "Excluded Devices";

    [Fact]
    public void Success_InInclusion_NotExcluded_ResolvesCanonicalTeam()
    {
        var eval = new EntraGroupEvaluation(EntraGroupStatus.Success, InInclusion: true, InExclusion: false);

        var outcome = GroupOutcomeMapper.Map(eval, Inclusion, Exclusion);

        Assert.Equal(ResolvedTeamSource.Group, outcome.Source);
        Assert.Equal(EntraCycleResult.ResolvedTeam, outcome.Result);
        Assert.Equal("newscentral-prague-its", outcome.Team);
    }

    [Fact]
    public void Success_InBoth_NoTeam()
    {
        var eval = new EntraGroupEvaluation(EntraGroupStatus.Success, InInclusion: true, InExclusion: true);

        var outcome = GroupOutcomeMapper.Map(eval, Inclusion, Exclusion);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
        Assert.Null(outcome.Team);
    }

    [Fact]
    public void Success_NotInInclusion_NoTeam()
    {
        var eval = new EntraGroupEvaluation(EntraGroupStatus.Success, InInclusion: false, InExclusion: false);

        var outcome = GroupOutcomeMapper.Map(eval, Inclusion, Exclusion);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void NameNotFound_NoTeam()
    {
        var outcome = GroupOutcomeMapper.Map(
            new EntraGroupEvaluation(EntraGroupStatus.NameNotFound), Inclusion, Exclusion);

        Assert.Equal(ResolvedTeamSource.Group, outcome.Source);
        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void NameAmbiguous_NoTeam()
    {
        var outcome = GroupOutcomeMapper.Map(
            new EntraGroupEvaluation(EntraGroupStatus.NameAmbiguous), Inclusion, Exclusion);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void PermissionDenied_NoTeam()
    {
        var outcome = GroupOutcomeMapper.Map(
            new EntraGroupEvaluation(EntraGroupStatus.PermissionDenied), Inclusion, Exclusion);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void Unreachable_GivesUnreachable()
    {
        var outcome = GroupOutcomeMapper.Map(
            new EntraGroupEvaluation(EntraGroupStatus.Unreachable), Inclusion, Exclusion);

        Assert.Equal(ResolvedTeamSource.Group, outcome.Source);
        Assert.Equal(EntraCycleResult.Unreachable, outcome.Result);
    }
}
