using Microsoft.Extensions.Logging;
using NewsCentral.Security;
using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers TelemetryUploader.ProcessAsync: the Telemetry:UploadEnabled gate and the unconditional
/// local retention sweep. Hermetic — "now" and every file age are injected through the UtcNow /
/// LastWriteUtc seams, so nothing depends on real file timestamps or the system clock. Files are
/// real (the sweep enumerates and deletes through CacheManager on a temp directory) but their ages
/// come exclusively from the injected func.
/// </summary>
public sealed class TelemetryRetentionTests : IDisposable
{
    private static readonly DateTime Now   = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old   = Now.AddDays(-(TelemetryDefaults.RetentionDays + 1));
    private static readonly DateTime Fresh = Now.AddDays(-1);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "nsvc-telemetry-" + Guid.NewGuid().ToString("N"));
    private readonly string _dest =
        Path.Combine(Path.GetTempPath(), "nsvc-teledest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_dest, recursive: true); } catch { /* best effort */ }
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    /// <summary>Injected ages: any file whose name contains "old" is outside the window.</summary>
    private static DateTime AgeByName(string path) =>
        Path.GetFileName(path).Contains("old", StringComparison.OrdinalIgnoreCase) ? Old : Fresh;

    private (TelemetryUploader Sut, CapturingLogger<TelemetryUploader> Log, CacheManager Cache)
        NewSut(bool uploadEnabled, string sharePath)
    {
        var config = new ServiceConfiguration
        {
            Repository = new RepositorySection { SharePath = sharePath },
            Telemetry  = new TelemetrySection { UploadEnabled = uploadEnabled }
        };
        var cache = new CacheManager(_root, JsonDefaults.Options);
        var log   = new CapturingLogger<TelemetryUploader>();
        var sut = new TelemetryUploader(cache, config, new HmacService(new HmacOptions()), log)
        {
            UtcNow       = () => Now,
            LastWriteUtc = AgeByName
        };
        return (sut, log, cache);
    }

    private string CreateSessionFile(string name)
    {
        var uploads = Path.Combine(_root, "uploads");
        Directory.CreateDirectory(uploads);
        var path = Path.Combine(uploads, name);
        File.WriteAllText(path, "{}");   // parses as SessionTelemetry; HMAC disabled → pass-through
        return path;
    }

    private string DestFile(string name) => Path.Combine(_dest, "uploads", name);

    // ── Facts ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadDisabled_NothingCopied_SweepStillRuns()
    {
        var oldFile   = CreateSessionFile("session-old.json");
        var freshFile = CreateSessionFile("session-fresh.json");
        var (sut, log, _) = NewSut(uploadEnabled: false, sharePath: _dest);

        await sut.ProcessAsync(CancellationToken.None);

        // Nothing forwarded — the destination uploads folder was never even created.
        Assert.False(Directory.Exists(Path.Combine(_dest, "uploads")));
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("upload disabled"));

        // But the retention sweep ran: the expired file is gone, the fresh one kept.
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(freshFile));
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Telemetry retention"));
    }

    [Fact]
    public async Task FileOlderThanWindow_IsDeleted()
    {
        var oldFile = CreateSessionFile("session-old.json");
        var (sut, log, _) = NewSut(uploadEnabled: true, sharePath: "");   // upload skipped → survivor

        await sut.ProcessAsync(CancellationToken.None);

        Assert.False(File.Exists(oldFile));
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Telemetry retention"));
    }

    [Fact]
    public async Task FileInsideWindow_IsKept()
    {
        var freshFile = CreateSessionFile("session-fresh.json");
        var (sut, log, _) = NewSut(uploadEnabled: true, sharePath: "");

        await sut.ProcessAsync(CancellationToken.None);

        Assert.True(File.Exists(freshFile));
        // Nothing deleted → no Information from the sweep (log-on-change).
        Assert.DoesNotContain(log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Telemetry retention"));
    }

    [Fact]
    public async Task SharePathUnconfigured_NothingUploaded_SweepStillSweeps()
    {
        var oldFile   = CreateSessionFile("session-old.json");
        var freshFile = CreateSessionFile("session-fresh.json");
        var (sut, log, _) = NewSut(uploadEnabled: true, sharePath: "");

        await sut.ProcessAsync(CancellationToken.None);

        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("SharePath is not configured"));
        Assert.False(File.Exists(oldFile));      // swept despite the upload early-out
        Assert.True(File.Exists(freshFile));
    }

    [Fact]
    public async Task DeleteThrows_LogsWarning_SweepContinues()
    {
        var lockedFile = CreateSessionFile("session-old-locked.json");
        var otherFile  = CreateSessionFile("session-old-other.json");
        var (sut, log, _) = NewSut(uploadEnabled: false, sharePath: "");

        // Hold the file open with no sharing so File.Delete genuinely throws.
        await using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await sut.ProcessAsync(CancellationToken.None);
        }

        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("failed to delete"));
        Assert.True(File.Exists(lockedFile));    // could not be deleted
        Assert.False(File.Exists(otherFile));    // one locked file never aborts the sweep
    }

    [Fact]
    public async Task NormalOperation_FilesUploadedAndRemoved_BeforeRetentionIsRelevant()
    {
        // Even a file already outside the window is uploaded, not swept: upload runs first, and a
        // successful copy removes the local file before retention ever considers it.
        var oldFile   = CreateSessionFile("session-old.json");
        var freshFile = CreateSessionFile("session-fresh.json");
        var (sut, log, _) = NewSut(uploadEnabled: true, sharePath: _dest);

        await sut.ProcessAsync(CancellationToken.None);

        Assert.True(File.Exists(DestFile("session-old.json")));
        Assert.True(File.Exists(DestFile("session-fresh.json")));
        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(freshFile));
        Assert.Contains(log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("2/2 uploaded"));
        // Retention had nothing left to do.
        Assert.DoesNotContain(log.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("Telemetry retention"));
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
