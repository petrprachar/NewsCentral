using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EnvironmentChangeRulesTests
{
    // ── RequiresRetargetConfirmation ─────────────────────────────────────────

    [Fact]
    public void RequiresRetargetConfirmation_DisabledToEnabled_GivesNone()
    {
        Assert.False(EnvironmentChangeRules.RequiresRetargetConfirmation(
            oldFingerprint: null, oldEnabled: false, newFingerprint: "local:c:\\x"));
    }

    [Fact]
    public void RequiresRetargetConfirmation_SameFingerprint_GivesNone()
    {
        Assert.False(EnvironmentChangeRules.RequiresRetargetConfirmation(
            oldFingerprint: "local:c:\\x", oldEnabled: true, newFingerprint: "local:c:\\x"));
    }

    [Fact]
    public void RequiresRetargetConfirmation_DifferentFingerprint_GivesOne()
    {
        Assert.True(EnvironmentChangeRules.RequiresRetargetConfirmation(
            oldFingerprint: "local:c:\\x", oldEnabled: true, newFingerprint: "local:c:\\y"));
    }

    [Fact]
    public void RequiresRetargetConfirmation_EnabledToDisabled_GivesOne()
    {
        Assert.True(EnvironmentChangeRules.RequiresRetargetConfirmation(
            oldFingerprint: "local:c:\\x", oldEnabled: true, newFingerprint: null));
    }

    [Fact]
    public void RequiresRetargetConfirmation_NullOldFingerprint_GivesNone()
    {
        Assert.False(EnvironmentChangeRules.RequiresRetargetConfirmation(
            oldFingerprint: null, oldEnabled: true, newFingerprint: "local:c:\\y"));
    }

    // ── FindCollisions ────────────────────────────────────────────────────────

    private static SharedDirectoryEntry Entry(
        string dataPath, string? fingerprint, DateTime? deletedUtc = null) =>
        new(dataPath, DisplayName: Path.GetFileName(dataPath.TrimEnd('\\')), AddedBy: "admin@contoso.com",
            ModifiedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), DeletedUtc: deletedUtc,
            DistributionFingerprint: fingerprint);

    [Fact]
    public void FindCollisions_MatchIsFound()
    {
        var entries = new[] { Entry("\\\\srv\\share\\EnvA", "local:c:\\x") };

        var collisions = EnvironmentChangeRules.FindCollisions("C:\\Current", "local:c:\\x", entries);

        Assert.Single(collisions);
    }

    [Theory]
    [InlineData("C:\\Current")]
    [InlineData("c:\\current\\")]
    [InlineData("C:\\CURRENT")]
    public void FindCollisions_ExcludesCurrentPath_CaseAndTrailingSlashInsensitive(string currentPath)
    {
        var entries = new[] { Entry("C:\\Current", "local:c:\\x") };

        var collisions = EnvironmentChangeRules.FindCollisions(currentPath, "local:c:\\x", entries);

        Assert.Empty(collisions);
    }

    [Fact]
    public void FindCollisions_ExcludesTombstones()
    {
        var entries = new[]
        {
            Entry("\\\\srv\\share\\EnvA", "local:c:\\x", deletedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        var collisions = EnvironmentChangeRules.FindCollisions("C:\\Current", "local:c:\\x", entries);

        Assert.Empty(collisions);
    }

    [Fact]
    public void FindCollisions_IgnoresNullFingerprints()
    {
        var entries = new[] { Entry("\\\\srv\\share\\EnvA", fingerprint: null) };

        var collisions = EnvironmentChangeRules.FindCollisions("C:\\Current", "local:c:\\x", entries);

        Assert.Empty(collisions);
    }

    [Fact]
    public void FindCollisions_NullCurrentFingerprint_GivesEmptyImmediately()
    {
        var entries = new[] { Entry("\\\\srv\\share\\EnvA", "local:c:\\x") };

        var collisions = EnvironmentChangeRules.FindCollisions("C:\\Current", currentFingerprint: null, entries);

        Assert.Empty(collisions);
    }
}
