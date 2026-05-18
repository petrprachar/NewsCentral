namespace NewsCentral.Services;

/// <summary>
/// Distribution tier — pushes published content to Azure Blob Storage
/// for consumption by high-volume viewers and Windows Service agents.
///
/// Only three services call this interface:
///   PublishingService       — upload presentation, schedule, images on publish
///   IndexGenerationService  — upload / delete index.json on every regeneration
///   PresentationService     — delete blobs on soft-delete of a presentation
///
/// All other services use IStorageService (authoring tier) exclusively.
///
/// Path convention mirrors IStorageService:
///   "{team}/content/presentations/pres_{id}.json"
///   "{team}/content/schedules/sched_{id}.json"
///   "{team}/images/generated/{filename}"
///   "{team}/index.json"
///
/// Implementations:
///   NullBlobDistributionService   EnableBlobDistribution = false  (dev, no side effects)
///   LocalBlobDistributionService  DistributionMode = "Local"      (dev/test, real files)
///   AzureBlobDistributionService  DistributionMode = "AzureBlob"  (production)
/// </summary>
public interface IBlobDistributionService
{
    /// <summary>
    /// Upload or overwrite a text file (JSON, index).
    /// Idempotent — safe to call on re-publish.
    /// </summary>
    Task UploadTextAsync(string relativePath, string content);

    /// <summary>
    /// Upload or overwrite a binary file from a stream (poster image, original image).
    /// Idempotent — safe to call on re-publish.
    /// </summary>
    Task UploadStreamAsync(string relativePath, Stream content);

    /// <summary>
    /// Hard-delete a file from the distribution tier.
    /// No-op if the file does not exist in the blob.
    /// Called when published content is soft-deleted from the authoring tier.
    /// </summary>
    Task DeleteAsync(string relativePath);

    /// <summary>
    /// Check whether a file exists in the distribution blob.
    /// Used to determine whether a delete operation needs to clean up
    /// the distribution tier (content may never have been published).
    /// </summary>
    Task<bool> ExistsAsync(string relativePath);
}
