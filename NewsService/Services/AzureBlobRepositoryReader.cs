namespace NewsService.Services;

/// <summary>
/// Azure Blob Storage repository reader.
/// Phase 2 — requires machine certificate authentication (not yet implemented).
/// Set StorageMode=Share in registry for development and on-premises use.
/// </summary>
public sealed class AzureBlobRepositoryReader : IRepositoryReader
{
    private readonly ILogger<AzureBlobRepositoryReader> _logger;

    public AzureBlobRepositoryReader(ILogger<AzureBlobRepositoryReader> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => false;
    public string SyncSource => "Azure";

    public Task<string?> ReadTextAsync(string relativePath, CancellationToken ct = default)
    {
        _logger.LogWarning("Azure Blob repository reader is not yet implemented. Set StorageMode=Share in registry.");
        return Task.FromResult<string?>(null);
    }

    public Task<byte[]?> ReadBytesAsync(string relativePath, CancellationToken ct = default)
    {
        _logger.LogWarning("Azure Blob repository reader is not yet implemented.");
        return Task.FromResult<byte[]?>(null);
    }
}
