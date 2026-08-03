using NewsCentral.Security;

namespace NewsService.Configuration;

public class ServiceConfiguration
{
    // Company is not configuration — it defines the registry hive path and is the build-time
    // constant SolutionConstants.Company. It is intentionally absent from this POCO.
    public ServiceSection Service { get; set; } = new();
    public RepositorySection Repository { get; set; } = new();
    public AzureBlobSection AzureBlob { get; set; } = new();
    public HmacOptions Hmac { get; set; } = new();
    public EntraOptions Entra { get; set; } = new();
    public DeliverySection Delivery { get; set; } = new();
    public TelemetrySection Telemetry { get; set; } = new();
}

public class TelemetrySection
{
    /// <summary>
    /// False = consume-only deployment: session-*.json files are not forwarded to the repository.
    /// The local retention sweep (TelemetryDefaults.RetentionDays) still runs — NewsViewer always
    /// writes telemetry; NewsService alone decides what becomes of it. Default true.
    /// </summary>
    public bool UploadEnabled { get; set; } = true;
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

    /// <summary>
    /// Named attribute schemes. Each entry resolves at most one dynamic team from the device's
    /// extensionAttributes and carries its own grace window, keyed (Attribute, {name}). The
    /// dictionary name is the instance id. Empty = the attribute source is inactive.
    /// </summary>
    public Dictionary<string, AttributeSchemeOptions> AttributeSchemes { get; set; } = new();

    /// <summary>Group-membership dynamic team (inclusion ∧ ¬exclusion). Inert until G2 wires the resolver.</summary>
    public GroupTeamOptions GroupTeam { get; set; } = new();
}

public class AttributeSchemeOptions
{
    /// <summary>The extensionAttribute whose value selects a rule from Mappings.</summary>
    public string Selector { get; set; } = "extensionAttribute1";

    /// <summary>Selector value → rule (e.g. "FAT" → "extensionAttribute2-extensionAttribute5").</summary>
    public Dictionary<string, string> Mappings { get; set; } = new();
}

public class GroupTeamOptions
{
    public string InclusionGroup { get; set; } = string.Empty;
    public string ExclusionGroup { get; set; } = string.Empty;
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

    /// <summary>
    /// When true, NewsService routes ALL its cloud SDK traffic (Azure Blob + Microsoft Graph) through
    /// WinHttpHandler with UseWinHttpProxy — the machine WinHTTP proxy used by the Intune client and
    /// Windows Update — instead of the default WinINet-based proxy resolution, which is unreliable under
    /// Local System with no user profile loaded. Default false = today's behavior (no transport override).
    /// </summary>
    public bool UseWinHttpProxy { get; set; } = false;
}
