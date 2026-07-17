using Azure.Identity;
using NewsCentral.Configuration;
using NewsService.Configuration;
using NewsService.Services;

namespace NewsService.Tests;

/// <summary>
/// Hermetic coverage of the ClientSecretEnv auth mode: the secret is injected through the
/// readEnv seam — no test reads or writes a real machine environment variable. Empty and
/// unset must BOTH fail closed (throw, no fallback to the registry ClientSecret).
/// </summary>
public sealed class AzureCredentialFactoryTests
{
    private static AzureBlobSection EnvModeConfig() => new()
    {
        AuthMode = "ClientSecretEnv",
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222"
    };

    [Fact]
    public void ClientSecretEnv_ValuePresent_ReadsNamedVariableAndBuildsCredential()
    {
        string? requestedName = null;

        var credential = AzureCredentialFactory.Create(EnvModeConfig(), transport: null,
            readEnv: name => { requestedName = name; return "injected-secret"; });

        Assert.Equal(SolutionConstants.NewsServiceAzureClientSecretEnvVar, requestedName);
        Assert.IsType<ClientSecretCredential>(credential);
    }

    [Fact]
    public void ClientSecretEnv_EmptyValue_FailsClosed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureCredentialFactory.Create(EnvModeConfig(), transport: null, readEnv: _ => ""));

        Assert.Contains("NEWSSERVICE_AZURE_CLIENTSECRET", ex.Message);
    }

    [Fact]
    public void ClientSecretEnv_Unset_FailsClosed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AzureCredentialFactory.Create(EnvModeConfig(), transport: null, readEnv: _ => null));

        Assert.Contains("NEWSSERVICE_AZURE_CLIENTSECRET", ex.Message);
    }

    [Fact]
    public void ClientSecret_RegistryMode_NeverConsultsEnvironment()
    {
        var cfg = new AzureBlobSection
        {
            AuthMode = "ClientSecret",
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ClientSecret = "registry-secret"
        };
        var envRead = false;

        var credential = AzureCredentialFactory.Create(cfg, transport: null,
            readEnv: _ => { envRead = true; return "should-not-be-used"; });

        Assert.False(envRead);
        Assert.IsType<ClientSecretCredential>(credential);
    }
}
