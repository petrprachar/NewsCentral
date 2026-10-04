namespace NewsCentral.Services;

/// <summary>
/// IStorageService backed by the local file system or a mounted network share.
/// When Azure Files is mounted as a Windows drive letter or UNC path,
/// this implementation works without any code changes — the OS handles
/// the SMB protocol transparently.
///
/// All relative paths are resolved against EnvironmentContext.DataPath, read fresh on every call
/// (no cached base path), so a runtime DataPath switch takes effect on the very next operation.
/// Example: "team-alpha/content/presentations/pres_123.json"
///       → "C:\NewsCentral\team-alpha\content\presentations\pres_123.json"
/// </summary>
public class LocalStorageService : IStorageService
{
    private readonly EnvironmentContext _environment;

    /// <summary>Primary constructor — used by DI.</summary>
    public LocalStorageService(EnvironmentContext environment)
    {
        _environment = environment;
    }

    // ── Path resolution ─────────────────────────────────────────────────────

    private string Resolve(string relativePath) =>
        Path.Combine(_environment.DataPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void EnsureParentExists(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    // ── Text ────────────────────────────────────────────────────────────────

    public async Task<string?> ReadTextAsync(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        return File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath)
            : null;
    }

    public async Task WriteTextAsync(string relativePath, string content)
    {
        var fullPath = Resolve(relativePath);
        EnsureParentExists(fullPath);
        await File.WriteAllTextAsync(fullPath, content);
    }

    // ── Binary ──────────────────────────────────────────────────────────────

    public async Task<byte[]?> ReadBytesAsync(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        return File.Exists(fullPath)
            ? await File.ReadAllBytesAsync(fullPath)
            : null;
    }

    public async Task WriteBytesAsync(string relativePath, byte[] content)
    {
        var fullPath = Resolve(relativePath);
        EnsureParentExists(fullPath);
        await File.WriteAllBytesAsync(fullPath, content);
    }

    public async Task WriteStreamAsync(string relativePath, Stream content)
    {
        var fullPath = Resolve(relativePath);
        EnsureParentExists(fullPath);
        using var fs = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs);
    }

    /// <summary>
    /// Opens the file with FileShare.ReadWrite so other processes
    /// (e.g. the app viewing the image while the index is being rebuilt)
    /// are not blocked. Caller must dispose the returned stream.
    /// </summary>
    public Task<Stream?> OpenReadAsync(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        if (!File.Exists(fullPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Task.FromResult<Stream?>(stream);
    }

    // ── Metadata ────────────────────────────────────────────────────────────

    public Task<bool> FileExistsAsync(string relativePath) =>
        Task.FromResult(File.Exists(Resolve(relativePath)));

    public Task<long> GetFileSizeAsync(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        return Task.FromResult(File.Exists(fullPath)
            ? new FileInfo(fullPath).Length
            : 0L);
    }

    public Task<IEnumerable<string>> ListFilesAsync(
        string relativeFolderPath,
        string searchPattern = "*.json")
    {
        var basePath = _environment.DataPath;
        var fullFolder = Resolve(relativeFolderPath);

        if (!Directory.Exists(fullFolder))
            return Task.FromResult(Enumerable.Empty<string>());

        // Return paths relative to basePath using forward-slash convention
        var files = Directory.GetFiles(fullFolder, searchPattern)
            .Select(f => Path.GetRelativePath(basePath, f)
                            .Replace(Path.DirectorySeparatorChar, '/'));

        return Task.FromResult(files);
    }

    // ── Folder ──────────────────────────────────────────────────────────────

    public Task EnsureFolderExistsAsync(string relativeFolderPath)
    {
        Directory.CreateDirectory(Resolve(relativeFolderPath));
        return Task.CompletedTask;
    }

    // ── Move / Copy / Delete ────────────────────────────────────────────────

    public Task MoveFileAsync(string sourceRelativePath, string destRelativePath)
    {
        var srcPath = Resolve(sourceRelativePath);
        var dstPath = Resolve(destRelativePath);

        if (!File.Exists(srcPath))
            throw new IOException($"Cannot move — source not found: {srcPath}");

        EnsureParentExists(dstPath);
        File.Move(srcPath, dstPath, overwrite: true);
        return Task.CompletedTask;
    }

    public async Task CopyFileAsync(string sourceRelativePath, string destRelativePath)
    {
        var srcPath = Resolve(sourceRelativePath);
        var dstPath = Resolve(destRelativePath);

        if (!File.Exists(srcPath))
            throw new IOException($"Cannot copy — source not found: {srcPath}");

        // Self-copy: same physical path — nothing to do
        if (string.Equals(srcPath, dstPath, StringComparison.OrdinalIgnoreCase))
            return;

        EnsureParentExists(dstPath);

        // FileShare.ReadWrite allows other processes to keep the source open
        using var src = new FileStream(
            srcPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(
            dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst);
    }

    public Task DeleteFileAsync(string relativePath)
    {
        var fullPath = Resolve(relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        return Task.CompletedTask;
    }
}
