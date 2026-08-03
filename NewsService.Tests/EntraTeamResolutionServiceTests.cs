using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NewsCentral.Models;
using NewsService;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

public sealed class EntraTeamResolutionServiceTests : IDisposable
{
    private const string FileName = "resolved-teams.json";

    // DeviceId must be a GUID (the orchestrator skips Graph otherwise); the device object id is a
    // separate directory id returned on Found and used for checkMemberGroups.
    private const string DeviceId       = "11111111-1111-1111-1111-111111111111";
    private const string DeviceObjectId = "22222222-2222-2222-2222-222222222222";

    private readonly List<string> _tempDirs = new();

    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeIdentity(string? id) : IDeviceIdentityProvider
    {
        public string? TryGetAzureAdDeviceId() => id;
    }

    private sealed class FakeClient : IEntraDeviceClient
    {
        private readonly EntraDeviceFetch? _result;
        private readonly bool _throws;

        private FakeClient(EntraDeviceFetch? result, bool throws)
        {
            _result = result;
            _throws = throws;
        }

        public static FakeClient Returning(EntraDeviceFetch result) => new(result, false);
        public static FakeClient Throwing() => new(null, true);

        public Task<EntraDeviceFetch> FetchAsync(string deviceId, CancellationToken ct)
        {
            if (_throws) throw new InvalidOperationException("simulated missing credentials");
            return Task.FromResult(_result!);
        }
    }

    private sealed class FakeGroupClient : IEntraGroupClient
    {
        private readonly EntraGroupEvaluation _eval;
        private readonly bool _failIfCalled;
        public int Calls { get; private set; }

        private FakeGroupClient(EntraGroupEvaluation eval, bool failIfCalled)
        {
            _eval = eval;
            _failIfCalled = failIfCalled;
        }

        public static FakeGroupClient With(EntraGroupStatus status, bool inInclusion = false, bool inExclusion = false)
            => new(new EntraGroupEvaluation(status, inInclusion, inExclusion), failIfCalled: false);

        /// <summary>A group client that must not be invoked (e.g. inclusion group empty).</summary>
        public static FakeGroupClient NeverCalled() =>
            new(new EntraGroupEvaluation(EntraGroupStatus.Unreachable), failIfCalled: true);

        public Task<EntraGroupEvaluation> EvaluateAsync(
            string deviceObjectId, string? inclusionName, string? exclusionName, CancellationToken ct)
        {
            Calls++;
            if (_failIfCalled)
                throw new InvalidOperationException("group client must not be called this cycle");
            return Task.FromResult(_eval);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nstests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private EntraTeamResolutionService CreateService(
        string dir, bool enabled, IDeviceIdentityProvider identity, IEntraDeviceClient client,
        Dictionary<string, string>? mappings = null, int graceMinutes = 240,
        IEntraGroupClient? group = null, string inclusion = "", string exclusion = "")
    {
        var cfg = new ServiceConfiguration();
        cfg.Service.CacheRootPath = dir;
        cfg.Entra.Enabled = enabled;
        cfg.Entra.GracePeriodMinutes = graceMinutes;
        cfg.Entra.Mappings = mappings ?? new();
        cfg.Entra.GroupTeam.InclusionGroup = inclusion;
        cfg.Entra.GroupTeam.ExclusionGroup = exclusion;

        return new EntraTeamResolutionService(
            cfg, identity, client, group ?? FakeGroupClient.NeverCalled(),
            NullLogger<EntraTeamResolutionService>.Instance);
    }

    private static void WriteFile(string dir, ResolvedTeamsFile file) =>
        File.WriteAllText(Path.Combine(dir, FileName),
            JsonSerializer.Serialize(file, JsonDefaults.Options));

    private static ResolvedTeamsFile? ReadFile(string dir)
    {
        var path = Path.Combine(dir, FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<ResolvedTeamsFile>(File.ReadAllText(path), JsonDefaults.Options)
            : null;
    }

    private static ResolvedTeamEntry Entry(string name, DateTime confirmed,
        ResolvedTeamState state = ResolvedTeamState.Active,
        ResolvedTeamSource source = ResolvedTeamSource.Attribute,
        string sourceId = "") =>
        new() { TeamFolderName = name, LastConfirmedUtc = confirmed, State = state, Source = source, SourceId = sourceId };

    private static EntraDeviceFetch FoundWith(params (string Key, string? Value)[] attrs)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, v) in attrs) d[k] = v;
        return EntraDeviceFetch.Found(d, DeviceObjectId);
    }

    // ── Existing single-source (Attribute) behavior ───────────────────────────

