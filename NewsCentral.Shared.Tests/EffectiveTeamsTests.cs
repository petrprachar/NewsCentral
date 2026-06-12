using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EffectiveTeamsTests
{
    [Fact]
    public void Union_DeDuplicatesOverlap()
    {
        var result = EffectiveTeams.Union(
            new[] { "cz-its", "de-prod" },
            new[] { "de-prod", "cz-prague-its" });

        Assert.Equal(new[] { "cz-its", "de-prod", "cz-prague-its" }, result);
    }

    [Fact]
    public void Union_IsCaseInsensitive()
    {
        var result = EffectiveTeams.Union(
            new[] { "cz-its" },
            new[] { "CZ-ITS" });

        Assert.Single(result);
        Assert.Equal("cz-its", result[0]);   // first occurrence wins
    }

    [Fact]
    public void Union_SkipsNullAndWhitespace()
    {
        var result = EffectiveTeams.Union(
            new[] { "cz-its", "  ", "" },
            new[] { "de-prod" });

        Assert.Equal(new[] { "cz-its", "de-prod" }, result);
    }
}
