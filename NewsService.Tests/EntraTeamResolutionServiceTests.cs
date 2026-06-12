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
        Dictionary<string, string>? mappings = null, int graceMinutes = 240)
    {
        var cfg = new ServiceConfiguration();
        cfg.Service.CacheRootPath = dir;
        cfg.Entra.Enabled = enabled;
        cfg.Entra.GracePeriodMinutes = graceMinutes;
        cfg.Entra.Mappings = mappings ?? new();

        return new EntraTeamResolutionService(
            cfg, identity, client, NullLogger<EntraTeamResolutionService>.Instance);
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

    private static EntraDeviceFetch FoundWith(params (string Key, string? Value)[] attrs)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, v) in attrs) d[k] = v;
        return EntraDeviceFetch.Found(d);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disabled_DeletesExistingFile_WritesNothing()
    {
        var dir = NewTempDir();
        WriteFile(dir, new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = [new ResolvedTeamEntry { TeamFolderName = "cz-its", LastConfirmedUtc = DateTime.UtcNow }]
        });

        var svc = CreateService(dir, enabled: false,
            new FakeIdentity("dev-1"), FakeClient.Returning(EntraDeviceFetch.NotFound));

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
    public async Task Found_ResolvingToTeam_WritesOneActiveEntry()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity("dev-1"),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" });

        await svc.RefreshAsync(CancellationToken.None);

        var file = ReadFile(dir);
        var only = Assert.Single(file!.Teams);
        Assert.Equal("cz", only.TeamFolderName);
        Assert.Equal(ResolvedTeamState.Active, only.State);
    }

    [Fact]
    public async Task Found_CleanNoTeam_UnknownSelector_WritesEmpty()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity("dev-1"),
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
            new FakeIdentity("dev-1"), FakeClient.Returning(EntraDeviceFetch.NotFound));

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
            Teams = [new ResolvedTeamEntry
            {
                TeamFolderName = "cz-its", LastConfirmedUtc = confirmed, State = ResolvedTeamState.Active
            }]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity("dev-1"), FakeClient.Returning(EntraDeviceFetch.Unreachable));

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
            Teams = [new ResolvedTeamEntry
            {
                TeamFolderName = "cz-its",
                LastConfirmedUtc = DateTime.UtcNow.AddMinutes(-300),   // past 240
                State = ResolvedTeamState.Active
            }]
        });

        var svc = CreateService(dir, enabled: true,
            new FakeIdentity("dev-1"), FakeClient.Returning(EntraDeviceFetch.Unreachable));

        await svc.RefreshAsync(CancellationToken.None);

        Assert.Empty(ReadFile(dir)!.Teams);
    }

    [Fact]
    public async Task ClientThrows_TreatedAsUnreachable_NeverThrows()
    {
        var dir = NewTempDir();
        var svc = CreateService(dir, enabled: true,
            new FakeIdentity("dev-1"), FakeClient.Throwing());

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
            new FakeIdentity("dev-1"),
            FakeClient.Returning(FoundWith(
                ("extensionAttribute1", "FAT"),
                ("extensionAttribute2", "CZ"))),
            mappings: new() { ["FAT"] = "extensionAttribute2" });

        await svc.RefreshAsync(CancellationToken.None);

        // Deserialize back with JsonDefaults.Options — enum State must round-trip by name.
        var file = ReadFile(dir);
        Assert.NotNull(file);
        Assert.NotEqual(default, file!.GeneratedUtc);
        Assert.Equal(ResolvedTeamState.Active, Assert.Single(file.Teams).State);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
