using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class ResolvedTeamsReaderTests : IDisposable
{
    private readonly string _dir;

    public ResolvedTeamsReaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rtr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private void Write(string contents) =>
        File.WriteAllText(Path.Combine(_dir, "resolved-teams.json"), contents);

    [Fact]
    public void Absent_ReturnsEmpty()
    {
        Assert.Empty(ResolvedTeamsReader.ReadDynamicTeamFolders(_dir));
    }

    [Fact]
    public void Valid_ReturnsTeamSet()
    {
        Write("""
        {
          "generatedUtc": "2026-06-12T08:00:00Z",
          "teams": [
            { "teamFolderName": "cz-prague-its", "lastConfirmedUtc": "2026-06-12T08:00:00Z", "state": "Active" },
            { "teamFolderName": "de-prod",       "lastConfirmedUtc": "2026-06-12T08:00:00Z", "state": "Grace" }
          ]
        }
        """);

        var set = ResolvedTeamsReader.ReadDynamicTeamFolders(_dir);

        Assert.Equal(2, set.Count);
        Assert.Contains("cz-prague-its", set);
        Assert.Contains("de-prod", set);
        Assert.Contains("CZ-PRAGUE-ITS", set);   // ordinal-ignore-case
    }

    [Fact]
    public void Malformed_ReturnsEmpty()
    {
        Write("{ this is not valid json ");

        Assert.Empty(ResolvedTeamsReader.ReadDynamicTeamFolders(_dir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
