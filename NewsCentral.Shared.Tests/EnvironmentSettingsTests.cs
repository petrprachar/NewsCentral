using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class EnvironmentSettingsTests
{
    private static EnvironmentSettings FullSettings() => new()
    {
        SchemaVersion = 1,
        DisplayName = "Production",
        Distribution = new DistributionSettings
        {
            Enabled = true,
            Mode = "AzureBlob",
            LocalPath = "C:\\Dist",
            AzureBlob = new AzureBlobSettings
            {
                TenantId = "tenant-1",
                ClientId = "client-1",
                AccountName = "acct1",
                ContainerName = "cont1"
            }
        },
        ModifiedBy = "admin@newscentral.local",
        ModifiedUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc)
    };

    // ── JSON round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void JsonRoundTrip_PreservesAllFields()
    {
        var original = FullSettings();

        var json = JsonSerializer.Serialize(original, EnvironmentSettingsJson.Options);
        var roundTripped = JsonSerializer.Deserialize<EnvironmentSettings>(json, EnvironmentSettingsJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.SchemaVersion, roundTripped!.SchemaVersion);
        Assert.Equal(original.DisplayName, roundTripped.DisplayName);
        Assert.Equal(original.ModifiedBy, roundTripped.ModifiedBy);
        Assert.Equal(original.ModifiedUtc, roundTripped.ModifiedUtc);
        Assert.Equal(original.Distribution.Enabled, roundTripped.Distribution.Enabled);
        Assert.Equal(original.Distribution.Mode, roundTripped.Distribution.Mode);
        Assert.Equal(original.Distribution.LocalPath, roundTripped.Distribution.LocalPath);
        Assert.Equal(original.Distribution.AzureBlob.TenantId, roundTripped.Distribution.AzureBlob.TenantId);
        Assert.Equal(original.Distribution.AzureBlob.ClientId, roundTripped.Distribution.AzureBlob.ClientId);
        Assert.Equal(original.Distribution.AzureBlob.AccountName, roundTripped.Distribution.AzureBlob.AccountName);
        Assert.Equal(original.Distribution.AzureBlob.ContainerName, roundTripped.Distribution.AzureBlob.ContainerName);
    }

    [Fact]
    public void Json_UsesCamelCasePropertyNames()
    {
        var json = JsonSerializer.Serialize(FullSettings(), EnvironmentSettingsJson.Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.True(root.TryGetProperty("displayName", out _));
        Assert.True(root.TryGetProperty("distribution", out var distribution));
        Assert.True(root.TryGetProperty("modifiedBy", out _));
        Assert.True(root.TryGetProperty("modifiedUtc", out _));

        Assert.True(distribution.TryGetProperty("enabled", out _));
        Assert.True(distribution.TryGetProperty("mode", out _));
        Assert.True(distribution.TryGetProperty("localPath", out _));
        Assert.True(distribution.TryGetProperty("azureBlob", out var azureBlob));

        Assert.True(azureBlob.TryGetProperty("tenantId", out _));
        Assert.True(azureBlob.TryGetProperty("clientId", out _));
        Assert.True(azureBlob.TryGetProperty("accountName", out _));
        Assert.True(azureBlob.TryGetProperty("containerName", out _));
    }

    // ── Resolve ───────────────────────────────────────────────────────────────

    private static EnvironmentSettings MachineDefaults() => new()
    {
        SchemaVersion = 1,
        Distribution = new DistributionSettings
        {
            Enabled = true,
            Mode = "Local",
            LocalPath = "C:\\MachineDist"
        }
    };

    [Fact]
    public void Resolve_NullFile_ReturnsMachineDefaults()
    {
        var defaults = MachineDefaults();
        var result = EnvironmentSettingsResolver.Resolve(null, defaults);

        Assert.Equal(EnvironmentSettingsSource.MachineDefaults, result.Source);
        Assert.Same(defaults, result.Settings);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Resolve_ValidFile_ReturnsEnvironmentFile_FileValuesWinOverDefaults()
    {
        var fileSettings = new EnvironmentSettings
        {
            SchemaVersion = 1,
            Distribution = new DistributionSettings
            {
                Enabled = true,
                Mode = "Local",
                LocalPath = "C:\\FileDist"
            }
        };
        var json = JsonSerializer.Serialize(fileSettings, EnvironmentSettingsJson.Options);

        var result = EnvironmentSettingsResolver.Resolve(json, MachineDefaults());

        Assert.Equal(EnvironmentSettingsSource.EnvironmentFile, result.Source);
        Assert.NotNull(result.Settings);
        Assert.Equal("C:\\FileDist", result.Settings!.Distribution.LocalPath);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Resolve_MalformedJson_ReturnsInvalid()
    {
        var result = EnvironmentSettingsResolver.Resolve("{ not valid json", MachineDefaults());

        Assert.Equal(EnvironmentSettingsSource.Invalid, result.Source);
        Assert.Null(result.Settings);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Resolve_SchemaVersion2_ReturnsInvalid_WithNewerVersionMessage()
    {
        const string json = """{ "schemaVersion": 2, "distribution": { "enabled": false, "mode": "Local" } }""";

        var result = EnvironmentSettingsResolver.Resolve(json, MachineDefaults());

        Assert.Equal(EnvironmentSettingsSource.Invalid, result.Source);
        Assert.Null(result.Settings);
        Assert.Contains("newer version", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_AzureModeMissingAccountName_ReturnsInvalid()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "distribution": {
                "enabled": true,
                "mode": "AzureBlob",
                "azureBlob": { "clientId": "client-1", "containerName": "newscentral" }
              }
            }
            """;

        var result = EnvironmentSettingsResolver.Resolve(json, MachineDefaults());

        Assert.Equal(EnvironmentSettingsSource.Invalid, result.Source);
        Assert.Null(result.Settings);
        Assert.Contains("AccountName", result.Error);
    }

    // ── Fingerprint ───────────────────────────────────────────────────────────

    [Fact]
    public void Fingerprint_Disabled_ReturnsNull()
    {
        var settings = new EnvironmentSettings { Distribution = new DistributionSettings { Enabled = false } };

        Assert.Null(EnvironmentSettingsResolver.Fingerprint(settings, "C:\\Data"));
    }

    [Fact]
    public void Fingerprint_LocalWithExplicitPath_UsesNormalizedLowerCasedPath()
    {
        var settings = new EnvironmentSettings
        {
            Distribution = new DistributionSettings { Enabled = true, Mode = "Local", LocalPath = "C:\\Dist" }
        };

        var fingerprint = EnvironmentSettingsResolver.Fingerprint(settings, "C:\\Data");

        Assert.Equal($"local:{Path.GetFullPath("C:\\Dist").ToLowerInvariant()}", fingerprint);
    }

    [Fact]
    public void Fingerprint_LocalWithEmptyPath_FallsBackToDistributionSubfolder()
    {
        var settings = new EnvironmentSettings
        {
            Distribution = new DistributionSettings { Enabled = true, Mode = "Local", LocalPath = null }
        };

        var fingerprint = EnvironmentSettingsResolver.Fingerprint(settings, "C:\\Data");

        var expectedRoot = Path.GetFullPath(Path.Combine("C:\\Data", "_distribution")).ToLowerInvariant();
        Assert.Equal($"local:{expectedRoot}", fingerprint);
    }

    [Fact]
    public void Fingerprint_AzureBlob_UsesAccountAndContainer()
    {
        var settings = new EnvironmentSettings
        {
            Distribution = new DistributionSettings
            {
                Enabled = true,
                Mode = "AzureBlob",
                AzureBlob = new AzureBlobSettings { AccountName = "Acct", ContainerName = "Cont" }
            }
        };

        var fingerprint = EnvironmentSettingsResolver.Fingerprint(settings, "C:\\Data");

        Assert.Equal("blob:acct/cont", fingerprint);
    }

    [Fact]
    public void Fingerprint_DifferentlyCasedPaths_AreEqual()
    {
        var lower = new EnvironmentSettings
        {
            Distribution = new DistributionSettings { Enabled = true, Mode = "Local", LocalPath = "c:\\dist" }
        };
        var upper = new EnvironmentSettings
        {
            Distribution = new DistributionSettings { Enabled = true, Mode = "Local", LocalPath = "C:\\DIST" }
        };

        var a = EnvironmentSettingsResolver.Fingerprint(lower, "C:\\Data");
        var b = EnvironmentSettingsResolver.Fingerprint(upper, "C:\\Data");

        Assert.Equal(a, b);
    }

    // ── FromMachineConfiguration ──────────────────────────────────────────────

    [Fact]
    public void FromMachineConfiguration_MapsEveryField()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:EnableBlobDistribution"] = "true",
                ["Storage:DistributionMode"] = "AzureBlob",
                ["Storage:LocalDistributionPath"] = "C:\\Dist",
                ["Storage:AzureBlobContainerName"] = "mycontainer",
                ["AzureBlob:TenantId"] = "tenant-x",
                ["AzureBlob:ClientId"] = "client-x",
                ["AzureBlob:AccountName"] = "account-x"
            })
            .Build();

        var appConfig = new AppConfiguration(config);
        var settings = EnvironmentSettingsResolver.FromMachineConfiguration(appConfig);

        Assert.Equal(EnvironmentSettingsResolver.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Null(settings.DisplayName);
        Assert.True(settings.Distribution.Enabled);
        Assert.Equal("AzureBlob", settings.Distribution.Mode);
        Assert.Equal("C:\\Dist", settings.Distribution.LocalPath);
        Assert.Equal("tenant-x", settings.Distribution.AzureBlob.TenantId);
        Assert.Equal("client-x", settings.Distribution.AzureBlob.ClientId);
        Assert.Equal("account-x", settings.Distribution.AzureBlob.AccountName);
        Assert.Equal("mycontainer", settings.Distribution.AzureBlob.ContainerName);
        Assert.Null(settings.ModifiedBy);
        Assert.Null(settings.ModifiedUtc);
    }
}
