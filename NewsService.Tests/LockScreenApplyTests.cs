using Microsoft.Extensions.Logging;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers SyncService.ApplyIntendedLockScreen — the registry-gated, stateless apply step.
/// The live PersonalizationCSP value (surfaced via ILockScreenService.GetCurrentLockScreenPath)
/// is the single source of truth: the lock screen is written only when the intended image differs
/// from the current value, and a failed write is never recorded as applied. A fake
/// ILockScreenService stands in for the registry.
/// </summary>
public sealed class LockScreenApplyTests
{
    private const string WinnerPath  = @"C:\cache\cz-its\images\generated\pres-1.jpg";
    private const string DefaultPath = @"C:\img\default.jpg";

    private static SyncService NewSut(FakeLockScreen fake, CapturingLogger<SyncService> log) =>
        // Only lockScreen + logger are exercised by ApplyIntendedLockScreen; the rest are unused.
        new(repository: null!, cache: null!, lockScreen: fake, imagePublisher: null!, telemetry: null!,
            entra: null!, configuration: null!, logger: log);

    [Fact]
    public void WinnerActive_CurrentDiffers_AppliesWinnerPath()
    {
        var fake = new FakeLockScreen { Current = @"C:\something\else.jpg" };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Equal(new[] { WinnerPath }, fake.SetCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void WinnerActive_CurrentMatches_DoesNotApply()
    {
        var fake = new FakeLockScreen { Current = WinnerPath };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Empty(fake.SetCalls);
    }

    [Fact]
    public void WinnerActive_CurrentMatchesCaseAndSeparators_DoesNotApply()
    {
        // Normalization: same target reached via a non-canonical current value still matches.
        var fake = new FakeLockScreen { Current = @"C:\CACHE\cz-its\images\generated\..\generated\pres-1.JPG" };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its");

        Assert.Empty(fake.SetCalls);
    }

    [Fact]
    public void NoWinner_DefaultExists_CurrentDiffers_AppliesDefault()
    {
        var fake = new FakeLockScreen { Current = null };   // CSP value absent
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(DefaultPath, "default");

        Assert.Equal(new[] { DefaultPath }, fake.SetCalls);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public void NoWinner_DefaultExists_CurrentMatches_DoesNotApply()
    {
        var fake = new FakeLockScreen { Current = DefaultPath };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(DefaultPath, "default");

        Assert.Empty(fake.SetCalls);
    }

    [Fact]
    public void NoWinner_NoDefault_AnyCurrent_DoesNotApply_Sticky()
    {
        // intended == null → leave whatever is currently set untouched.
        var fake = new FakeLockScreen { Current = @"C:\whatever\is\set.jpg" };
        var log  = new CapturingLogger<SyncService>();

        NewSut(fake, log).ApplyIntendedLockScreen(null, string.Empty);

        Assert.Empty(fake.SetCalls);
    }

    [Fact]
    public void SetReturnsFalse_LogsError_NoThrow_NothingRecorded()
    {
        var fake = new FakeLockScreen { Current = @"C:\old.jpg", SetResult = false };
        var log  = new CapturingLogger<SyncService>();

        var ex = Record.Exception(() =>
            NewSut(fake, log).ApplyIntendedLockScreen(WinnerPath, "presentation pres-1, team cz-its"));

        Assert.Null(ex);                                                  // no throw
        Assert.Equal(new[] { WinnerPath }, fake.SetCalls);               // attempted
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);    // error logged
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Information); // not claimed applied
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class FakeLockScreen : ILockScreenService
    {
        public string? Current { get; set; }
        public bool SetResult { get; set; } = true;
        public List<string> SetCalls { get; } = new();

        public bool SetLockScreen(string imagePath)
        {
            SetCalls.Add(imagePath);
            if (SetResult) Current = imagePath;
            return SetResult;
        }

        public string? GetCurrentLockScreenPath() => Current;
    }

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
