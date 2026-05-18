namespace NewsCentral.Services;

/// <summary>
/// No-op distribution service.
/// Active when EnableBlobDistribution = false in appsettings.json.
///
/// Every call is logged to the console so the publish/delete workflow
/// can be verified end-to-end without any files being written outside
/// the authoring tier. Flip EnableBlobDistribution to true when you
/// are ready to test actual distribution output.
/// </summary>
public class NullBlobDistributionService : IBlobDistributionService
{
    private const string Tag = "[BlobDist  NULL]";

    public Task UploadTextAsync(string relativePath, string content)
    {
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadText   → {relativePath}  ({content.Length} chars)");
        return Task.CompletedTask;
    }

    public Task UploadStreamAsync(string relativePath, Stream content)
    {
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadStream → {relativePath}");
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string relativePath)
    {
        System.Diagnostics.Debug.WriteLine($"{Tag} Delete       → {relativePath}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Always returns false so callers that guard on ExistsAsync before
    /// deleting skip the delete gracefully instead of trying to remove
    /// a blob that was never pushed.
    /// </summary>
    public Task<bool> ExistsAsync(string relativePath) =>
        Task.FromResult(false);
}
