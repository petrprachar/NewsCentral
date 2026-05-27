using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

public static class TeamConfigurationReader
{
    public static string[] GetTeams(IConfiguration configuration) =>
        configuration.GetSection("teams")
            .GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray()!;
}
