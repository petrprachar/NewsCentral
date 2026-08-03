using NewsCentral.Configuration;
using NewsCentral.Models;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EntraResolvedTeamsMergerTests
{
    private static readonly DateTime Now = new(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(240);

    private static readonly IReadOnlySet<EntraSourceKey> LegacyKeys = new HashSet<EntraSourceKey>
    {
        EntraSourceKey.Legacy(ResolvedTeamSource.Attribute),
        EntraSourceKey.Legacy(ResolvedTeamSource.Group)
    };

    private static ResolvedTeamEntry Entry(string name, DateTime confirmed,
        ResolvedTeamState state = ResolvedTeamState.Active,
        ResolvedTeamSource source = ResolvedTeamSource.Attribute,
        string sourceId = "") =>
        new() { TeamFolderName = name, LastConfirmedUtc = confirmed, State = state, Source = source, SourceId = sourceId };

    private static EntraSourceOutcome Attr(EntraCycleResult r, string? team = null) =>
        new(EntraSourceKey.Legacy(ResolvedTeamSource.Attribute), r, team);

    private static EntraSourceOutcome Group(EntraCycleResult r, string? team = null) =>
        new(EntraSourceKey.Legacy(ResolvedTeamSource.Group), r, team);

    // ── Single-source (Attribute) — behavior identical to the old single-team cases ──

    [Fact]
    public void ResolvedTeam_ProducesSingleActiveEntry()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [], [Attr(EntraCycleResult.ResolvedTeam, "cz-prague-its")], LegacyKeys, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-prague-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
        Assert.Equal("", only.SourceId);
        Assert.Equal(Now, only.LastConfirmedUtc);
    }

    [Fact]
    public void ResolvedTeam_ReplacesDifferentPriorTeam()
    {
        var existing = new[] { Entry("de-prod", Now.AddHours(-1)) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.ResolvedTeam, "cz-its")], LegacyKeys, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }

    [Fact]
    public void NoTeam_ProducesEmptyList()
    {
        var existing = new[] { Entry("cz-its", Now.AddMinutes(-5)) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.NoTeam)], LegacyKeys, Now, Grace);

        Assert.Empty(merged);
    }

    [Fact]
    public void Unreachable_WithinGrace_RetainedAsGrace_TimestampUnchanged()
    {
        var confirmed = Now.AddMinutes(-100);   // within 240-minute grace
        var existing = new[] { Entry("cz-its", confirmed) };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

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
            existing, [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

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
            existing, [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
    }

    [Fact]
    public void Unreachable_EmptyExisting_ProducesEmpty()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [], [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

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
            existing, [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

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
            existing, [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

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
            LegacyKeys, Now, Grace);

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
            LegacyKeys, Now, Grace);

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
            LegacyKeys, Now, Grace);

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
            LegacyKeys, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("attr-x", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }

    // ── EntraSourceKey / multi-instance facts ─────────────────────────────────

    [Fact]
    public void SourceKey_IdComparison_IsCaseInsensitive()
    {
        var a = new EntraSourceKey(ResolvedTeamSource.Attribute, "Site");
        var b = new EntraSourceKey(ResolvedTeamSource.Attribute, "site");

        Assert.Equal(a, b);
        Assert.True(a.Equals(b));

        var set = new HashSet<EntraSourceKey> { a };
        Assert.Contains(b, set);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void SameSource_DifferentIds_GraceIsIndependent()
    {
        var confirmedA = Now.AddMinutes(-5);
        var confirmedB = Now.AddMinutes(-100);
        var existing = new[]
        {
            Entry("team-a", confirmedA, source: ResolvedTeamSource.Attribute, sourceId: "a"),
            Entry("team-b", confirmedB, source: ResolvedTeamSource.Attribute, sourceId: "b"),
        };

        var keyA = new EntraSourceKey(ResolvedTeamSource.Attribute, "a");
        var keyB = new EntraSourceKey(ResolvedTeamSource.Attribute, "b");
        var activeKeys = new HashSet<EntraSourceKey> { keyA, keyB };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing,
            [
                new EntraSourceOutcome(keyA, EntraCycleResult.ResolvedTeam, "team-a-new"),
                new EntraSourceOutcome(keyB, EntraCycleResult.Unreachable, null),
            ],
            activeKeys, Now, Grace);

        Assert.Equal(2, merged.Count);

        var a = Assert.Single(merged, e => e.SourceId == "a");
        Assert.Equal("team-a-new", a.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, a.State);

        var b = Assert.Single(merged, e => e.SourceId == "b");
        Assert.Equal("team-b", b.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, b.State);
        Assert.Equal(confirmedB, b.LastConfirmedUtc);   // preserved
    }

    [Fact]
    public void EntryWithInactiveKey_IsDroppedWithoutGrace()
    {
        // Well inside the grace window — but its key is no longer configured, so it must NOT ride grace.
        var existing = new[]
        {
            Entry("retired-team", Now.AddMinutes(-5), source: ResolvedTeamSource.Group, sourceId: "retired"),
        };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [], LegacyKeys, Now, Grace);

        Assert.Empty(merged);
    }

    [Fact]
    public void EntryWithInactiveKey_IsDropped_EvenWhenAllSourcesUnreachable()
    {
        var confirmed = Now.AddMinutes(-5);
        var existing = new[]
        {
            Entry("cz-its",       confirmed, source: ResolvedTeamSource.Attribute),
            Entry("grp-team",     confirmed, source: ResolvedTeamSource.Group),
            Entry("retired-team", confirmed, source: ResolvedTeamSource.Group, sourceId: "retired"),
        };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing,
            [Attr(EntraCycleResult.Unreachable), Group(EntraCycleResult.Unreachable)],
            LegacyKeys, Now, Grace);

        Assert.Equal(2, merged.Count);
        Assert.DoesNotContain(merged, e => e.TeamFolderName == "retired-team");
        Assert.Contains(merged, e => e.TeamFolderName == "cz-its" && e.State == ResolvedTeamState.Grace);
        Assert.Contains(merged, e => e.TeamFolderName == "grp-team" && e.State == ResolvedTeamState.Grace);
    }

    [Fact]
    public void OutcomeForInactiveKey_IsIgnored()
    {
        var inactiveKey = new EntraSourceKey(ResolvedTeamSource.Group, "unconfigured");

        var merged = EntraResolvedTeamsMerger.Merge(
            [], [new EntraSourceOutcome(inactiveKey, EntraCycleResult.ResolvedTeam, "some-team")],
            LegacyKeys, Now, Grace);

        Assert.Empty(merged);
    }

    [Fact]
    public void DuplicateOutcomesForSameKey_FirstWins()
    {
        var merged = EntraResolvedTeamsMerger.Merge(
            [],
            [Attr(EntraCycleResult.ResolvedTeam, "first-team"),
             Attr(EntraCycleResult.ResolvedTeam, "second-team")],
            LegacyKeys, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("first-team", only.TeamFolderName);
    }

    [Fact]
    public void MissingSourceId_DefaultsToEmpty_AndMergesUnderLegacyKey()
    {
        // Simulates a pre-feature entry deserialized without a sourceId field (defaults to "").
        var existing = new[] { Entry("cz-its", Now.AddMinutes(-100)) };
        Assert.Equal("", existing[0].SourceId);

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.Equal("", only.SourceId);
    }

    // SourceId is declared non-nullable with a "" default, but System.Text.Json writes a literal
    // null into a non-nullable string property when the source JSON contains an explicit
    // "sourceId": null (e.g. a hand-edited or truncated resolved-teams.json) — unlike an omitted
    // property, which falls back to the "" default. EntraSourceKey must coalesce a null Id to ""
    // in both Equals and GetHashCode so such an entry keys as the legacy singleton instead of
    // throwing or being treated as an unconfigured instance.
    [Fact]
    public void NullSourceId_MergesUnderLegacyKey_AndIsNotDropped()
    {
        var confirmed = Now.AddMinutes(-50);   // within grace
        var existing = new[]
        {
            Entry("cz-its", confirmed, source: ResolvedTeamSource.Attribute, sourceId: null!),
        };

        var merged = EntraResolvedTeamsMerger.Merge(
            existing, [Attr(EntraCycleResult.Unreachable)], LegacyKeys, Now, Grace);

        var only = Assert.Single(merged);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.Equal(confirmed, only.LastConfirmedUtc);
    }
}
