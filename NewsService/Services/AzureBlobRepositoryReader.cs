using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using NewsService.Configuration;
using System.Security.Cryptography.X509Certificates;

namespace NewsService.Services;

/// <summary>
/// Reads content from Azure Blob Storage.
/// Authentication is selected by AzureBlob:AuthMode:
///   "Certificate"  — ClientCertificateCredential, cert loaded from LocalMachine\My by thumbprint.
///   "ClientSecret" — ClientSecretCredential.
/// Set StorageMode=Share in registry for development and on-premises use.
/// </summary>
public sealed class AzureBlobRepositoryReader : IRepositoryReader
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AzureBlobRepositoryReader> _logger;

    public bool IsAvailable => true;
    public string SyncSource => "Azure";

    public AzureBlobRepositoryReader(
        AzureBlobSection config,
        ILogger<AzureBlobRepositoryReader> logger)
    {
        _logger = logger;

        if (string.IsNullOrWhiteSpace(config.AccountName))
            throw new InvalidOperationException("AzureBlob:AccountName must be set.");
        if (string.IsNullOrWhiteSpace(config.ContainerName))
            throw new InvalidOperationException("AzureBlob:ContainerName must be set.");

        var credential  = BuildCredential(config);
        var serviceUri  = new Uri($"https://{config.AccountName}.blob.core.windows.net");
        var service     = new BlobServiceClient(serviceUri, credential);
        _container      = service.GetBlobContainerClient(config.ContainerName);

        _logger.LogInformation(
            "Azure Blob reader initialised — account={Account}, container={Container}, authMode={Mode}",
            config.AccountName, config.ContainerName, config.AuthMode);
    }

    public async Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var blob     = _container.GetBlobClient(relativePath);
            var response = await blob.DownloadContentAsync(ct);
            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Blob not found: {Path}", relativePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read blob: {Path}", relativePath);
            return null;
        }
    }

    public async Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default)
    {
        try
        {
            var blob     = _container.GetBlobClient(relativePath);
            var response = await blob.DownloadContentAsync(ct);
            return response.Value.Content.ToArray();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Blob not found: {Path}", relativePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read blob bytes: {Path}", relativePath);
            return null;
        }
    }

    // ── Credential factory ───────────────────────────────────────────────────

    private static TokenCredential BuildCredential(AzureBlobSection cfg)
    {
        if (cfg.AuthMode.Equals("ClientSecret", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(cfg.TenantId) ||
                string.IsNullOrWhiteSpace(cfg.ClientId) ||
                string.IsNullOrWhiteSpace(cfg.ClientSecret))
                throw new InvalidOperationException(
                    "AzureBlob:AuthMode=ClientSecret requires TenantId, ClientId, and ClientSecret.");

            return new ClientSecretCredential(cfg.TenantId, cfg.ClientId, cfg.ClientSecret);
        }

        // Default: Certificate
        if (string.IsNullOrWhiteSpace(cfg.TenantId) ||
            string.IsNullOrWhiteSpace(cfg.ClientId) ||
            string.IsNullOrWhiteSpace(cfg.CertificateThumbprint))
            throw new InvalidOperationException(
                "AzureBlob:AuthMode=Certificate requires TenantId, ClientId, and CertificateThumbprint.");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Find(
            X509FindType.FindByThumbprint, cfg.CertificateThumbprint, validOnly: false);

        if (certs.Count == 0)
            throw new InvalidOperationException(
                $"Certificate with thumbprint '{cfg.CertificateThumbprint}' not found in LocalMachine\\My.");

        return new ClientCertificateCredential(cfg.TenantId, cfg.ClientId, certs[0]);
    }
}
