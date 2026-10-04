using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Distribution service backed by Azure Blob Storage.
/// Authentication: InteractiveBrowserCredential (MSAL) — user signs in on first upload,
/// subsequent calls reuse the persisted token cache ("NewsCentral").
/// Active when Distribution.Enabled and Distribution.Mode == "AzureBlob" in the effective
/// environment settings (DistributionServiceRouter selects and constructs this).
/// </summary>
public class AzureBlobDistributionService : IBlobDistributionService
{
    private readonly BlobContainerClient _container;
    private volatile bool _containerEnsured;
    private const string Tag = "[BlobDist AZURE]";

    public AzureBlobDistributionService(AzureBlobSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AccountName))
            throw new InvalidOperationException(
                "AzureBlob:AccountName must be set when DistributionMode is AzureBlob.");
        if (string.IsNullOrWhiteSpace(settings.ClientId))
            throw new InvalidOperationException(
                "AzureBlob:ClientId must be set when DistributionMode is AzureBlob.");

        var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = settings.TenantId,
            ClientId = settings.ClientId,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "NewsCentral" }
        });

        var serviceUri  = new Uri($"https://{settings.AccountName}.blob.core.windows.net");
        var blobService = new BlobServiceClient(serviceUri, credential);
        _container = blobService.GetBlobContainerClient(settings.ContainerName);

        System.Diagnostics.Debug.WriteLine($"{Tag} Container: {settings.ContainerName}");
    }

    // ── IBlobDistributionService ─────────────────────────────────────────────

    public async Task UploadTextAsync(string relativePath, string content)
    {
        await EnsureContainerAsync();
        var blob = _container.GetBlobClient(relativePath);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, overwrite: true);
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadText   → {relativePath}  ({content.Length} chars)");
    }

    public async Task UploadStreamAsync(string relativePath, Stream content)
    {
        await EnsureContainerAsync();
        var blob = _container.GetBlobClient(relativePath);
        await blob.UploadAsync(content, overwrite: true);
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadStream → {relativePath}");
    }

    public async Task DeleteAsync(string relativePath)
    {
        await EnsureContainerAsync();
        var blob = _container.GetBlobClient(relativePath);
        var deleted = await blob.DeleteIfExistsAsync();
        System.Diagnostics.Debug.WriteLine(deleted
            ? $"{Tag} Delete       → {relativePath}"
            : $"{Tag} Delete (not found, skipped) → {relativePath}");
    }

    public async Task<bool> ExistsAsync(string relativePath)
    {
        await EnsureContainerAsync();
        var blob = _container.GetBlobClient(relativePath);
        return await blob.ExistsAsync();
    }

    private async Task EnsureContainerAsync()
    {
        if (_containerEnsured) return;
        await _container.CreateIfNotExistsAsync(PublicAccessType.None);
        _containerEnsured = true;
    }
}
