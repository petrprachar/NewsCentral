using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Distribution service backed by a local disk subtree.
/// Active when EnableBlobDistribution = true and DistributionMode = "Local".
///
/// Mirrors the Azure Blob path structure exactly so that switching to
/// AzureBlobDistributionService requires zero path changes anywhere else.
///
/// Root: AppConfiguration.LocalDistributionPath
///   (must be DIFFERENT from DataPath — the separation is the point)
///
/// If LocalDistributionPath is not configured, falls back to a
/// "_distribution" subfolder next to DataPath so the app always starts.
///
/// Example layout after a publish:
///   C:\NewsCentralDist\
///     team-alpha\
///       content\presentations\pres_abc.json
///       content\schedules\sched_def.json
///       images\generated\poster_abc_v1.jpg
///       index.json
///     team-beta\
///       index.json
/// </summary>
public class LocalBlobDistributionService : IBlobDistributionService
{
    private readonly string _root;
    private const string Tag = "[BlobDist LOCAL]";

    public LocalBlobDistributionService(AppConfiguration config)
    {
        _root = string.IsNullOrWhiteSpace(config.LocalDistributionPath)
            ? Path.Combine(config.DataPath, "_distribution")
            : config.LocalDistributionPath;

        System.Diagnostics.Debug.WriteLine($"{Tag} Root: {_root}");
    }

    // ── Path resolution ─────────────────────────────────────────────────────

    private string Resolve(string relativePath) =>
        Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void EnsureParentExists(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    // ── IBlobDistributionService ─────────────────────────────────────────────

    public async Task UploadTextAsync(string relativePath, string content)
    {
        var fullPath = Resolve(relativePath);
        EnsureParentExists(fullPath);
        await File.WriteAllTextAsync(fullPath, content);
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadText   → {relativePath}  ({content.Length} chars)");
    }

    public async Task UploadStreamAsync(string relativePath, Stream content)
    {
        var fullPath = Resolve(relativePath);
        EnsureParentExists(fullPath);
        using var fs = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs);
        System.Diagnostics.Debug.WriteLine($"{Tag} UploadStream → {relativePath}");
    }

    public Task DeleteAsync(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            System.Diagnostics.Debug.WriteLine($"{Tag} Delete       → {relativePath}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"{Tag} Delete (not found, skipped) → {relativePath}");
        }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string relativePath) =>
        Task.FromResult(File.Exists(Resolve(relativePath)));
}
