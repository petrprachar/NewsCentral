using NewsCentral.Models;
using NewsCentral.Ui;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class PosterThumbnailCacheTests
{
    private static Presentation MakePresentation(
        string id = "p1", int version = 1, DateTime? lastModified = null, string imageBase64 = "AAAA")
    {
        return new Presentation
        {
            PresentationID = id,
            Version = version,
            LastModified = lastModified ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ContentImageBase64 = imageBase64
        };
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_EmptyImage_ReturnsNullWithoutCallingScaler()
    {
        var scaler = FakeScaler.Sync(_ => new byte[] { 1, 2, 3 });
        var cache = new PosterThumbnailCache(scaler);
        var presentation = MakePresentation(imageBase64: string.Empty);

        var result = await cache.GetThumbnailDataUrlAsync(presentation);

        Assert.Null(result);
        Assert.Equal(0, scaler.CallCount);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_CacheHit_DoesNotCallScalerAgain()
    {
        var scaler = FakeScaler.Sync(_ => new byte[] { 9, 9, 9 });
        var cache = new PosterThumbnailCache(scaler);
        var presentation = MakePresentation();

        var first = await cache.GetThumbnailDataUrlAsync(presentation);
        var second = await cache.GetThumbnailDataUrlAsync(presentation);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, scaler.CallCount);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_ResultIsADataUrl()
    {
        var scaler = FakeScaler.Sync(_ => new byte[] { 1, 2, 3 });
        var cache = new PosterThumbnailCache(scaler);
        var presentation = MakePresentation();

        var result = await cache.GetThumbnailDataUrlAsync(presentation);

        Assert.StartsWith("data:image/jpeg;base64,", result);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_VersionChange_Regenerates()
    {
        var scaler = FakeScaler.Sync(_ => new byte[] { 1 });
        var cache = new PosterThumbnailCache(scaler);
        var v1 = MakePresentation(version: 1);
        var v2 = MakePresentation(version: 2);

        await cache.GetThumbnailDataUrlAsync(v1);
        await cache.GetThumbnailDataUrlAsync(v2);

        Assert.Equal(2, scaler.CallCount);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_LastModifiedChange_Regenerates()
    {
        var scaler = FakeScaler.Sync(_ => new byte[] { 1 });
        var cache = new PosterThumbnailCache(scaler);
        var original = MakePresentation(lastModified: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var edited = MakePresentation(lastModified: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        await cache.GetThumbnailDataUrlAsync(original);
        await cache.GetThumbnailDataUrlAsync(edited);

        Assert.Equal(2, scaler.CallCount);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_ScalerReturnsNull_ResultIsNull_NoFullImageFallback()
    {
        var scaler = FakeScaler.Sync(_ => null);
        var cache = new PosterThumbnailCache(scaler);
        var presentation = MakePresentation();

        var result = await cache.GetThumbnailDataUrlAsync(presentation);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_ScalerThrows_ResultIsNull_NoFullImageFallback()
    {
        var scaler = FakeScaler.Sync(_ => throw new InvalidOperationException("decode failed"));
        var cache = new PosterThumbnailCache(scaler);
        var presentation = MakePresentation();

        var result = await cache.GetThumbnailDataUrlAsync(presentation);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_ScalerThrows_LogsAWarning()
    {
        var scaler = FakeScaler.Sync(_ => throw new InvalidOperationException("decode failed"));
        var logger = new FakeLogger();
        var cache = new PosterThumbnailCache(scaler, logger);
        var presentation = MakePresentation();

        await cache.GetThumbnailDataUrlAsync(presentation);

        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_ConcurrentRequestsForSameKey_CallsScalerOnce()
    {
        var gate = new SemaphoreSlim(0);
        var entered = 0;
        var scaler = FakeScaler.Async(async _ =>
        {
            Interlocked.Increment(ref entered);
            await gate.WaitAsync();
            return new byte[] { 1 };
        });
        var cache = new PosterThumbnailCache(scaler);
        var presentation = MakePresentation();

        var task1 = cache.GetThumbnailDataUrlAsync(presentation);
        var task2 = cache.GetThumbnailDataUrlAsync(presentation);

        // Give both calls a chance to race into the cache before releasing the scaler.
        await Task.Delay(20);
        gate.Release(2);

        var results = await Task.WhenAll(task1, task2);

        Assert.Equal(1, scaler.CallCount);
        Assert.Equal(1, entered);
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public async Task GetThumbnailDataUrlAsync_PastCapacity_EvictsLeastRecentlyUsed()
    {
        var scaler = FakeScaler.Sync(_ => new byte[] { 1 });
        var cache = new PosterThumbnailCache(scaler);

        for (var i = 0; i < PosterThumbnailCache.MaxEntries; i++)
            await cache.GetThumbnailDataUrlAsync(MakePresentation(id: $"p{i}"));

        Assert.Equal(PosterThumbnailCache.MaxEntries, scaler.CallCount);

        // Re-touch p0 so it becomes the most-recently-used entry; p1 is now the least-recently-
        // used one (every other entry was inserted, and therefore touched, after it).
        await cache.GetThumbnailDataUrlAsync(MakePresentation(id: "p0"));
        Assert.Equal(PosterThumbnailCache.MaxEntries, scaler.CallCount); // cache hit, no new call

        // One more distinct key pushes the cache over capacity — p1, the current
        // least-recently-used entry, must be evicted; nothing else is.
        await cache.GetThumbnailDataUrlAsync(MakePresentation(id: "overflow"));
        var callsAfterOverflow = PosterThumbnailCache.MaxEntries + 1;
        Assert.Equal(callsAfterOverflow, scaler.CallCount);

        // p0 (explicitly refreshed above) and overflow (just inserted) must still be cached.
        await cache.GetThumbnailDataUrlAsync(MakePresentation(id: "p0"));
        await cache.GetThumbnailDataUrlAsync(MakePresentation(id: "overflow"));
        Assert.Equal(callsAfterOverflow, scaler.CallCount);

        // p2 was inserted after p1 and was never evicted, so it must still be cached too.
        await cache.GetThumbnailDataUrlAsync(MakePresentation(id: "p2"));
        Assert.Equal(callsAfterOverflow, scaler.CallCount);

        // p1 — the one entry evicted above — must regenerate.
        await cache.GetThumbnailDataUrlAsync(MakePresentation(id: "p1"));
        Assert.Equal(callsAfterOverflow + 1, scaler.CallCount);
    }

    private sealed class FakeScaler : IPosterThumbnailScaler
    {
        private readonly Func<byte[], Task<byte[]?>> _handler;
        private int _callCount;

        private FakeScaler(Func<byte[], Task<byte[]?>> handler)
        {
            _handler = handler;
        }

        public static FakeScaler Sync(Func<byte[], byte[]?> handler) =>
            new(bytes => Task.FromResult(handler(bytes)));

        public static FakeScaler Async(Func<byte[], Task<byte[]?>> handler) => new(handler);

        public int CallCount => _callCount;

        public Task<byte[]?> ScaleToJpegAsync(byte[] source, int maxWidth, int maxHeight, int quality, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return _handler(source);
        }
    }

    private sealed class FakeLogger : IPosterThumbnailCacheLogger
    {
        public List<string> Warnings { get; } = new();

        public void LogWarning(string message) => Warnings.Add(message);
    }
}
