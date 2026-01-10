using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

public class AppConfiguration
{
    private readonly IConfiguration _configuration;

    public AppConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string DataPath => _configuration["AppSettings:DataPath"] ?? string.Empty;
    public string DefaultAdminUsername => _configuration["AppSettings:DefaultAdminUsername"] ?? "admin";
    public string DefaultAdminPassword => _configuration["AppSettings:DefaultAdminPassword"] ?? "admin";
    public int LockExpirationMinutes => int.Parse(_configuration["AppSettings:LockExpirationMinutes"] ?? "15");

    public string ClaudeApiKey => _configuration["AI:ClaudeApiKey"] ?? string.Empty;
    public string ClaudeApiUrl => _configuration["AI:ClaudeApiUrl"] ?? string.Empty;

    public string AzureBlobConnectionString => _configuration["Storage:AzureBlobConnectionString"] ?? string.Empty;
    public string DefaultStorageType => _configuration["Storage:DefaultStorageType"] ?? "NetworkShare";
}
