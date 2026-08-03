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

        /// <summary>Number of times <see cref="FetchAsync"/> was invoked — used to assert the
        /// device fetch is skipped entirely when nothing is configured to resolve.</summary>
        public int Calls { get; private set; }

        private FakeClient(EntraDeviceFetch? result, bool throws)
        {
            _result = result;
            _throws = throws;
        }

        public static FakeClient Returning(EntraDeviceFetch result) => new(result, false);
        public static FakeClient Throwing() => new(null, true);

        public Task<EntraDeviceFetch> FetchAsync(string deviceId, CancellationToken ct)
        {
            Calls++;
            if (_throws) throw new InvalidOperationException("simulated missing credentials");
            return Task.FromResult(_result!);
        }
    }

    private sealed class FakeGroupClient : IEntraGroupClient
    {
        private readonly EntraGroupSnapshot _snapshot;
        private readonly bool _failIfCalled;
        public int Calls { get; private set; }

        private FakeGroupClient(EntraGroupSnapshot snapshot, bool failIfCalled)
        {
            _snapshot = snapshot;
            _failIfCalled = failIfCalled;
        }

        public static FakeGroupClient With(EntraGroupSnapshot snapshot) => new(snapshot, failIfCalled: false);

        /// <summary>A group client that must not be invoked (e.g. no group instances configured).</summary>
        public static FakeGroupClient NeverCalled() =>
            new(Snapshot(EntraGroupStatus.Unreachable), failIfCalled: true);

        public Task<EntraGroupSnapshot> EvaluateAsync(
            string deviceObjectId, IReadOnlyCollection<string> groupNames, CancellationToken ct)
        {
            Calls++;
            if (_failIfCalled)
                throw new InvalidOperationException("group client must not be called this cycle");
            return Task.FromResult(_snapshot);
        }

        public static EntraGroupSnapshot Snapshot(
            EntraGroupStatus status,
            IReadOnlyDictionary<string, EntraGroupStatus>? nameStatus = null,
            IReadOnlyCollection<string>? memberOf = null) =>
            new(status,
                nameStatus ?? new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(memberOf ?? [], StringComparer.OrdinalIgnoreCase));

        public static Dictionary<string, EntraGroupStatus> Resolved(params string[] names) =>
            names.ToDictionary(n => n, _ => EntraGroupStatus.Success, StringComparer.OrdinalIgnoreCase);
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
        Dictionary<string, AttributeSchemeOptions>? schemes = null, int graceMinutes = 240,
        IEntraGroupClient? group = null,
        Dictionary<string, GroupTeamOptions>? groupInstances = null, string globalExclusion = "")
    {
        var cfg = new ServiceConfiguration();
        cfg.Service.CacheRootPath = dir;
        cfg.Entra.Enabled = enabled;
        cfg.Entra.GracePeriodMinutes = graceMinutes;
        cfg.Entra.AttributeSchemes = schemes ?? new();
        cfg.Entra.GroupTeams.Instances = groupInstances ?? new();
        cfg.Entra.GroupTeams.ExclusionGroup = globalExclusion;

        return new EntraTeamResolutionService(
            cfg, identity, client, group ?? FakeGroupClient.NeverCalled(),
            NullLogger<EntraTeamResolutionService>.Instance);
    }

    private static AttributeSchemeOptions Scheme(
        Dictionary<string, string>? mappings = null, string selector = "extensionAttribute1") =>
        new() { Selector = selector, Mappings = mappings ?? new() };

    private static Dictionary<string, AttributeSchemeOptions> OneScheme(
        string name, Dictionary<string, string>? mappings = null, string selector = "extensionAttribute1") =>
        new() { [name] = Scheme(mappings, selector) };

    private static GroupTeamOptions GroupInstance(string inclusion, string exclusion = "") =>
        new() { InclusionGroup = inclusion, ExclusionGroup = exclusion };

    private static Dictionary<string, GroupTeamOptions> OneGroupInstance(
        string label, string inclusion, string exclusion = "") =>
        new() { [label] = GroupInstance(inclusion, exclusion) };

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

    // ── Existing single-scheme (Attribute) behavior ───────────────────────────

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
            new FakeIdentity(null), FakeClient.Throwing(),   // client must not even be needed
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

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
            new FakeIdentity("not-a-guid"), FakeClient.Throwing(),   // client must not be called
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

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
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

        await svc.RefreshAsync(CancellationToken.None);

        var file = ReadFile(dir);
        var only = Assert.Single(file!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
        Assert.Equal("primary", only.SourceId);
    }

    [Fact]
    public async Task Found_CleanNoTeam_UnknownSelector_WritesEmpty()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(("extensionAttribute1", "ZZZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task NotFound_WritesEmpty()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.NotFound),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

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
            Teams = [Entry("cz-its", confirmed, sourceId: "primary")]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

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
            Teams = [Entry("cz-its", DateTime.UtcNow.AddMinutes(-300), sourceId: "primary")]   // past 240
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task ClientThrows_TreatedAsUnreachable_NeverThrows()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Throwing(),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

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
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

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
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, FakeGroupClient.Resolved("Grp Team"), memberOf: ["Grp Team"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"), group: FakeGroupClient.With(snapshot));

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
    public async Task Found_NoGroupInstancesConfigured_RemovesPriorGroupEntry()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry("old-grp", DateTime.UtcNow.AddMinutes(-5), source: ResolvedTeamSource.Group, sourceId: "old-grp")]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            group: FakeGroupClient.NeverCalled());   // no group instances configured → group client must not be called

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
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"),
            group: FakeGroupClient.With(FakeGroupClient.Snapshot(EntraGroupStatus.PermissionDenied)));

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
    }

    [Fact]
    public async Task Found_GroupNameAmbiguous_NoGroupEntry_AttributeUnaffected()
    {
        var dir = NewTempDir();
        var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["Grp Team"] = EntraGroupStatus.NameAmbiguous,
        };
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"),
            group: FakeGroupClient.With(FakeGroupClient.Snapshot(EntraGroupStatus.Success, nameStatus)));

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
                Entry("cz-its",   confirmed, source: ResolvedTeamSource.Attribute, sourceId: "primary"),
                Entry("grp-team", confirmed, source: ResolvedTeamSource.Group, sourceId: "grp-team"),
            ]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"),
            group: FakeGroupClient.NeverCalled());   // device unreachable short-circuits before group

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
                Entry("cz-its",   DateTime.UtcNow.AddMinutes(-5), source: ResolvedTeamSource.Attribute, sourceId: "primary"),
                Entry("grp-team", DateTime.UtcNow.AddMinutes(-5), source: ResolvedTeamSource.Group, sourceId: "grp-team"),
            ]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.PermissionDenied),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"),
            group: FakeGroupClient.NeverCalled());

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
            Teams = [Entry("grp-team", confirmed, source: ResolvedTeamSource.Group, sourceId: "grp-team")]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"),
            group: FakeGroupClient.With(FakeGroupClient.Snapshot(EntraGroupStatus.Unreachable)));

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

    // Before M3, the group source was a legacy singleton and always carried SourceId "". After M3,
    // the group source is multi-instance and the SourceId is DERIVED — Canonicalize(InclusionGroup),
    // identical to the team folder name — so it is never "" for an active instance.
    [Fact]
    public async Task Found_AttributeAndGroupBothResolve_BothCarryDerivedSourceIds()
    {
        var dir = NewTempDir();
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, FakeGroupClient.Resolved("Grp Team"), memberOf: ["Grp Team"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"), group: FakeGroupClient.With(snapshot));

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);

        var attr = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Attribute);
        Assert.Equal("primary", attr.SourceId);   // authored scheme name — unchanged from M2

        var grp = Assert.Single(teams, t => t.Source == ResolvedTeamSource.Group);
        Assert.Equal("grp-team", grp.SourceId);   // derived from InclusionGroup, not the label "grp"
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

        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, FakeGroupClient.Resolved("Grp Team"), memberOf: ["Grp Team"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: OneGroupInstance("grp", "Grp Team"), group: FakeGroupClient.With(snapshot));

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

    // ── Multiple named attribute schemes (M2) ─────────────────────────────────

    [Fact]
    public async Task TwoSchemes_BothResolve_TwoActiveEntriesWithSchemeIds()
    {
        var dir = NewTempDir();
        var schemes = new Dictionary<string, AttributeSchemeOptions>
        {
            ["alpha"] = Scheme(new() { ["FAT"] = "extensionAttribute2" }, "extensionAttribute1"),
            ["beta"]  = Scheme(new() { ["VDE"] = "extensionAttribute3" }, "extensionAttribute7"),
        };

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"),
                ("extensionAttribute7", "VDE"),
                ("extensionAttribute3", "Berlin"))),
            schemes: schemes);

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);

        var alpha = Assert.Single(teams, t => t.SourceId == "alpha");
        Assert.Equal("cz", alpha.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, alpha.Source);
        Assert.Equal(ResolvedTeamState.Active, alpha.State);

        var beta = Assert.Single(teams, t => t.SourceId == "beta");
        Assert.Equal("berlin", beta.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, beta.Source);
        Assert.Equal(ResolvedTeamState.Active, beta.State);
    }

    [Fact]
    public async Task TwoSchemes_OneResolvesOneDoesNot_OnlyResolvedPersists()
    {
        var dir = NewTempDir();
        var schemes = new Dictionary<string, AttributeSchemeOptions>
        {
            ["alpha"] = Scheme(new() { ["FAT"] = "extensionAttribute2" }, "extensionAttribute1"),
            ["beta"]  = Scheme(new() { ["VDE"] = "extensionAttribute3" }, "extensionAttribute7"),   // no attr7 on device
        };

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: schemes);

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("alpha", only.SourceId);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }

    [Fact]
    public async Task SchemeRemovedFromConfig_PriorEntryPruned()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-50);   // within grace
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow.AddMinutes(-50),
            Teams =
            [
                Entry("gone-team",    confirmed, source: ResolvedTeamSource.Attribute, sourceId: "gone"),
                Entry("primary-team", confirmed, source: ResolvedTeamSource.Attribute, sourceId: "primary"),
            ]
        });

        // "gone" is no longer present in configuration — only "primary" remains.
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        var only = Assert.Single(teams);
        Assert.Equal("primary-team", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.DoesNotContain(teams, t => t.SourceId == "gone");
    }

    [Fact]
    public async Task LegacyEmptySourceIdEntry_IsPruned()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-50);   // within grace — would ride grace if still active
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow.AddMinutes(-50),
            Teams = [Entry("old-legacy-team", confirmed, source: ResolvedTeamSource.Attribute, sourceId: "")]
        });

        // Entra:Mappings (the pre-M2 flat, unnamed scheme) no longer exists as a concept — "" is
        // not a configured scheme id.
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.DoesNotContain(teams, t => t.TeamFolderName == "old-legacy-team");
    }

    [Fact]
    public async Task InvalidSchemeName_IsSkippedWithWarning_AndNotInActiveKeys()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-5);   // well within grace
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow.AddMinutes(-5),
            Teams = [Entry("stale", confirmed, source: ResolvedTeamSource.Attribute, sourceId: "bad:name")]
        });

        var schemes = new Dictionary<string, AttributeSchemeOptions>
        {
            ["good"]     = Scheme(new() { ["FAT"] = "extensionAttribute2" }),
            ["bad:name"] = Scheme(new()),
        };

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: schemes);

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        var only = Assert.Single(teams);
        Assert.Equal("good", only.SourceId);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.DoesNotContain(teams, t => t.SourceId == "bad:name");
    }

    [Fact]
    public async Task TwoSchemes_SameTeamName_BothEntriesKept()
    {
        var dir = NewTempDir();
        var schemes = new Dictionary<string, AttributeSchemeOptions>
        {
            ["alpha"] = Scheme(new() { ["FAT"] = "extensionAttribute2" }, "extensionAttribute1"),
            ["beta"]  = Scheme(new() { ["FAT"] = "extensionAttribute8" }, "extensionAttribute7"),
        };

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "same-team"),
                ("extensionAttribute7", "FAT"),
                ("extensionAttribute8", "same-team"))),
            schemes: schemes);

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);
        Assert.All(teams, t => Assert.Equal("same-team", t.TeamFolderName));

        Assert.Contains(teams, t => t.SourceId == "alpha");
        Assert.Contains(teams, t => t.SourceId == "beta");
    }

    [Fact]
    public async Task DeviceUnreachable_AllSchemesGrace_Independently()
    {
        var dir = NewTempDir();
        var withinGrace = DateTime.UtcNow.AddMinutes(-100);    // within 240
        var beyondGrace = DateTime.UtcNow.AddMinutes(-300);    // past 240
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams =
            [
                Entry("alpha-team", withinGrace, source: ResolvedTeamSource.Attribute, sourceId: "alpha"),
                Entry("beta-team",  beyondGrace, source: ResolvedTeamSource.Attribute, sourceId: "beta"),
            ]
        });

        var schemes = new Dictionary<string, AttributeSchemeOptions>
        {
            ["alpha"] = Scheme(new() { ["FAT"] = "extensionAttribute2" }),
            ["beta"]  = Scheme(new() { ["FAT"] = "extensionAttribute2" }),
        };

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            schemes: schemes);

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        var only = Assert.Single(teams);
        Assert.Equal("alpha-team", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.Equal(withinGrace, only.LastConfirmedUtc, TimeSpan.FromSeconds(1));
    }

    // ── Multiple named group instances + global exclusion (M3) ────────────────

    [Fact]
    public async Task TwoInstances_BothResolve_EntriesCarryDerivedSourceIds()
    {
        var dir = NewTempDir();
        var groupInstances = new Dictionary<string, GroupTeamOptions>
        {
            ["pilot-1"] = GroupInstance("NewsCentral Prague ITS"),
            ["pilot-2"] = GroupInstance("NewsCentral Brno QA"),
        };
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success,
            FakeGroupClient.Resolved("NewsCentral Prague ITS", "NewsCentral Brno QA"),
            memberOf: ["NewsCentral Prague ITS", "NewsCentral Brno QA"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(FoundWith()),
            groupInstances: groupInstances, group: FakeGroupClient.With(snapshot));

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.Equal(2, teams.Count);

        const string pragueId = "newscentral-prague-its";
        const string brnoId   = "newscentral-brno-qa";

        var prague = Assert.Single(teams, t => t.SourceId == pragueId);
        Assert.Equal(pragueId, prague.TeamFolderName);

        var brno = Assert.Single(teams, t => t.SourceId == brnoId);
        Assert.Equal(brnoId, brno.TeamFolderName);
    }

    [Fact]
    public async Task InstanceLabel_DoesNotAppearInResolvedTeams()
    {
        var dir = NewTempDir();
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, FakeGroupClient.Resolved("NewsCentral Prague ITS"),
            memberOf: ["NewsCentral Prague ITS"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(FoundWith()),
            groupInstances: OneGroupInstance("pilot-1", "NewsCentral Prague ITS"),
            group: FakeGroupClient.With(snapshot));

        await svc.RefreshAsync(CancellationToken.None);

        var raw = File.ReadAllText(Path.Combine(dir, FileName));
        Assert.DoesNotContain("pilot-1", raw, StringComparison.OrdinalIgnoreCase);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("newscentral-prague-its", only.TeamFolderName);
        Assert.Equal("newscentral-prague-its", only.SourceId);
    }

    [Fact]
    public async Task GlobalExclusionUnresolvable_AllGroupInstancesGoDark_AttributeUnaffected()
    {
        var dir = NewTempDir();
        var groupInstances = new Dictionary<string, GroupTeamOptions>
        {
            ["pilot-1"] = GroupInstance("NewsCentral Prague ITS"),
            ["pilot-2"] = GroupInstance("NewsCentral Brno QA"),
        };
        var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["NewsCentral Prague ITS"] = EntraGroupStatus.Success,
            ["NewsCentral Brno QA"]    = EntraGroupStatus.Success,
            ["Fleet Kill Switch"]      = EntraGroupStatus.NameNotFound,
        };
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, nameStatus,
            memberOf: ["NewsCentral Prague ITS", "NewsCentral Brno QA"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }),
            groupInstances: groupInstances, globalExclusion: "Fleet Kill Switch",
            group: FakeGroupClient.With(snapshot));

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        var only = Assert.Single(teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamSource.Attribute, only.Source);
    }

    [Fact]
    public async Task InstanceExclusionUnresolvable_OnlyThatInstanceAffected()
    {
        var dir = NewTempDir();
        var groupInstances = new Dictionary<string, GroupTeamOptions>
        {
            ["pilot-1"] = GroupInstance("NewsCentral Prague ITS", "Bad Exclusion Name"),
            ["pilot-2"] = GroupInstance("NewsCentral Brno QA"),
        };
        var nameStatus = new Dictionary<string, EntraGroupStatus>(StringComparer.OrdinalIgnoreCase)
        {
            ["NewsCentral Prague ITS"] = EntraGroupStatus.Success,
            ["NewsCentral Brno QA"]    = EntraGroupStatus.Success,
            ["Bad Exclusion Name"]     = EntraGroupStatus.NameNotFound,
        };
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, nameStatus,
            memberOf: ["NewsCentral Prague ITS", "NewsCentral Brno QA"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(FoundWith()),
            groupInstances: groupInstances, group: FakeGroupClient.With(snapshot));

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("newscentral-brno-qa", only.TeamFolderName);
    }

    [Fact]
    public async Task InclusionGroupChanged_OldKeyPrunedWithoutGrace_NewKeyStartsActive()
    {
        var dir = NewTempDir();
        const string oldId = "newscentral-prague-its";
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry(oldId, DateTime.UtcNow.AddMinutes(-5), source: ResolvedTeamSource.Group, sourceId: oldId)]
        });

        // Same label, retargeted InclusionGroup — the derived id (and thus the key) changes.
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, FakeGroupClient.Resolved("NewsCentral Brno QA"),
            memberOf: ["NewsCentral Brno QA"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(FoundWith()),
            groupInstances: OneGroupInstance("pilot-1", "NewsCentral Brno QA"),
            group: FakeGroupClient.With(snapshot));

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        Assert.DoesNotContain(teams, t => t.SourceId == oldId);
        var only = Assert.Single(teams);
        Assert.Equal("newscentral-brno-qa", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }

    [Fact]
    public async Task TwoInstancesDerivingSameId_FirstByLabelWins()
    {
        var dir = NewTempDir();
        // Both canonicalize to "newscentral-prague-its"; "alpha" < "beta" ordinally, so alpha wins
        // and only ITS InclusionGroup ("NewsCentral Prague ITS") is evaluated.
        var groupInstances = new Dictionary<string, GroupTeamOptions>
        {
            ["beta"]  = GroupInstance("newscentral prague its"),
            ["alpha"] = GroupInstance("NewsCentral Prague ITS"),
        };
        var snapshot = FakeGroupClient.Snapshot(
            EntraGroupStatus.Success, FakeGroupClient.Resolved("NewsCentral Prague ITS"),
            memberOf: ["NewsCentral Prague ITS"]);

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(FoundWith()),
            groupInstances: groupInstances, group: FakeGroupClient.With(snapshot));

        await svc.RefreshAsync(CancellationToken.None);

        // The id collides, so only the first-by-label instance survives — exactly one team.
        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("newscentral-prague-its", only.TeamFolderName);
    }

    [Fact]
    public async Task BlankInclusionGroup_InstanceSkipped_PriorEntryPruned()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-5);
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry("stale-grp", confirmed, source: ResolvedTeamSource.Group, sourceId: "stale-grp")]
        });

        // Blank InclusionGroup → the instance is skipped during validation and contributes no key;
        // with no attribute schemes either, this also exercises the skip-device-fetch path.
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Throwing(),
            groupInstances: OneGroupInstance("pilot-1", ""));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task NoGroupInstances_GroupClientNeverCalled()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            schemes: OneScheme("primary", new() { ["FAT"] = "extensionAttribute2" }));
            // groupInstances omitted (empty); group omitted → defaults to FakeGroupClient.NeverCalled()

        await svc.RefreshAsync(CancellationToken.None);

        var only = Assert.Single(ReadFile(dir)!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
    }

    [Fact]
    public async Task NoSchemesAndNoGroupInstances_SkipsDeviceFetch()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry("stale-team", DateTime.UtcNow.AddMinutes(-5))]
        });

        var client = FakeClient.Throwing();   // would surface as a failure if ever invoked
        var svc = CreateService(dir, enabled: true, new FakeIdentity(DeviceId), client);
        // schemes and groupInstances both default to empty — nothing configured

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Equal(0, client.Calls);
        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task DeviceUnreachable_AllInstancesGraceIndependently()
    {
        var dir = NewTempDir();
        var withinGrace = DateTime.UtcNow.AddMinutes(-100);   // within 240
        var beyondGrace = DateTime.UtcNow.AddMinutes(-300);   // past 240

        const string idA = "newscentral-prague-its";
        const string idB = "newscentral-brno-qa";

        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams =
            [
                Entry(idA, withinGrace, source: ResolvedTeamSource.Group, sourceId: idA),
                Entry(idB, beyondGrace, source: ResolvedTeamSource.Group, sourceId: idB),
            ]
        });

        var groupInstances = new Dictionary<string, GroupTeamOptions>
        {
            ["pilot-1"] = GroupInstance("NewsCentral Prague ITS"),
            ["pilot-2"] = GroupInstance("NewsCentral Brno QA"),
        };

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            groupInstances: groupInstances);   // group client not called — device fetch fails first

        await svc.RefreshAsync(CancellationToken.None);

        var teams = ReadFile(dir)!.Teams;
        var only = Assert.Single(teams);
        Assert.Equal(idA, only.SourceId);
        Assert.Equal(ResolvedTeamState.Grace, only.State);
        Assert.Equal(withinGrace, only.LastConfirmedUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task StaleLegacyGroupEntry_IsPruned()
    {
        var dir = NewTempDir();
        var confirmed = DateTime.UtcNow.AddMinutes(-5);   // well within grace
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [Entry("old-legacy-grp", confirmed, source: ResolvedTeamSource.Group, sourceId: "")]
        });

        // Entra:GroupTeam (the pre-M3 flat singleton) no longer exists as a concept — "" is not a
        // configured group instance id.
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity(DeviceId), FakeClient.Returning(EntraDeviceFetch.Unreachable),
            groupInstances: OneGroupInstance("pilot-1", "NewsCentral Prague ITS"));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.DoesNotContain(ReadFile(dir)!.Teams, t => t.TeamFolderName == "old-legacy-grp");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
