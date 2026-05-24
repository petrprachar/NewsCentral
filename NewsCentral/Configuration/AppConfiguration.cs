using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

public class AppConfiguration
{
    private readonly IConfiguration _configuration;

    public AppConfiguration(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    // ── Authoring tier ───────────────────────────────────────────────────────
    // DataPath is the root for IStorageService / LocalStorageService.
    // When using Azure Files, mount the share as a drive and set DataPath to it.

    public string DataPath             => _configuration["DataPath"] ?? string.Empty;
    public string DefaultAdminUsername => _configuration["DefaultAdminUsername"] ?? "admin";
    public string DefaultAdminPassword => _configuration["DefaultAdminPassword"] ?? "admin";
    public int    LockExpirationMinutes =>
        int.TryParse(_configuration["LockExpirationMinutes"], out var m) ? m : 15;

    // ── AI services ─────────────────────────────────────────────────────────

    public string ClaudeApiKey => _configuration["AI:ClaudeApiKey"] ?? string.Empty;
    public string ClaudeApiUrl => _configuration["AI:ClaudeApiUrl"] ?? string.Empty;

    // ── Distribution tier (IBlobDistributionService) ─────────────────────────
    //
    // EnableBlobDistribution = false → NullBlobDistributionService (logs only)
    // EnableBlobDistribution = true
    //   DistributionMode = "Local"    → LocalBlobDistributionService
    //   DistributionMode = "AzureBlob"→ AzureBlobDistributionService
    //
    // Recommended progression:
    //   Dev start  : EnableBlobDistribution=false
    //   Dev/test   : EnableBlobDistribution=true, DistributionMode=Local
    //   Production : EnableBlobDistribution=true, DistributionMode=AzureBlob

    public bool   EnableBlobDistribution  =>
        bool.TryParse(_configuration["Storage:EnableBlobDistribution"], out var b) && b;

    public string DistributionMode        =>
        _configuration["Storage:DistributionMode"] ?? "Local";

    /// <summary>
    /// Root folder for LocalBlobDistributionService.
    /// MUST differ from DataPath — the separation is the point.
    /// Falls back to DataPath\_distribution if not set.
    /// </summary>
    public string LocalDistributionPath   =>
        _configuration["Storage:LocalDistributionPath"] ?? string.Empty;

    public string AzureBlobConnectionString =>
        _configuration["Storage:AzureBlobConnectionString"] ?? string.Empty;

    public string AzureBlobContainerName =>
        _configuration["Storage:AzureBlobContainerName"] ?? "newscentral";

    // Uncomment for Phase 2 (Entra ID / Managed Identity):
    // public string AzureBlobAccountName =>
    //     _configuration["Storage:AzureBlobAccountName"] ?? string.Empty;
}
