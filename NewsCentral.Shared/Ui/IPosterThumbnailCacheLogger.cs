namespace NewsCentral.Ui;

/// <summary>
/// UI-2.2: a minimal logging seam for <see cref="PosterThumbnailCache"/>. NewsCentral.Shared does
/// not reference Microsoft.Extensions.Logging.Abstractions (and UI-2.2 may not add a new NuGet
/// package), so this tiny interface stands in for it; NewsCentral (the MAUI app) adapts its own
/// ILogger&lt;PosterThumbnailCache&gt; to this interface at DI registration time.
/// </summary>
public interface IPosterThumbnailCacheLogger
{
    void LogWarning(string message);
}

/// <summary>Default no-op logger — used when no logger is supplied (e.g. in tests).</summary>
public sealed class NullPosterThumbnailCacheLogger : IPosterThumbnailCacheLogger
{
    public static readonly NullPosterThumbnailCacheLogger Instance = new();

    private NullPosterThumbnailCacheLogger()
    {
    }

    public void LogWarning(string message)
    {
    }
}
