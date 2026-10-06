using NewsCentral.Models;

namespace NewsCentral.Ui;

/// <summary>
/// UI-2.2: a bounded LRU cache of small JPEG thumbnail data URLs, keyed by presentation identity
/// and content fingerprint. Exists so a page with many assignments can render one thumbnail per
/// DISTINCT presentation instead of embedding the full poster image once per row/batch — the
/// pattern that crashed the Assignments page's render batch at ~200 assignments (see
/// docs/newscentral-spec.md context in the UI-2.2 change). Thread-safe; a singleton for the
/// process lifetime.
/// </summary>
public sealed class PosterThumbnailCache
{
    /// <summary>Entries kept past this count are evicted, least-recently-used first.</summary>
    public const int MaxEntries = 256;

    private const int MaxWidth = 320;
    private const int MaxHeight = 240;
    private const int JpegQuality = 80;

    private readonly IPosterThumbnailScaler _scaler;
    private readonly IPosterThumbnailCacheLogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _index = new();
    private readonly LinkedList<CacheEntry> _lru = new(); // front = most recently used

    public PosterThumbnailCache(IPosterThumbnailScaler scaler, IPosterThumbnailCacheLogger? logger = null)
    {
        _scaler = scaler;
        _logger = logger ?? NullPosterThumbnailCacheLogger.Instance;
    }

    /// <summary>
    /// Cache key: identity (PresentationID) plus a content fingerprint (Version, LastModified
    /// ticks, image length) — any of the three changing produces a different key, so an edited
    /// presentation regenerates its thumbnail instead of serving a stale one.
    /// </summary>
    public static string KeyFor(Presentation presentation) =>
        $"{presentation.PresentationID}|{presentation.Version}|{presentation.LastModified.Ticks}|{presentation.ContentImageBase64.Length}";

    /// <summary>
    /// Returns a <c>data:image/jpeg;base64,...</c> thumbnail URL, or null if the presentation has
    /// no image or the image could not be decoded/scaled. Never falls back to the full-size image.
    /// Concurrent calls for the same cache key share exactly one generation.
    /// </summary>
    public Task<string?> GetThumbnailDataUrlAsync(Presentation presentation, CancellationToken ct = default)
    {
        if (presentation is null)
            throw new ArgumentNullException(nameof(presentation));

        if (string.IsNullOrEmpty(presentation.ContentImageBase64))
            return Task.FromResult<string?>(null);

        var key = KeyFor(presentation);
        Lazy<Task<string?>> lazy;

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                lazy = node.Value.Lazy;
            }
            else
            {
                // The Lazy factory is NOT invoked here — only metadata bookkeeping happens under
                // _gate. The actual decode/scale work runs when .Value is first awaited, below,
                // outside this lock, and Lazy<T> guarantees that work runs exactly once even if
                // this method is re-entered concurrently for the same key.
                lazy = new Lazy<Task<string?>>(
                    () => GenerateAsync(presentation, ct),
                    LazyThreadSafetyMode.ExecutionAndPublication);

                var entry = new CacheEntry(key, lazy);
                var newNode = _lru.AddFirst(entry);
                _index[key] = newNode;
                EvictLeastRecentlyUsed();
            }
        }

        return lazy.Value;
    }

    private async Task<string?> GenerateAsync(Presentation presentation, CancellationToken ct)
    {
        byte[] source;
        try
        {
            source = Convert.FromBase64String(presentation.ContentImageBase64);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(
                $"Poster thumbnail skipped for presentation '{presentation.PresentationID}': invalid image data ({ex.Message}).");
            return null;
        }

        byte[]? scaled;
        try
        {
            scaled = await _scaler.ScaleToJpegAsync(source, MaxWidth, MaxHeight, JpegQuality, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                $"Poster thumbnail skipped for presentation '{presentation.PresentationID}': scaler threw ({ex.Message}).");
            return null;
        }

        if (scaled is null)
        {
            _logger.LogWarning(
                $"Poster thumbnail skipped for presentation '{presentation.PresentationID}': the image could not be decoded.");
            return null;
        }

        return $"data:image/jpeg;base64,{Convert.ToBase64String(scaled)}";
    }

    private void EvictLeastRecentlyUsed()
    {
        while (_lru.Count > MaxEntries)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
        }
    }

    private sealed record CacheEntry(string Key, Lazy<Task<string?>> Lazy);
}
