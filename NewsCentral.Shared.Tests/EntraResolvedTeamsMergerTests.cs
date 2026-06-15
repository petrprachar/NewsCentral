using NewsCentral.Configuration;
using NewsCentral.Models;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EntraResolvedTeamsMergerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(240);

    private static ResolvedTeamEntry Entry(string name, DateTime confirmed,
        ResolvedTeamState state = ResolvedTeamState.Active,
        ResolvedTeamSource source = ResolvedTeamSource.Attribute) =>
        new() { TeamFolderName = name, LastConfirmedUtc = confirmed, State = state, Source = source };

    private static EntraSourceOutcome Attr(EntraCycleResult r, string? team = null) =>
        new(ResolvedTeamSource.Attribute, r, team);

    private static EntraSourceOutcome Group(EntraCycleResult r, string? team = null) =>
        new(ResolvedTeamSource.Group, r, team);

    // ── Single-source (Attribute) — behavior identical to the old single-team cases ──

    [Fact]
    public void ResolvedTeam_ProducesSingleActiveEntry()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [], [Attr(EntraCycleResult.ResolvedTeam, "cz-prague-its")], Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-prague-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
        Assert.Equal(Now, only.LastConfirmedUtc);
    }

    [Fact]
    public void ResolvedTeam_ReplacesDifferentPriorTeam()
    {
        var existing = new[] { Entry("de-prod", Now.AddHours(-1)) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.ResolvedTeam, "cz-its")], Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }

    [Fact]
    public void NoTeam_ProducesEmptyList()
    {
        var existing = new[] { Entry("cz-its", Now.AddMinutes(-5)) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.NoTeam)], Now, Grace);

        Assert.Empty(merged);
    }

    [Fact]
    public void Unreachable_WithinGrace_RetainedAsGrace_TimestampUnchanged()
    {
        var confirmed = Now.AddMinutes(-100);   // within 240-minute grace
        var existing = new[] { Entry("cz-its", confirmed) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.Unreachable)], Now, Grace);

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
            existing, [Attr(EntraCycleResult.Unreachable)], Now, Grace);

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
            existing, [Attr(EntraCycleResult.Unreachable)], Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
    }

    [Fact]
    public void Unreachable_EmptyExisting_ProducesEmpty()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [], [Attr(EntraCycleResult.Unreachable)], Now, Grace);

        Assert.Empty(merged);
    }

    [Fact]
    public void Unreachable_OnlyAffectsOwnSource_OtherSourcePassesThroughUntouched()
    {
        // Attribute Unreachable must NOT touch a Group entry: with no Group outcome this cycle, the
        // Group entry passes through UNCHANGED (still Active) — the attribute branch never re-grades it.
        var confirmed = Now.AddMinutes(-10);
        var existing = new[] { Entry("grp-team", confirmed, source: ResolvedTeamSource.Group) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.Unreachable)], Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("grp-team", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Group, only.Source);
        Assert.Equal(ResolvedTeamState.Active, only.State);   // untouched, not graced
        Assert.Equal(confirmed, only.LastConfirmedUtc);
    }

    [Fact]
    public void MissingSource_DefaultsToAttribute_AndGreysUnderAttribute()
    {
        // Simulates a pre-feature entry deserialized without a Source field (defaults to Attribute).
        var existing = new[] { Entry("cz-its", Now.AddMinutes(-100)) };   // Source = Attribute (default)
        Assert.Equal(ResolvedTeamSource.Attribute, existing[0].Source);

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.Unreachable)], Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
    }

    // ── Multi-source facts ──────────────────────────────────────────────────────

    [Fact]
    public void AttributeResolved_AndGroupResolved_TwoActiveEntriesWithCorrectSource()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [],
            [Attr(EntraCycleResult.ResolvedTeam, "cz-its"),
             Group(EntraCycleResult.ResolvedTeam, "grp-team")],
            Now, Grace);

        Assert.Equal(2, merged.Count);

        var attr = Assert.Single(merged, e => e.Source == ResolvedTeamSource.Attribute);
        Assert.Equal("cz-its", attr.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, attr.State);

        var grp = Assert.Single(merged, e => e.Source == ResolvedTeamSource.Group);
        Assert.Equal("grp-team", grp.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, grp.State);
    }

    [Fact]
    public void AttributeResolved_AndGroupUnreachableWithinGrace_AttrActive_GroupGrace()
    {
        var confirmed = Now.AddMinutes(-50);
        var existing = new[] { Entry("grp-team", confirmed, source: ResolvedTeamSource.Group) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing,
            [Attr(EntraCycleResult.ResolvedTeam, "cz-its"),
             Group(EntraCycleResult.Unreachable)],
            Now, Grace);

        var attr = Assert.Single(merged, e => e.Source == ResolvedTeamSource.Attribute);
        Assert.Equal("cz-its", attr.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, attr.State);

        var grp = Assert.Single(merged, e => e.Source == ResolvedTeamSource.Group);
        Assert.Equal("grp-team", grp.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, grp.State);
        Assert.Equal(confirmed, grp.LastConfirmedUtc);   // preserved
    }

    [Fact]
    public void AttributeUnreachableWithinGrace_AndGroupResolved_AttrGrace_GroupActive()
    {
        var confirmed = Now.AddMinutes(-50);
        var existing = new[] { Entry("cz-its", confirmed, source: ResolvedTeamSource.Attribute) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing,
            [Attr(EntraCycleResult.Unreachable),
             Group(EntraCycleResult.ResolvedTeam, "grp-team")],
            Now, Grace);

        var attr = Assert.Single(merged, e => e.Source == ResolvedTeamSource.Attribute);
        Assert.Equal("cz-its", attr.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, attr.State);

        var grp = Assert.Single(merged, e => e.Source == ResolvedTeamSource.Group);
        Assert.Equal("grp-team", grp.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, grp.State);
    }

    [Fact]
    public void AttributeReplaces_AndGroupNoTeamRemoves_OnlyNewAttributeRemains()
    {
        var existing = new[]
        {
            Entry("attr-y",   Now.AddHours(-1), source: ResolvedTeamSource.Attribute),
            Entry("grp-team", Now.AddHours(-1), source: ResolvedTeamSource.Group),
        };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing,
            [Attr(EntraCycleResult.ResolvedTeam, "attr-x"),
             Group(EntraCycleResult.NoTeam)],
            Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("attr-x", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }
}
