using NewsCentral.Security;

namespace NewsService.Configuration;

public class ServiceConfiguration
{
    public string Company { get; set; } = "Contoso";
    public string ApplicationName { get; set; } = "NewsCentral";
    public ServiceSection Service { get; set; } = new();
    public RepositorySection Repository { get; set; } = new();
    public AzureBlobSection AzureBlob { get; set; } = new();
    public HmacOptions Hmac { get; set; } = new();
    public EntraOptions Entra { get; set; } = new();
    public DeliverySection Delivery { get; set; } = new();
}

public class DeliverySection
{
    /// <summary>
    /// Absolute, machine-readable path (SYSTEM-readable in the pre-logon context) to a default
    /// lock-screen image applied when no lock-screen content is active. Empty = no default
    /// (the last-applied lock screen is left in place — sticky).
    /// </summary>
    public string DefaultLockScreenPath { get; set; } = string.Empty;
}

public class EntraOptions
{
    public bool Enabled { get; set; } = false;
    public int GracePeriodMinutes { get; set; } = 240;

    /// <summary>Selector (extensionAttribute1 value) → rule (e.g. "FAT" → "extensionAttribute2-extensionAttribute5").</summary>
    public Dictionary<string, string> Mappings { get; set; } = new();
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
