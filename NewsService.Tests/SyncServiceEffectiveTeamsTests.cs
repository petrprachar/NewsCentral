using System.Text.Json;
using NewsCentral.Models;
using NewsService;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers SyncService.ResolveEffectiveTeams — the static ∪ dynamic team-set computation used to
/// drive the poll cycle. The verification precedence itself is proven in NewsCentral.Shared.Tests.
/// </summary>
public sealed class SyncServiceEffectiveTeamsTests : IDisposable
{
    private readonly string _dir;

    public SyncServiceEffectiveTeamsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "syncteams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private void WriteResolved(params string[] folders)
    {
        var file = new ResolvedTeamsFile
        {
            GeneratedUtc = DateTime.UtcNow,
            Teams = folders.Select(f => new ResolvedTeamEntry
            {
                TeamFolderName = f, LastConfirmedUtc = DateTime.UtcNow
            }).ToList()
        };
        File.WriteAllText(Path.Combine(_dir, "resolved-teams.json"),
            JsonSerializer.Serialize(file, JsonDefaults.Options));
    }

    [Fact]
    public void UnionsAndDeDupes_CaseInsensitive()
    {
        WriteResolved("DE-PROD", "cz-prague-its");   // DE-PROD overlaps de-prod (different case)

        var (effective, dynamicTeams) = SyncService.ResolveEffectiveTeams(
            new[] { "cz-its", "de-prod" }, _dir);

        Assert.Equal(new[] { "cz-its", "de-prod", "cz-prague-its" }, effective);
        Assert.Contains("de-prod", dynamicTeams);          // ordinal-ignore-case set
        Assert.Contains("cz-prague-its", dynamicTeams);
    }

    [Fact]
    public void DynamicEmpty_ReturnsStaticOnly()
    {
        // No resolved-teams.json written at all.
        var (effective, dynamicTeams) = SyncService.ResolveEffectiveTeams(
            new[] { "cz-its", "de-prod" }, _dir);

        Assert.Equal(new[] { "cz-its", "de-prod" }, effective);
        Assert.Empty(dynamicTeams);
    }

    [Fact]
    public void DynamicOnly_NoStatic_ReturnsDynamic()
    {
        WriteResolved("cz-prague-its");

        var (effective, dynamicTeams) = SyncService.ResolveEffectiveTeams(
            Array.Empty<string>(), _dir);

        Assert.Equal(new[] { "cz-prague-its" }, effective);
        Assert.Contains("cz-prague-its", dynamicTeams);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
