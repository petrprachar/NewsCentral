namespace NewsCentral.Services;

/// <summary>
/// Abstraction over the AUTHORING tier file storage.
/// Local disk and Azure Files (SMB) are both served by LocalStorageService.
/// All paths are relative to the storage root (EnvironmentContext.DataPath).
/// Use forward-slash convention throughout:
///   "{team}/content/presentations/pres_{id}.json"
///   "{team}/images/generated/{filename}"
///   "{team}/deleted/assign_{id}.json"
///   "config/users.json"
///
/// Azure Blob Storage is a separate concern handled by IBlobDistributionService.
/// </summary>
public interface IStorageService
{
    // ── Text ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read a UTF-8 text file (JSON, index).
    /// Returns null if the file does not exist.
    /// </summary>
    Task<string?> ReadTextAsync(string relativePath);

    /// <summary>
    /// Write UTF-8 text to a file, creating parent folders as needed.
    /// Overwrites any existing file.
    /// </summary>
    Task WriteTextAsync(string relativePath, string content);

    // ── Binary ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Read all bytes from a binary file (image).
    /// Returns null if the file does not exist.
    /// </summary>
    Task<byte[]?> ReadBytesAsync(string relativePath);

    /// <summary>
    /// Write all bytes to a binary file, creating parent folders as needed.
    /// Overwrites any existing file.
    /// </summary>
    Task WriteBytesAsync(string relativePath, byte[] content);

    /// <summary>
    /// Write a stream to a file, creating parent folders as needed.
    /// Used by PosterGenerationService (ImageSharp stream output).
    /// </summary>
    Task WriteStreamAsync(string relativePath, Stream content);

    /// <summary>
    /// Open a file for sequential reading.
    /// CALLER IS RESPONSIBLE for disposing the returned stream.
    /// Returns null if the file does not exist.
    /// Sequential read only — do not assume the stream is seekable
    /// (Azure Blob streams are not).
    /// Used by IndexGenerationService for SHA-256 hash streaming.
    /// </summary>
    Task<Stream?> OpenReadAsync(string relativePath);

    // ── Metadata ────────────────────────────────────────────────────────────

    /// <summary>
    /// Check whether a file exists.
    /// </summary>
    Task<bool> FileExistsAsync(string relativePath);

    /// <summary>
    /// Return the size of a file in bytes.
    /// Returns 0 if the file does not exist.
    /// Used by IndexGenerationService to populate ImageSizeBytes.
    /// Local: FileInfo.Length  |  Azure Blob: BlobProperties.ContentLength
    /// </summary>
    Task<long> GetFileSizeAsync(string relativePath);

    /// <summary>
    /// List relative paths of files in a folder matching a search pattern.
    /// Returns an empty enumerable if the folder does not exist.
    /// Returned paths use forward-slash convention.
    /// Azure Blob: pattern is prefix-matched, not glob.
    /// </summary>
    Task<IEnumerable<string>> ListFilesAsync(
        string relativeFolderPath,
        string searchPattern = "*.json");

    // ── Folder ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure a folder exists, creating it and any parents as needed.
    /// Azure Blob: no-op (blob storage has no real directories).
    /// </summary>
    Task EnsureFolderExistsAsync(string relativeFolderPath);

    // ── Move / Copy / Delete ────────────────────────────────────────────────

    /// <summary>
    /// Move a file — the foundation of soft-delete.
    /// Destination folder is created if needed.
    /// Throws IOException if the source does not exist.
    /// Azure Blob: implemented as server-side copy + delete (no native move).
    /// </summary>
    Task MoveFileAsync(string sourceRelativePath, string destRelativePath);

    /// <summary>
    /// Copy a file within the same storage root.
    /// Self-copy (source == dest) is silently skipped.
    /// Destination folder is created if needed.
    /// </summary>
    Task CopyFileAsync(string sourceRelativePath, string destRelativePath);

    /// <summary>
    /// Hard-delete a file. No-op if the file does not exist.
    /// Use MoveFileAsync for soft-delete to {team}/deleted/.
    /// </summary>
    Task DeleteFileAsync(string relativePath);
}
