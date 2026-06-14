using NewsService.Services;
using Xunit;

using Action = NewsService.Services.SyncService.LockScreenAction;

namespace NewsService.Tests;

/// <summary>
/// Covers SyncService.DecideLockScreen — the pure apply-on-change decision for the lock screen.
/// NewsService applies the lock screen only (wallpaper moved to NewsViewer); a configurable default
/// image is applied when no lock-screen content is active. The PersonalizationCSP write itself is a
/// thin, environment-bound seam and is not unit-tested here.
/// </summary>
public sealed class LockScreenDecisionTests
{
    private const string Sentinel = SyncService.DefaultLockScreenSentinel;

    // ── Winner selection / apply-on-change ───────────────────────────────────

    [Fact]
    public void Winner_NotYetApplied_AppliesContentAndAdvancesState()
    {
        var d = SyncService.DecideLockScreen(
            winnerPresentationId: "pres-1",
            lastAppliedId: null,
            defaultLockScreenPath: "",
            defaultFileExists: false);

        Assert.Equal(Action.ApplyContent, d.Action);
        Assert.Equal("pres-1", d.NewStateId);
    }

    [Fact]
    public void Winner_AlreadyApplied_NoOp()
    {
        var d = SyncService.DecideLockScreen("pres-1", "pres-1", "", false);

        Assert.Equal(Action.None, d.Action);
        Assert.Null(d.NewStateId);   // state unchanged → CSP not re-asserted
    }

    [Fact]
    public void Winner_DifferentFromState_OverridesDefaultSentinel()
    {
        // Default was applied last cycle; new content now wins.
        var d = SyncService.DecideLockScreen("pres-2", Sentinel, @"C:\img\default.jpg", true);

        Assert.Equal(Action.ApplyContent, d.Action);
        Assert.Equal("pres-2", d.NewStateId);
    }

    // ── No winner — default behavior ─────────────────────────────────────────

    [Fact]
    public void NoWinner_DefaultConfiguredAndPresent_AppliesDefault()
    {
        var d = SyncService.DecideLockScreen(null, "pres-1", @"C:\img\default.jpg", true);

        Assert.Equal(Action.ApplyDefault, d.Action);
        Assert.Equal(Sentinel, d.NewStateId);
    }

    [Fact]
    public void NoWinner_DefaultAlreadyApplied_NoOp()
    {
        var d = SyncService.DecideLockScreen(null, Sentinel, @"C:\img\default.jpg", true);

        Assert.Equal(Action.None, d.Action);
        Assert.Null(d.NewStateId);
    }

    [Fact]
    public void NoWinner_NoDefaultConfigured_NoChange_Sticky()
    {
        // Last applied content stays in place — nothing is reset.
        var d = SyncService.DecideLockScreen(null, "pres-1", "", false);

        Assert.Equal(Action.None, d.Action);
        Assert.Null(d.NewStateId);
    }

    [Fact]
    public void NoWinner_DefaultConfiguredButFileMissing_WarnsNoChange()
    {
        var d = SyncService.DecideLockScreen(null, "pres-1", @"C:\img\missing.jpg", false);

        Assert.Equal(Action.DefaultMissing, d.Action);
        Assert.Null(d.NewStateId);
    }

    // ── Full sentinel transition: winner → default → winner ──────────────────

    [Fact]
    public void Transition_Winner_To_Default_To_Winner()
    {
        const string defaultPath = @"C:\img\default.jpg";

        // 1. Content wins from a clean state.
        var s1 = SyncService.DecideLockScreen("pres-1", null, defaultPath, true);
        Assert.Equal(Action.ApplyContent, s1.Action);
        var state = s1.NewStateId;                       // "pres-1"
        Assert.Equal("pres-1", state);

        // 2. Content expires; default takes over.
        var s2 = SyncService.DecideLockScreen(null, state, defaultPath, true);
        Assert.Equal(Action.ApplyDefault, s2.Action);
        state = s2.NewStateId;                           // sentinel
        Assert.Equal(Sentinel, state);

        // 3. New content wins again; overrides the default sentinel.
        var s3 = SyncService.DecideLockScreen("pres-3", state, defaultPath, true);
        Assert.Equal(Action.ApplyContent, s3.Action);
        Assert.Equal("pres-3", s3.NewStateId);
    }
}
