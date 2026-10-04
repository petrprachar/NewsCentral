using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EnvironmentCatalogTests
{
    private static IConfiguration RegistryStyle(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Read_EmptyRegistry_GivesEmptyCatalog_AllowUserEnvironmentsTrue()
    {
        var registry = RegistryStyle(new());
        var catalog = EnvironmentCatalogReader.Read(registry);

        Assert.Empty(catalog.Entries);
        Assert.True(catalog.AllowUserEnvironments);
        Assert.Empty(catalog.Warnings);
    }

    [Fact]
    public void Read_TwoNamedEntries_ParsedWithAllFields()
    {
        var registry = RegistryStyle(new()
        {
            ["Environments:Dev:DataPath"] = "C:\\Download\\NewsCentral",
            ["Environments:Dev:DisplayName"] = "Dev box",
            ["Environments:Dev:Storage:EnableBlobDistribution"] = "true",
            ["Environments:Dev:Storage:DistributionMode"] = "Local",
            ["Environments:Dev:Storage:LocalDistributionPath"] = "C:\\Download\\NewsCentralDistPolicy",
            ["Environments:Dev:Storage:AzureBlobContainerName"] = "newscentral-dev",
            ["Environments:Dev:AzureBlob:TenantId"] = "tenant-dev",
            ["Environments:Dev:AzureBlob:ClientId"] = "client-dev",
            ["Environments:Dev:AzureBlob:AccountName"] = "acct-dev",

            ["Environments:Other:DataPath"] = "C:\\Download\\NewsCentralB"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        Assert.Equal(2, catalog.Entries.Count);
        var dev = catalog.Entries.Single(e => e.Name == "Dev");
        Assert.Equal("C:\\Download\\NewsCentral", dev.DataPath);
        Assert.Equal("Dev box", dev.DisplayName);
        Assert.True(dev.EnableBlobDistribution);
        Assert.Equal("Local", dev.DistributionMode);
        Assert.Equal("C:\\Download\\NewsCentralDistPolicy", dev.LocalDistributionPath);
        Assert.Equal("newscentral-dev", dev.AzureBlobContainerName);
        Assert.Equal("tenant-dev", dev.AzureTenantId);
        Assert.Equal("client-dev", dev.AzureClientId);
        Assert.Equal("acct-dev", dev.AzureAccountName);
        Assert.False(dev.IsImplicitDefault);

        var other = catalog.Entries.Single(e => e.Name == "Other");
        Assert.Equal("C:\\Download\\NewsCentralB", other.DataPath);
        Assert.Null(other.DisplayName);
        Assert.Null(other.EnableBlobDistribution);
    }

    [Fact]
    public void Read_EntryMissingDataPath_SkippedWithWarning()
    {
        var registry = RegistryStyle(new()
        {
            ["Environments:NoPath:DisplayName"] = "Missing DataPath"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        Assert.Empty(catalog.Entries);
        Assert.Contains(catalog.Warnings, w => w.Contains("NoPath") && w.Contains("DataPath"));
    }

    [Fact]
    public void Read_FlatDataPath_GivesImplicitDefault()
    {
        var registry = RegistryStyle(new()
        {
            ["DataPath"] = "C:\\Download\\NewsCentral",
            ["Storage:EnableBlobDistribution"] = "true",
            ["Storage:DistributionMode"] = "Local",
            ["Storage:LocalDistributionPath"] = "C:\\Download\\NewsCentralDistPolicy",
            ["AzureBlob:AccountName"] = "acct-flat"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("Default", entry.Name);
        Assert.True(entry.IsImplicitDefault);
        Assert.Equal("C:\\Download\\NewsCentral", entry.DataPath);
        Assert.True(entry.EnableBlobDistribution);
        Assert.Equal("Local", entry.DistributionMode);
        Assert.Equal("C:\\Download\\NewsCentralDistPolicy", entry.LocalDistributionPath);
        Assert.Equal("acct-flat", entry.AzureAccountName);
    }

    [Fact]
    public void Read_ExplicitDefaultBeatsImplicit_WithWarning()
    {
        var registry = RegistryStyle(new()
        {
            ["DataPath"] = "C:\\Download\\Implicit",
            ["Environments:Default:DataPath"] = "C:\\Download\\Explicit"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("C:\\Download\\Explicit", entry.DataPath);
        Assert.False(entry.IsImplicitDefault);
        Assert.Contains(catalog.Warnings, w => w.Contains("implicit") && w.Contains("explicit"));
    }

    [Fact]
    public void Read_DuplicateDataPaths_DifferingCaseAndTrailingSlash_FirstWinsWithWarning()
    {
        var registry = RegistryStyle(new()
        {
            ["Environments:Alpha:DataPath"] = "C:\\Download\\NewsCentral",
            ["Environments:Beta:DataPath"] = "C:\\DOWNLOAD\\NewsCentral\\"
        });

        var catalog = EnvironmentCatalogReader.Read(registry);

        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("Alpha", entry.Name); // "Alpha" < "Beta" in name order
        Assert.Contains(catalog.Warnings, w => w.Contains("Beta"));
    }

    [Fact]
    public void Read_AllowUserEnvironmentsFalse_GivesFalse()
    {
        var registry = RegistryStyle(new() { ["AllowUserEnvironments"] = "False" });

        var catalog = EnvironmentCatalogReader.Read(registry);

        Assert.False(catalog.AllowUserEnvironments);
    }

    [Fact]
    public void FindByDataPath_IsCaseAndTrailingSlashInsensitive()
    {
        var registry = RegistryStyle(new()
        {
            ["Environments:Dev:DataPath"] = "C:\\Download\\NewsCentral"
        });
        var catalog = EnvironmentCatalogReader.Read(registry);

        var found = catalog.FindByDataPath("c:\\download\\newscentral\\");

        Assert.NotNull(found);
        Assert.Equal("Dev", found!.Name);
    }

    [Fact]
    public void FindByDataPath_NoMatch_ReturnsNull()
    {
        var catalog = EnvironmentCatalog.Empty;

        Assert.Null(catalog.FindByDataPath("C:\\Anything"));
    }

    // ── ApplyPolicy ───────────────────────────────────────────────────────────

    private static EnvironmentSettings BaseSettings() => new()
    {
        SchemaVersion = 1,
        DisplayName = "Original",
        Distribution = new DistributionSettings
        {
            Enabled = false,
            Mode = "Local",
            LocalPath = "C:\\OriginalDist",
            AzureBlob = new AzureBlobSettings
            {
                TenantId = "orig-tenant",
                ClientId = "orig-client",
                AccountName = "orig-account",
                ContainerName = "orig-container"
            }
        }
    };

    [Fact]
    public void ApplyPolicy_OnlyOverridesSetFields_AndListsExactlyThose()
    {
        var baseSettings = BaseSettings();
        var policy = new PolicyEnvironment(
            Name: "Dev",
            DataPath: "C:\\Download\\NewsCentral",
            DisplayName: "Dev box",
            EnableBlobDistribution: null,
            DistributionMode: null,
            LocalDistributionPath: "C:\\Download\\NewsCentralDistPolicy",
            AzureBlobContainerName: null,
            AzureTenantId: null,
            AzureClientId: null,
            AzureAccountName: null,
            IsImplicitDefault: false);

        var (result, fields) = EnvironmentSettingsResolver.ApplyPolicy(baseSettings, policy);

        Assert.Equal("Dev box", result.DisplayName);
        Assert.Equal("C:\\Download\\NewsCentralDistPolicy", result.Distribution.LocalPath);
        // Untouched fields keep their base values.
        Assert.False(result.Distribution.Enabled);
        Assert.Equal("Local", result.Distribution.Mode);
        Assert.Equal("orig-account", result.Distribution.AzureBlob.AccountName);

        Assert.Equal(new[] { "displayName", "distribution.localPath" }, fields);
    }

    [Fact]
    public void ApplyPolicy_NeverMutatesInput()
    {
        var baseSettings = BaseSettings();
        var policy = new PolicyEnvironment(
            "Dev", "C:\\Download\\NewsCentral", "Dev box",
            true, "AzureBlob", "C:\\New", "newcontainer", "new-tenant", "new-client", "new-account",
            IsImplicitDefault: false);

        EnvironmentSettingsResolver.ApplyPolicy(baseSettings, policy);

        Assert.Equal("Original", baseSettings.DisplayName);
        Assert.False(baseSettings.Distribution.Enabled);
        Assert.Equal("Local", baseSettings.Distribution.Mode);
        Assert.Equal("C:\\OriginalDist", baseSettings.Distribution.LocalPath);
        Assert.Equal("orig-account", baseSettings.Distribution.AzureBlob.AccountName);
    }

    [Fact]
    public void ApplyPolicy_AllFieldsSet_OverridesEveryOne()
    {
        var baseSettings = BaseSettings();
        var policy = new PolicyEnvironment(
            "Dev", "C:\\Download\\NewsCentral", "Dev box",
            true, "AzureBlob", "C:\\New", "newcontainer", "new-tenant", "new-client", "new-account",
            IsImplicitDefault: false);

        var (result, fields) = EnvironmentSettingsResolver.ApplyPolicy(baseSettings, policy);

        Assert.Equal("Dev box", result.DisplayName);
        Assert.True(result.Distribution.Enabled);
        Assert.Equal("AzureBlob", result.Distribution.Mode);
        Assert.Equal("C:\\New", result.Distribution.LocalPath);
        Assert.Equal("newcontainer", result.Distribution.AzureBlob.ContainerName);
        Assert.Equal("new-tenant", result.Distribution.AzureBlob.TenantId);
        Assert.Equal("new-client", result.Distribution.AzureBlob.ClientId);
        Assert.Equal("new-account", result.Distribution.AzureBlob.AccountName);

        Assert.Equal(8, fields.Count);
    }
}
