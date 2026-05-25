namespace NewsService.Configuration;

public class ServiceConfiguration
{
    public string Company { get; set; } = "Contoso";
    public string ApplicationName { get; set; } = "NewsCentral";
    public ServiceSection Service { get; set; } = new();
    public RepositorySection Repository { get; set; } = new();
    public AzureBlobSection AzureBlob { get; set; } = new();
}

public class ServiceSection
{
    public int PollIntervalSeconds { get; set; } = 60;
    public string CacheRootPath { get; set; } = @"C:\ProgramData\NewsCentral";
}

public class RepositorySection
{
    public string StorageMode { get; set; } = "Share";
    public string SharePath { get; set; } = string.Empty;
}

public class AzureBlobSection
{
    public string AuthMode              { get; set; } = "Certificate";
    public string TenantId              { get; set; } = string.Empty;
    public string ClientId              { get; set; } = string.Empty;
    public string CertificateThumbprint { get; set; } = string.Empty;
    public string ClientSecret          { get; set; } = string.Empty;
    public string AccountName           { get; set; } = string.Empty;
    public string ContainerName         { get; set; } = "newscentral";
}
