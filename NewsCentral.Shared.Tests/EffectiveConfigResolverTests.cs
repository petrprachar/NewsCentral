using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class EffectiveConfigResolverTests
{
    // ── ParseExeDirectory (spec §4.2) ─────────────────────────────────────────

    [Fact]
    public void ParseExeDirectory_QuotedPathWithArgs()
    {
        var dir = EffectiveConfigResolver.ParseExeDirectory(
            "\"C:\\Program Files\\NewsService\\NewsService.exe\" --run");
        Assert.Equal(@"C:\Program Files\NewsService", dir);
    }

    [Fact]
    public void ParseExeDirectory_UnquotedPathWithArgs()
    {
        var dir = EffectiveConfigResolver.ParseExeDirectory(
            @"C:\Apps\NewsViewer\NewsViewer.exe /silent");
        Assert.Equal(@"C:\Apps\NewsViewer", dir);
    }

    [Fact]
    public void ParseExeDirectory_QuotedPathWithSpaces()
    {
        var dir = EffectiveConfigResolver.ParseExeDirectory(
            "\"C:\\Program Files\\My App\\app.exe\"");
        Assert.Equal(@"C:\Program Files\My App", dir);
    }

    [Fact]
    public void ParseExeDirectory_Garbage_ReturnsNull()
    {
        Assert.Null(EffectiveConfigResolver.ParseExeDirectory("not a path at all"));
        Assert.Null(EffectiveConfigResolver.ParseExeDirectory(""));
        Assert.Null(EffectiveConfigResolver.ParseExeDirectory("   "));
    }

    // ── Project (spec §8 step 5) — pure, two in-memory layers ─────────────────

    private static ConfigKeyDescriptor Desc(string key, bool secret = false) => new()
    {
        CanonicalKey = key,
        DisplayName = key,
        RegistrySubkeyPath = key.Replace(':', '\\'),
        RegistryType = RegistryValueType.RegSz,
        Default = "",
        ControlKind = secret ? ControlKind.Redacted : ControlKind.Text,
        IsSecret = secret,
        OverridableState = OverridableState.Overridable,
        ValueHint = ""
    };

    [Fact]
    public void Project_ResolvesPerKeyValues_AndStructuralBlocks()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "Test",
            HasTeamsHive = true,
            HasSigningHive = true,
            HasEntraMappings = true,
            Keys = new[]
            {
                Desc("Company"),
                Desc("Service:PollIntervalSeconds"),
                Desc("AzureBlob:ClientSecret", secret: true)   // absent in both layers
            }
        };

        var app = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Company"] = "Contoso",
            ["Service:PollIntervalSeconds"] = "60"
        }).Build();

        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Company"] = "Contoso",
            ["Service:PollIntervalSeconds"] = "90",
            ["teams:0"] = "alpha",
            ["teams:1"] = "beta",
            ["Signing:RequireSignedIndex"] = "true",
            ["Signing:team-x:PublicKey"] = "PKX",
            ["Signing:team-x:PublicKeyPrevious"] = "PKXP",
            ["Signing:team-y:PublicKey"] = "PKY",
            ["Entra:Mappings:FAT"] = "rule-fat",
            ["Entra:Mappings:VDE"] = "rule-vde"
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, app, reg);

        // Per-key value resolution
        var company = result.Rows.Single(r => r.Descriptor.CanonicalKey == "Company");
        Assert.Equal("Contoso", company.AppSettingsValue);
        Assert.Equal("Contoso", company.RegistryValue);

        var poll = result.Rows.Single(r => r.Descriptor.CanonicalKey == "Service:PollIntervalSeconds");
        Assert.Equal("60", poll.AppSettingsValue);
        Assert.Equal("90", poll.RegistryValue);

        // Absent in both layers → null
        var secret = result.Rows.Single(r => r.Descriptor.CanonicalKey == "AzureBlob:ClientSecret");
        Assert.Null(secret.AppSettingsValue);
        Assert.Null(secret.RegistryValue);

        // teams
        Assert.Equal(new[] { "alpha", "beta" }, result.Teams);

        // Signing — excludes RequireSignedIndex; per-team PublicKey/Previous
        Assert.Equal(2, result.Signing.Count);
        var tx = result.Signing.Single(s => s.Team == "team-x");
        Assert.Equal("PKX", tx.PublicKey);
        Assert.Equal("PKXP", tx.PublicKeyPrevious);
        var ty = result.Signing.Single(s => s.Team == "team-y");
        Assert.Equal("PKY", ty.PublicKey);
        Assert.Null(ty.PublicKeyPrevious);
        Assert.DoesNotContain(result.Signing, s => s.Team == "RequireSignedIndex");

        // Entra mappings
        Assert.Equal(2, result.EntraMappings.Count);
        Assert.Equal("rule-fat", result.EntraMappings.Single(m => m.Selector == "FAT").Rule);
        Assert.Equal("rule-vde", result.EntraMappings.Single(m => m.Selector == "VDE").Rule);
    }

    [Fact]
    public void Project_OmitsStructuralBlocks_WhenFlagsFalse()
    {
        var manifest = new ComponentManifest
        {
            ComponentName = "NoStructure",
            HasTeamsHive = false,
            HasSigningHive = false,
            HasEntraMappings = false,
            Keys = new[] { Desc("Company") }
        };

        var empty = new ConfigurationBuilder().Build();
        var reg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["teams:0"] = "alpha",
            ["Signing:team-x:PublicKey"] = "PKX",
            ["Entra:Mappings:FAT"] = "rule-fat"
        }).Build();

        var result = EffectiveConfigResolver.Project(manifest, empty, reg);

        Assert.Empty(result.Teams);
        Assert.Empty(result.Signing);
        Assert.Empty(result.EntraMappings);
    }

    // ── Resolve install-dir override (spec §4) ────────────────────────────────

    [Fact]
    public void Resolve_WithValidOverride_ReadsThatDirsAppsettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncr-override-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                "{ \"Company\": \"TestCo\", \"Service\": { \"PollIntervalSeconds\": \"77\" } }");

            var result = EffectiveConfigResolver.Resolve(ConfigManifests.NewsService, dir);

            Assert.Equal(dir, result.InstallDir);
            Assert.Equal("manual override (session)", result.DiscoverySource);
            Assert.Equal(Path.Combine(dir, "appsettings.json"), result.AppSettingsPath);
            Assert.True(result.AppSettingsExists);
            Assert.Equal(LayerStatus.Found, result.AppSettingsStatus);

            // Values are read from THAT directory's appsettings.json.
            Assert.Equal("TestCo",
                result.Rows.Single(r => r.Descriptor.CanonicalKey == "Company").AppSettingsValue);
            Assert.Equal("77",
                result.Rows.Single(r => r.Descriptor.CanonicalKey == "Service:PollIntervalSeconds").AppSettingsValue);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Resolve_WithEmptyOrNullOverride_FallsBackToDiscovery()
    {
        // NewsCentral discovery is deterministic: AppContext.BaseDirectory.
        var viaNull  = EffectiveConfigResolver.Resolve(ConfigManifests.NewsCentral, null);
        var viaEmpty = EffectiveConfigResolver.Resolve(ConfigManifests.NewsCentral, "   ");

        Assert.Equal("AppContext.BaseDirectory", viaNull.DiscoverySource);
        Assert.Equal("AppContext.BaseDirectory", viaEmpty.DiscoverySource);
        Assert.Equal(AppContext.BaseDirectory, viaNull.InstallDir);
        Assert.Equal(AppContext.BaseDirectory, viaEmpty.InstallDir);
    }

    [Fact]
    public void Resolve_WithNonExistentOverride_ResolvesEmpty_WithoutThrowing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ncr-missing-" + Guid.NewGuid().ToString("N"));

        var result = EffectiveConfigResolver.Resolve(ConfigManifests.NewsService, missing);

        Assert.Equal(missing, result.InstallDir);
        Assert.False(result.InstallDirExists);
        Assert.Equal(Path.Combine(missing, "appsettings.json"), result.AppSettingsPath);
        Assert.False(result.AppSettingsExists);
        Assert.Equal(LayerStatus.NotFound, result.AppSettingsStatus);

        // Rows still present (manifest surface), all appsettings values absent; no structural data.
        Assert.NotEmpty(result.Rows);
        Assert.All(result.Rows, row => Assert.Null(row.AppSettingsValue));
        Assert.Empty(result.Teams);
    }
}
