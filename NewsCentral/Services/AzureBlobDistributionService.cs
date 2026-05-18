using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Distribution service backed by Azure Blob Storage.
/// Active when EnableBlobDistribution = true and DistributionMode = "AzureBlob".
///
/// NuGet required: Azure.Storage.Blobs (v12.x)
///   dotnet add package Azure.Storage.Blobs
///
/// The container is created as private — viewers and Windows Service agents
/// authenticate via Managed Identity or time-limited SAS tokens.
/// A CDN can be placed in front for high-volume viewer traffic.
///
/// Authentication phases:
///   Phase 1 (now):    connection string via AppConfiguration.AzureBlobConnectionString
///   Phase 2 (Entra):  replace the BlobServiceClient constructor with:
///                       new BlobServiceClient(
///                           new Uri($"https://{accountName}.blob.core.windows.net"),
///                           new DefaultAzureCredential())
///                     No other changes needed in this class or its callers.
/// </summary>
public class AzureBlobDistributionService : IBlobDistributionService
{
    private readonly BlobContainerClient _container;
    private const string Tag = "[BlobDist AZURE]";

    public AzureBlobDistributionService(AppConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.AzureBlobConnectionString))
            throw new InvalidOperationException(
                "Storage:AzureBlobConnectionString must be set when DistributionMode is AzureBlob. " +
                "Add the connection string to appsettings.json or user secrets.");

        // ── Phase 1: connection string ───────────────────────────────────────
        var blobService = new BlobServiceClient(config.AzureBlobConnectionString);
        _container = blobService.GetBlobContainerClient(config.AzureBlobContainerName);
        _container.CreateIfNotExists(PublicAccessType.None);

        System.Diagnostics.Debug.WriteLine($"{Tag} Container: {config.AzureBlobContainerName}");

        // ── Phase 2: Entra ID / Managed Identity (uncomment when ready) ──────
        // var accountName = config.AzureBlobAccountName;  // add to AppConfiguration
        // var uri = new Uri($"https://{accountName}.blob.core.windows.net");
        // var blobService = new BlobServiceClient(uri, new DefaultAzureCredential());
        // _container = blobService.GetBlobContainerClient(config.AzureBlobContainerName);
        // _container.CreateIfNotExists(PublicAccessType.None);
    }

    // ── IBlobDistributionService ─────────────────────────────────────────────

    public async Task UploadTextAsync(string relativePath, string content)
    {
        var blob = _container.GetBlobClient(relativePath);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await blob.UploadAsync(stream, overwrite: true);
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadText   → {relativePath}  ({content.Length} chars)");
    }

    public async Task UploadStreamAsync(string relativePath, Stream content)
    {
        var blob = _container.GetBlobClient(relativePath);
        await blob.UploadAsync(content, overwrite: true);
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadStream → {relativePath}");
    }

    public async Task DeleteAsync(string relativePath)
    {
        var blob = _container.GetBlobClient(relativePath);
        var deleted = await blob.DeleteIfExistsAsync();
        System.Diagnostics.Debug.WriteLine(deleted
            ? $"{Tag} Delete       → {relativePath}"
            : $"{Tag} Delete (not found, skipped) → {relativePath}");
    }

    public async Task<bool> ExistsAsync(string relativePath)
    {
        var blob = _container.GetBlobClient(relativePath);
        return await blob.ExistsAsync();
    }
}
