using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

public class AppConfiguration
{
    private readonly IConfiguration _configuration;

    public AppConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // Remove "AppSettings:" prefix - config keys are at root level
    public string DataPath => _configuration["DataPath"] ?? string.Empty;
    public string DefaultAdminUsername => _configuration["DefaultAdminUsername"] ?? "admin";
    public string DefaultAdminPassword => _configuration["DefaultAdminPassword"] ?? "admin";
    public int LockExpirationMinutes => int.Parse(_configuration["LockExpirationMinutes"] ?? "15");

    public string ClaudeApiKey => _configuration["AI:ClaudeApiKey"] ?? string.Empty;
    public string ClaudeApiUrl => _configuration["AI:ClaudeApiUrl"] ?? string.Empty;
    public string AzureBlobConnectionString => _configuration["Storage:AzureBlobConnectionString"] ?? string.Empty;
    public string DefaultStorageType => _configuration["Storage:DefaultStorageType"] ?? "NetworkShare";
}