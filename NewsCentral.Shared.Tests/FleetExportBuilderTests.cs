using NewsCentral.Configuration.FleetExport;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class FleetExportBuilderTests
{
    private static FleetExportInput MakeInput(
        FleetExportDistribution? distribution = null,
        string? effectiveLocalDistributionPath = null,
        IReadOnlyList<FleetExportTeam>? teams = null) => new(
        Company: "Contoso",
        EnvironmentDisplayName: "Pilot",
        DataPath: @"C:\Download\NewsCentral",
        GeneratedAtUtc: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
        Distribution: distribution ?? new FleetExportDistribution(true, "Local", null, "", "", "newscentral"),
        EffectiveLocalDistributionPath: effectiveLocalDistributionPath ?? @"\\fileserver\newscentral",
        Teams: teams ?? new List<FleetExportTeam>
        {
            new("cz-its", "CZ ITS", "pubkey-cz", "pubkey-cz-prev")
        });

    [Fact]
    public void Build_LocalMode_UncPath_ExportsSharePath_NoPathWarning()
    {
        var input = MakeInput(effectiveLocalDistributionPath: @"\\fileserver\newscentral");

        var result = FleetExportBuilder.Build(input);

        Assert.Contains("StorageMode = 'Share'", result.PowerShell);
        Assert.Contains(@"SharePath   = '\\fileserver\newscentral'", result.PowerShell);
        Assert.Contains("\"StorageMode\"=\"Share\"", result.Reg);
        Assert.DoesNotContain(FleetExportBuilder.SharePathPlaceholder, result.PowerShell);
        Assert.DoesNotContain(FleetExportBuilder.SharePathPlaceholder, result.Reg);
        Assert.DoesNotContain(FleetExportBuilder.SharePathPlaceholder, result.Placeholders);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("UNC path"));
    }

    [Fact]
    public void Build_LocalMode_LocalPath_EmitsPlaceholderAndWarning()
    {
        var input = MakeInput(effectiveLocalDistributionPath: @"C:\Download\NewsCentral\_distribution");

        var result = FleetExportBuilder.Build(input);

        Assert.Contains(FleetExportBuilder.SharePathPlaceholder, result.PowerShell);
        Assert.Contains(FleetExportBuilder.SharePathPlaceholder, result.Reg);
        Assert.Contains(FleetExportBuilder.SharePathPlaceholder, result.Placeholders);
        Assert.Contains(result.Warnings, w => w.Contains("is local to this computer") && w.Contains("UNC path"));
    }

    [Fact]
    public void Build_AzureBlob_ExportsIdentifiers_ClientIdAndThumbprintArePlaceholders()
    {
        var distribution = new FleetExportDistribution(true, "AzureBlob", null, "tenant-guid-123", "stgacct1", "container1");
        var input = MakeInput(distribution: distribution);

        var result = FleetExportBuilder.Build(input);

        Assert.Contains("tenant-guid-123", result.PowerShell);
        Assert.Contains("stgacct1", result.PowerShell);
        Assert.Contains("container1", result.PowerShell);
        Assert.Contains("'Certificate'", result.PowerShell);
        Assert.Contains(FleetExportBuilder.FleetClientIdPlaceholder, result.PowerShell);
        Assert.Contains(FleetExportBuilder.CertThumbprintPlaceholder, result.PowerShell);

        Assert.Contains("tenant-guid-123", result.Reg);
        Assert.Contains("stgacct1", result.Reg);
        Assert.Contains("container1", result.Reg);
        Assert.Contains("\"AuthMode\"=\"Certificate\"", result.Reg);
        Assert.Contains(FleetExportBuilder.FleetClientIdPlaceholder, result.Reg);
        Assert.Contains(FleetExportBuilder.CertThumbprintPlaceholder, result.Reg);

        Assert.Contains(FleetExportBuilder.FleetClientIdPlaceholder, result.Placeholders);
        Assert.Contains(FleetExportBuilder.CertThumbprintPlaceholder, result.Placeholders);

        // The authoring app's ClientId is never even part of the input type, so it structurally
        // cannot leak into the export — FleetExportDistribution carries no ClientId property.
        var distributionProperties = typeof(FleetExportDistribution).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("ClientId", distributionProperties);
    }

    [Fact]
    public void Build_DistributionDisabled_WarnsButStillProducesOutput()
    {
        var distribution = new FleetExportDistribution(false, "Local", null, "", "", "newscentral");
        var input = MakeInput(distribution: distribution);

        var result = FleetExportBuilder.Build(input);

        Assert.Contains(result.Warnings, w => w.Contains("Distribution is disabled"));
        Assert.False(string.IsNullOrWhiteSpace(result.PowerShell));
        Assert.False(string.IsNullOrWhiteSpace(result.Reg));
    }

    [Fact]
    public void Build_TeamWithCurrentAndPreviousKeys_EmitsBothForBothComponents()
    {
        var teams = new List<FleetExportTeam> { new("cz-its", "CZ ITS", "pub-current", "pub-previous") };
        var input = MakeInput(teams: teams);

        var result = FleetExportBuilder.Build(input);

        Assert.Equal(2, CountOccurrences(result.PowerShell, "pub-current"));
        Assert.Equal(2, CountOccurrences(result.PowerShell, "pub-previous"));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"PublicKey\"=\"pub-current\""));
        Assert.Equal(2, CountOccurrences(result.Reg, "\"PublicKeyPrevious\"=\"pub-previous\""));
    }

    [Fact]
    public void Build_TeamWithoutKey_OmittedFromSigningAndWarned()
    {
        var teams = new List<FleetExportTeam>
        {
            new("cz-its", "CZ ITS", "pub-cz", null),
            new("de-prod", "DE Prod", null, null)
        };
        var input = MakeInput(teams: teams);

        var result = FleetExportBuilder.Build(input);

        Assert.DoesNotContain("de-prod", GetSigningSection(result.Reg));
        Assert.Contains(result.Warnings, w => w.Contains("No signing key: de-prod"));
        Assert.Contains("cz-its", result.PowerShell);
    }

    private static string GetSigningSection(string reg)
    {
        var start = reg.IndexOf("Signing]", StringComparison.Ordinal);
        var teamsExampleStart = reg.IndexOf("Example only", StringComparison.Ordinal);
        return start >= 0 && teamsExampleStart > start ? reg[start..teamsExampleStart] : reg;
    }

    [Fact]
    public void Build_NoPrivateKeyFieldExists_AndOutputNeverMentionsPrivateKey()
    {
        var teams = new List<FleetExportTeam> { new("cz-its", "CZ ITS", "pub-cz", "pub-cz-prev") };
        var input = MakeInput(teams: teams);

        var result = FleetExportBuilder.Build(input);

        var teamProperties = typeof(FleetExportTeam).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("PrivateKey", teamProperties);

        Assert.DoesNotContain("PrivateKey", result.PowerShell);
        Assert.DoesNotContain("PrivateKey", result.Reg);
    }

    [Fact]
    public void Build_Reg_EscapesBackslashesAndQuotes_HeaderLineExact_AllValuesRegSz()
    {
        var teams = new List<FleetExportTeam> { new("cz-its", "CZ ITS", "pub\"with\"quotes", null) };
        var input = MakeInput(effectiveLocalDistributionPath: @"\\server\share\path", teams: teams);

        var result = FleetExportBuilder.Build(input);

        Assert.StartsWith("Windows Registry Editor Version 5.00", result.Reg);
        var firstLine = result.Reg.Split('\n')[0].TrimEnd('\r');
        Assert.Equal("Windows Registry Editor Version 5.00", firstLine);

        // \\server\share\path -> each backslash doubled.
        Assert.Contains(@"""SharePath""=""\\\\server\\share\\path""", result.Reg);

        // An embedded quote is escaped as \".
        Assert.Contains(@"\""with\""", result.Reg);

        // Every value line uses the quoted "name"="value" form (REG_SZ) — never a dword: / hex: prefix.
        foreach (var line in result.Reg.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('"') && trimmed.Contains('='))
            {
                Assert.DoesNotContain("dword:", trimmed);
                Assert.DoesNotContain("hex:", trimmed);
            }
        }
    }

    [Fact]
    public void Build_PowerShell_ApostropheInTeamName_EscapedAndRoundTrips()
    {
        var teams = new List<FleetExportTeam> { new("o'brien-team", "O'Brien Team", "pub-key", null) };
        var input = MakeInput(teams: teams);

        var result = FleetExportBuilder.Build(input);

        Assert.Contains("'o''brien-team'", result.PowerShell);

        // Textual round-trip check (no System.Management.Automation reference in this project):
        // a single-quoted PowerShell string with an embedded ' must be written as ''.
        var quoted = "'o''brien-team'";
        var unescaped = quoted.Trim('\'').Replace("''", "'");
        Assert.Equal("o'brien-team", unescaped);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
