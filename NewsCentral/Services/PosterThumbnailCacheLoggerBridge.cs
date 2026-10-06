using Microsoft.Extensions.Logging;
using NewsCentral.Ui;

namespace NewsCentral.Services;

/// <summary>
/// UI-2.2: adapts the app's own ILogger&lt;PosterThumbnailCache&gt; (NewsCentral already
/// references Microsoft.Extensions.Logging) to the small IPosterThumbnailCacheLogger seam that
/// NewsCentral.Shared exposes — Shared itself does not reference Logging.Abstractions.
/// </summary>
public sealed class PosterThumbnailCacheLoggerBridge : IPosterThumbnailCacheLogger
{
    private readonly ILogger<PosterThumbnailCache> _logger;

    public PosterThumbnailCacheLoggerBridge(ILogger<PosterThumbnailCache> logger)
    {
        _logger = logger;
    }

    public void LogWarning(string message) => _logger.LogWarning("{Message}", message);
}
