using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers GroupOutcomeMapper — a membership snapshot → per-instance EntraSourceOutcome.
/// Persistent failures (name not found / ambiguous / 403) → NoTeam; transient → Unreachable;
/// a resolvable snapshot → the GroupTeamDecision result. An unresolvable GLOBAL exclusion is
/// fail-closed and pre-empts everything except the transport-level checks.
/// </summary>
public sealed class GroupOutcomeMapperTests
{
    private const string Inclusion         = "NewsCentral Prague ITS";
    private const string InstanceExclusion = "Excluded Devices";
    private const string GlobalExclusion   = "Fleet Kill Switch";

    private static readonly EntraSourceKey Key = new(ResolvedTeamSource.Group, "newscentral-prague-its");

    private static EntraGroupSnapshot Snapshot(
        EntraGroupStatus status,
        IReadOnlyDictionary<string, EntraGroupStatus>? nameStatus = null,
        IReadOnlyCollection<string>? memberOf = null) =>
        new(status,
            nameStatus ?? new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(memberOf ?? [], StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, EntraGroupStatus> Resolved(params string[] names) =>
        names.ToDictionary(n => n, _ => EntraGroupStatus.Success, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void InclusionResolved_Member_NoExclusions_ResolvesCanonicalTeam()
    {
        var snapshot = Snapshot(EntraGroupStatus.Success, Resolved(Inclusion), memberOf: [Inclusion]);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, null, null);

        Assert.Equal(Key, outcome.Key);
        Assert.Equal(EntraCycleResult.ResolvedTeam, outcome.Result);
        Assert.Equal("newscentral-prague-its", outcome.Team);
    }

    [Fact]
    public void MemberOfInstanceExclusion_NoTeam()
    {
        var snapshot = Snapshot(EntraGroupStatus.Success,
            Resolved(Inclusion, InstanceExclusion), memberOf: [Inclusion, InstanceExclusion]);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, InstanceExclusion, null);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void MemberOfGlobalExclusion_NoTeam_EvenWhenNotInInstanceExclusion()
    {
        var snapshot = Snapshot(EntraGroupStatus.Success,
            Resolved(Inclusion, InstanceExclusion, GlobalExclusion),
            memberOf: [Inclusion, GlobalExclusion]);   // in inclusion + global, NOT in instance exclusion

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, InstanceExclusion, GlobalExclusion);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void GlobalExclusionConfigured_Resolves_DeviceNotAMember_TeamGranted()
    {
        // The everyday case: a global exclusion exists, resolves fine, but this device isn't in it.
        var snapshot = Snapshot(EntraGroupStatus.Success,
            Resolved(Inclusion, GlobalExclusion), memberOf: [Inclusion]);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, null, GlobalExclusion);

        Assert.Equal(EntraCycleResult.ResolvedTeam, outcome.Result);
        Assert.Equal("newscentral-prague-its", outcome.Team);
    }

    [Fact]
    public void GlobalExclusionNameNotFound_NoTeam_EvenThoughInInclusionAndInNoExclusion()
    {
        var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase)
        {
            [Inclusion]       = EntraGroupStatus.Success,
            [GlobalExclusion] = EntraGroupStatus.NameNotFound,
        };
        var snapshot = Snapshot(EntraGroupStatus.Success, nameStatus, memberOf: [Inclusion]);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, null, GlobalExclusion);

        // Fail-closed: the device IS in inclusion and in no exclusion, but the kill switch's own
        // name can't be resolved, so every group instance goes dark rather than silently granting.
        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void GlobalExclusionNameAmbiguous_NoTeam()
    {
        var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase)
        {
            [Inclusion]       = EntraGroupStatus.Success,
            [GlobalExclusion] = EntraGroupStatus.NameAmbiguous,
        };
        var snapshot = Snapshot(EntraGroupStatus.Success, nameStatus, memberOf: [Inclusion]);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, null, GlobalExclusion);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Theory]
    [InlineData(EntraGroupStatus.NameNotFound)]
    [InlineData(EntraGroupStatus.NameAmbiguous)]
    public void InclusionUnresolvable_NoTeam(EntraGroupStatus status)
    {
        var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase)
        {
            [Inclusion] = status,
        };
        var snapshot = Snapshot(EntraGroupStatus.Success, nameStatus);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, null, null);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void InstanceExclusionNameNotFound_NoTeam()
    {
        var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase)
        {
            [Inclusion]         = EntraGroupStatus.Success,
            [InstanceExclusion] = EntraGroupStatus.NameNotFound,
        };
        var snapshot = Snapshot(EntraGroupStatus.Success, nameStatus, memberOf: [Inclusion]);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, InstanceExclusion, null);

        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void PermissionDenied_NoTeam()
    {
        var snapshot = Snapshot(EntraGroupStatus.PermissionDenied);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, null, null);

        Assert.Equal(Key, outcome.Key);
        Assert.Equal(EntraCycleResult.NoTeam, outcome.Result);
    }

    [Fact]
    public void Unreachable_GivesUnreachable()
    {
        var snapshot = Snapshot(EntraGroupStatus.Unreachable);

        var outcome = GroupOutcomeMapper.Map(snapshot, Key, Inclusion, null, null);

        Assert.Equal(Key, outcome.Key);
        Assert.Equal(EntraCycleResult.Unreachable, outcome.Result);
    }

    [Fact]
    public void ReturnedOutcome_AlwaysCarriesThePassedInKey()
    {
        var customKey = new EntraSourceKey(ResolvedTeamSource.Group, "second-instance");
        var snapshot = Snapshot(EntraGroupStatus.Success, Resolved(Inclusion), memberOf: [Inclusion]);

        var outcome = GroupOutcomeMapper.Map(snapshot, customKey, Inclusion, null, null);

        Assert.Equal(customKey, outcome.Key);
    }
}
