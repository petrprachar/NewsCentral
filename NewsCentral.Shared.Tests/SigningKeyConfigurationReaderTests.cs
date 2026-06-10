using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class SigningKeyConfigurationReaderTests
{
    [Fact]
    public void GetPublicKeys_ReturnsBothKeys_CurrentFirst()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:team-x:PublicKey"]         = "PUBKEY_CURRENT",
                ["Signing:team-x:PublicKeyPrevious"] = "PUBKEY_PREVIOUS"
            })
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config, "team-x");

        Assert.Equal(2, keys.Length);
        Assert.Equal("PUBKEY_CURRENT",  keys[0]);
        Assert.Equal("PUBKEY_PREVIOUS", keys[1]);
    }

    [Fact]
    public void GetPublicKeys_FiltersEmptyStrings()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:team-x:PublicKey"]         = "",                // empty → omitted
                ["Signing:team-x:PublicKeyPrevious"] = "PUBKEY_PREVIOUS"
            })
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config, "team-x");

        Assert.Single(keys);
        Assert.Equal("PUBKEY_PREVIOUS", keys[0]);
    }

    [Fact]
    public void GetPublicKeys_FiltersNullValues()
    {
        // Config key absent → IConfiguration returns null
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:team-x:PublicKeyPrevious"] = "PUBKEY_PREVIOUS"
                // PublicKey absent → null
            })
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config, "team-x");

        Assert.Single(keys);
        Assert.Equal("PUBKEY_PREVIOUS", keys[0]);
    }

    [Fact]
    public void GetPublicKeys_ReturnsEmpty_WhenNeitherConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var keys = SigningKeyConfigurationReader.GetPublicKeys(config, "team-x");

        Assert.Empty(keys);
    }

    [Fact]
    public void GetPublicKeys_IsScopedToTeam()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Signing:team-a:PublicKey"] = "KEY_A",
                ["Signing:team-b:PublicKey"] = "KEY_B"
            })
            .Build();

        var keysA = SigningKeyConfigurationReader.GetPublicKeys(config, "team-a");
        var keysB = SigningKeyConfigurationReader.GetPublicKeys(config, "team-b");

        Assert.Single(keysA);
        Assert.Equal("KEY_A", keysA[0]);
        Assert.Single(keysB);
        Assert.Equal("KEY_B", keysB[0]);
    }
}