    [Fact]
    public async Task Disabled_DeletesExistingFile_WritesNothing()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry("cz-its", DateTime.UtcNow)]
        });

        var svc = CreateService(dir, enabled: false,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.NotFound));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(dir, FileName)));
    }

    [Fact]
    public async Task DeviceIdNull_TreatedAsUnreachable_NeverThrows()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(null), FakeClient.Throwing());   // client must not even be needed

        await svc.RefreshAsync(CancellationToken.None);

        var file = ReadFile(dir);
        Assert.NotNull(file);
        Assert.Empty(file!.Teams);   // Unreachable + no prior → empty
    }

    [Fact]
    public async Task DeviceIdNotGuid_SkipsGraph_TreatedAsUnreachable()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity("not-a-guid"), FakeClient.Throwing());   // client must not be called

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task Found_ResolvingToTeam_WritesOneActiveEntry()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" });

        await svc.RefreshAsync(CancellationToken.None);

        var file = ReadFile(dir);
        var only = Assert.Single(file!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
    }

    [Fact]
    public async Task Found_CleanNoTeam_UnknownSelector_WritesEmpty()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(("extensionAttribute1", "ZZZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" });

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task NotFound_WritesEmpty()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.NotFound));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task Unreachable_PriorWithinGrace_RetainedAsGrace_TimestampUnchanged()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-100);   // within 240
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow.AddMinutes(-100),
            Teams = [Entry("cz-its", confirmed)]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable));

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("cz-its", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.Equal(confirmed, only.LastConfirmedUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Unreachable_PriorBeyondGrace_Dropped()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow.AddMinutes(-300),
            Teams = [Entry("cz-its", DateTime.UtcNow.AddMinutes(-300))]   // past 240
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task ClientThrows_TreatedAsUnreachable_NeverThrows()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Throwing());

        await svc.RefreshAsync(CancellationToken.None);   // must not throw

        var file = ReadFile(dir);
        Assert.NotNull(file);
        Assert.Empty(file!.Teams);
    }

    [Fact]
    public async Task WrittenFile_RoundTrips_WithJsonDefaults()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" });

        await svc.RefreshAsync(CancellationToken.None);

        var file = ReadFile(dir);
        Assert.NotNull(file);
        Assert.NotEqual(default, file!.GeneratedUtc);
        Assert.Equal(ResolvedTeamState.Active, Assert.Single(file.Teams).State);
    }

    // ── Dual-source (Attribute + Group) ───────────────────────────────────────

    [Fact]
    public async Task Found_AttributeAndGroupBothResolve_TwoActiveEntries()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" },
            group: FakeGroupClient.With(EntraGroupStatus.Success, inInclusion: true),
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);

        var attr = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Attribute);
        Assert.Equal("cz", attr.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, attr.State);

        var grp = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Group);
        Assert.Equal("grp-team", grp.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, grp.State);
    }

    [Fact]
    public async Task Found_InclusionEmpty_EmitsGroupNoTeam_RemovesPriorGroupEntry()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry("old-grp", DateTime.UtcNow.AddMinutes(-5), source: ResolvedTeamSource.Group)]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" },
            group: FakeGroupClient.NeverCalled(),   // inclusion empty → group client must not be called
            inclusion: "");

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
    }

    [Fact]
    public async Task Found_GroupPermissionDenied_NoGroupEntry_AttributeUnaffected()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" },
            group: FakeGroupClient.With(EntraGroupStatus.PermissionDenied),
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
    }

    [Fact]
    public async Task Found_GroupNameAmbiguous_NoGroupEntry_AttributeUnaffected()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" },
            group: FakeGroupClient.With(EntraGroupStatus.NameAmbiguous),
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
    }

    [Fact]
    public async Task DeviceUnreachable_BothSourcesGraced()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-50);   // within grace
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow.AddMinutes(-50),
            Teams =
            [
                Entry("cz-its",   confirmed, source: ResolvedTeamSource.Attribute),
                Entry("grp-team", confirmed, source: ResolvedTeamSource.Group),
            ]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            group: FakeGroupClient.NeverCalled(),   // device unreachable short-circuits before group
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);
        Assert.Equal(ResolvedTeamState.Grace,
            Assert.Single(teams, t => t.Source == ResolvedTeamSource.Attribute).State);
        Assert.Equal(ResolvedTeamState.Grace,
            Assert.Single(teams, t => t.Source == ResolvedTeamSource.Group).State);
    }

    [Fact]
    public async Task DevicePermissionDenied_BothSourcesRemoved()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams =
            [
                Entry("cz-its",   DateTime.UtcNow.AddMinutes(-5), source: ResolvedTeamSource.Attribute),
                Entry("grp-team", DateTime.UtcNow.AddMinutes(-5), source: ResolvedTeamSource.Group),
            ]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.PermissionDenied),
            group: FakeGroupClient.NeverCalled(),
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task Found_GroupUnreachable_AttributeActive_PriorGroupGraced()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-50);   // within grace
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow.AddMinutes(-50),
            Teams = [Entry("grp-team", confirmed, source: ResolvedTeamSource.Group)]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" },
            group: FakeGroupClient.With(EntraGroupStatus.Unreachable),
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);

        var attr = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Attribute);
        Assert.Equal("cz", attr.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, attr.State);

        var grp = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Group);
        Assert.Equal("grp-team", grp.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, grp.State);
        Assert.Equal(confirmed, grp.LastConfirmedUtc, TimeSpan.FromSeconds(1));
    }

    // ── SourceId (key-with-instance) ──────────────────────────────────────────

    [Fact]
    public async Task Found_AttributeAndGroupBothResolve_EntriesCarryEmptySourceId()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" },
            group: FakeGroupClient.With(EntraGroupStatus.Success, inInclusion: true),
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);
        Assert.All(teams, t => Assert.Equal("", t.SourceId));
    }

    [Fact]
    public async Task PreExisting_EntryWithUnknownSourceId_IsPruned_LegacyEntriesResolveNormally()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry("stale-team", DateTime.UtcNow.AddMinutes(-5),
                source: ResolvedTeamSource.Group, sourceId: "stale")]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" },
            group: FakeGroupClient.With(EntraGroupStatus.Success, inInclusion: true),
            inclusion: "Grp Team");

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.DoesNotContain(teams, t => t.TeamFolderName == "stale-team");

        var attr = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Attribute);
        Assert.Equal("cz", attr.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, attr.State);

        var grp = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Group);
        Assert.Equal("grp-team", grp.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, grp.State);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
