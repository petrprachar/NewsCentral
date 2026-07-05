using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;

namespace NewsCentral.Shared.Tests;

public sealed class AppConfigurationTests
{
    [Fact]
    public void AdminCredentials_ReadFromInitializationKeys()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Initialization:DefaultAdminUsername"] = "root",
                ["Initialization:DefaultAdminPassword"] = "s3cret"
            })
            .Build();

        var appConfig = new AppConfiguration(config);

        Assert.Equal("root",   appConfig.DefaultAdminUsername);
        Assert.Equal("s3cret", appConfig.DefaultAdminPassword);
    }

    [Fact]
    public void AdminCredentials_FallBackToAdmin_WhenNeitherKeyPresent()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var appConfig = new AppConfiguration(config);

        Assert.Equal("admin", appConfig.DefaultAdminUsername);
        Assert.Equal("admin", appConfig.DefaultAdminPassword);
    }
}
