namespace NewsCentral.Services;

/// <summary>
/// Handles poster image storage for the authoring tier.
/// Image processing (SixLabors) was removed — the original image is stored
/// as-is under the "generated" folder, acting as the poster.
///
/// No blob distribution here — pushing to Azure Blob is handled by
/// PublishingService after the assignment is approved and published.
/// </summary>
public class PosterGenerationService
{
    private readonly IStorageService _storage;

    public PosterGenerationService(IStorageService storage)
    {
        _storage = storage;
    }

    /// <summary>
    /// Saves the image data as a poster file in the team's generated-images folder.
    /// Returns the relative path for storage in the Presentation record.
    /// e.g. "team-alpha/images/generated/poster_{id}_v1.jpg"
    /// </summary>
    public async Task<string> GeneratePosterAsync(
        string teamFolderName,
        string presentationId,
        string version,
        byte[] originalImageData,
        string headlineText,
        string bodyText,
        string ctaText = "Learn More",
        PosterLayout layout = PosterLayout.Standard)
    {
        var posterFileName = $"poster_{presentationId}_v{version}.jpg";
        var relativePath   = $"{teamFolderName}/images/generated/{posterFileName}";

        // WriteBytesAsync creates the folder if it does not exist
        await _storage.WriteBytesAsync(relativePath, originalImageData);

        return relativePath;
    }

    /// <summary>
    /// Reads poster image bytes from the authoring tier.
    /// posterPath is the relative path stored in Presentation.GeneratedImagePath,
    /// e.g. "team-alpha/images/generated/poster_{id}_v1.jpg"
    /// </summary>
    public async Task<byte[]> GetPosterImageDataAsync(string teamFolderName, string posterPath)
    {
        var data = await _storage.ReadBytesAsync(posterPath);

        if (data == null)
            throw new FileNotFoundException($"Poster not found: {posterPath}");

        return data;
    }
}

public enum PosterLayout
{
    Standard,
    MinimalTop,
    CenterFocus,
    BottomOverlay
}
