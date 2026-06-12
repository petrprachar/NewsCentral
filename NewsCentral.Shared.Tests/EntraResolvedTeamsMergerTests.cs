using NewsCentral.Configuration;
using NewsCentral.Models;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EntraResolvedTeamsMergerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(240);

    private static ResolvedTeamEntry Entry(string name, DateTime confirmed,
        ResolvedTeamState state = ResolvedTeamState.Active) =>
        new() { TeamFolderName = name, LastConfirmedUtc = confirmed, State = state };

    [Fact]
    public void ResolvedTeam_ProducesSingleActiveEntry()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [], EntraCycleResult.ResolvedTeam, "cz-prague-its", Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-prague-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
        Assert.Equal(Now, only.LastConfirmedUtc);
    }

    [Fact]
    public void ResolvedTeam_ReplacesDifferentPriorTeam()
    {
        var existing = new[] { Entry("de-prod", Now.AddHours(-1)) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, EntraCycleResult.ResolvedTeam, "cz-its", Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }

    [Fact]
    public void NoTeam_ProducesEmptyList()
    {
        var existing = new[] { Entry("cz-its", Now.AddMinutes(-5)) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, EntraCycleResult.NoTeam, null, Now, Grace);

        Assert.Empty(merged);
    }

    [Fact]
    public void Unreachable_WithinGrace_RetainedAsGrace_TimestampUnchanged()
    {
        var confirmed = Now.AddMinutes(-100);   // within 240-minute grace
        var existing = new[] { Entry("cz-its", confirmed) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, EntraCycleResult.Unreachable, null, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.Equal(confirmed, only.LastConfirmedUtc);   // NOT advanced
    }

    [Fact]
    public void Unreachable_BeyondGrace_Dropped()
    {
        var existing = new[] { Entry("cz-its", Now.AddMinutes(-300)) };   // past 240

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, EntraCycleResult.Unreachable, null, Now, Grace);

        Assert.Empty(merged);
    }

    [Fact]
    public void Unreachable_Mixed_OnlyWithinGraceRetained()
    {
        var existing = new[]
        {
            Entry("cz-its",  Now.AddMinutes(-100)),   // in grace
            Entry("de-prod", Now.AddMinutes(-500)),   // expired
        };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, EntraCycleResult.Unreachable, null, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
    }

    [Fact]
    public void Unreachable_EmptyExisting_ProducesEmpty()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [], EntraCycleResult.Unreachable, null, Now, Grace);

        Assert.Empty(merged);
    }
}
