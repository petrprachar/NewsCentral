using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers ImagePublisher.Publish (hash re-verification + content-derived naming + no-recopy) and
/// SweepExcept (stale-file cleanup). Files are real, on a temp publish root cleaned up per test.
/// </summary>
public sealed class ImagePublisherTests : IDisposable
{
    private readonly string _publishRoot =
        Path.Combine(Path.GetTempPath(), "nsvc-publish-" + Guid.NewGuid().ToString("N"));
    private readonly string _sourceDir =
        Path.Combine(Path.GetTempPath(), "nsvc-source-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_publishRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_sourceDir, recursive: true); } catch { /* best effort */ }
    }

    private (ImagePublisher Sut, CapturingLogger<ImagePublisher> Log) NewSut()
    {
        var config = new ServiceConfiguration
        {
            Delivery = new DeliverySection { PublishedImagePath = _publishRoot }
        };
        var log = new CapturingLogger<ImagePublisher>();
        return (new ImagePublisher(config, log), log);
    }

    private string CreateSourceFile(byte[] bytes, string extension = ".jpg")
    {
        Directory.CreateDirectory(_sourceDir);
        var path = Path.Combine(_sourceDir, $"src-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string HexOf(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // ── Publish ───────────────────────────────────────────────────────────────

    [Fact]
    public void HashMatch_CopiesFile_ReturnsContentDerivedName()
    {
        var bytes = "hello world"u8.ToArray();
        var hex   = HexOf(bytes);
        var source = CreateSourceFile(bytes, ".jpg");
        var (sut, _) = NewSut();

        var result = sut.Publish(source, $"sha256:{hex}", "lockscreen");

        Assert.NotNull(result);
        Assert.Equal($"lockscreen-{hex[..16]}.jpg", Path.GetFileName(result));
        Assert.True(File.Exists(result));
        Assert.Equal(bytes, File.ReadAllBytes(result!));
    }

    [Fact]
    public void HashMismatch_ReturnsNull_NothingWritten_LogsError()
    {
        var bytes  = "hello world"u8.ToArray();
        var source = CreateSourceFile(bytes);
        var (sut, log) = NewSut();

        var result = sut.Publish(source, "sha256:" + new string('0', 64), "lockscreen");

        Assert.Null(result);
        Assert.False(Directory.Exists(_publishRoot) && Directory.EnumerateFiles(_publishRoot).Any());
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("hash mismatch"));
    }

    [Fact]
    public void Sha256PrefixAndBareHex_BothAccepted_CaseInsensitive()
    {
        var bytes = "prefix-and-bare"u8.ToArray();
        var hex   = HexOf(bytes);

        var (sutPrefixed, _) = NewSut();
        var resultPrefixed = sutPrefixed.Publish(CreateSourceFile(bytes), $"sha256:{hex}", "lockscreen");
        Assert.NotNull(resultPrefixed);

        var (sutBare, _) = NewSut();
        var resultBare = sutBare.Publish(CreateSourceFile(bytes), hex, "lockscreen");
        Assert.NotNull(resultBare);

        var (sutUpper, _) = NewSut();
        var resultUpper = sutUpper.Publish(CreateSourceFile(bytes), $"SHA256:{hex.ToUpperInvariant()}", "lockscreen");
        Assert.NotNull(resultUpper);
    }

    [Fact]
    public void TargetAlreadyExists_DoesNotRecopy_ReturnsExistingPath()
    {
        var bytes  = "no recopy"u8.ToArray();
        var hex    = HexOf(bytes);
        var source = CreateSourceFile(bytes);
        var (sut, _) = NewSut();

        var first = sut.Publish(source, hex, "lockscreen");
        Assert.NotNull(first);

        // Overwrite the published file directly so a re-copy would be observable.
        var sentinel = "SENTINEL-UNCHANGED"u8.ToArray();
        File.WriteAllBytes(first!, sentinel);

        var second = sut.Publish(source, hex, "lockscreen");

        Assert.Equal(first, second);
        Assert.Equal(sentinel, File.ReadAllBytes(second!));   // proves no re-copy occurred
    }

    [Fact]
    public void MissingSource_ReturnsNull_NoThrow()
    {
        var (sut, log) = NewSut();
        var missing = Path.Combine(_sourceDir, "does-not-exist.jpg");

        var ex = Record.Exception(() => sut.Publish(missing, "sha256:" + new string('0', 64), "lockscreen"));

        Assert.Null(ex);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void PublishRootDoesNotExist_IsCreated()
    {
        Assert.False(Directory.Exists(_publishRoot));

        var bytes  = "creates root"u8.ToArray();
        var source = CreateSourceFile(bytes);
        var (sut, _) = NewSut();

        var result = sut.Publish(source, HexOf(bytes), "lockscreen");

        Assert.NotNull(result);
        Assert.True(Directory.Exists(_publishRoot));
    }

    // ── SweepExcept ───────────────────────────────────────────────────────────

    [Fact]
    public void SweepExcept_DeletesOtherLockscreenFiles_KeepsNamed_IgnoresUnrelated()
    {
        Directory.CreateDirectory(_publishRoot);
        var keep    = Path.Combine(_publishRoot, "lockscreen-aaaaaaaaaaaaaaaa.jpg");
        var stale   = Path.Combine(_publishRoot, "lockscreen-bbbbbbbbbbbbbbbb.jpg");
        var unrelated = Path.Combine(_publishRoot, "not-a-lockscreen-file.txt");
        File.WriteAllText(keep, "keep");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(unrelated, "unrelated");
        var (sut, _) = NewSut();

        sut.SweepExcept("lockscreen-aaaaaaaaaaaaaaaa.jpg", "lockscreen");

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void SweepExcept_NullKeep_DeletesAllLockscreenFiles()
    {
        Directory.CreateDirectory(_publishRoot);
        var a = Path.Combine(_publishRoot, "lockscreen-aaaaaaaaaaaaaaaa.jpg");
        var b = Path.Combine(_publishRoot, "lockscreen-bbbbbbbbbbbbbbbb.jpg");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        var (sut, _) = NewSut();

        sut.SweepExcept(null, "lockscreen");

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void SweepExcept_IsScopedToPrefix_NeverTouchesOtherSurfacesFiles()
    {
        Directory.CreateDirectory(_publishRoot);
        var lockFile = Path.Combine(_publishRoot, "lockscreen-aaaaaaaaaaaaaaaa.jpg");
        var wallFile = Path.Combine(_publishRoot, "wallpaper-bbbbbbbbbbbbbbbb.jpg");
        File.WriteAllText(lockFile, "lock");
        File.WriteAllText(wallFile, "wall");
        var (sut, _) = NewSut();

        sut.SweepExcept(null, "wallpaper");

        Assert.True(File.Exists(lockFile));    // a wallpaper sweep must never delete a lockscreen-* file
        Assert.False(File.Exists(wallFile));
    }

    // ── Test double ───────────────────────────────────────────────────────────

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
