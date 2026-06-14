using NewsCentral.Models.IndexFile;
using Xunit;

namespace NewsCentral.Shared.Tests;

/// <summary>
/// Covers ActiveAssignmentSelector — the pure active-window/day-of-week filter plus
/// "newest by PresentationLastModified" pick used by NewsViewer (poster + wallpaper) and mirrored
/// from NewsService's lock-screen selection. Exercised here with the IsWallpaper predicate.
/// </summary>
public sealed class ActiveAssignmentSelectorTests
{
    // Fixed instant so day-of-week mapping is deterministic.
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0);

    private static PublishedAssignmentIndex Make(
        string id, DateTime lastModified, bool isWallpaper,
        DateTime? start = null, DateTime? end = null, string days = "1,2,3,4,5,6,7") => new()
    {
        PresentationId           = id,
        PresentationLastModified = lastModified,
        ScheduleStart            = start ?? Now.AddDays(-1),
        ScheduleEnd              = end   ?? Now.AddDays(1),
        DaysOfWeek               = days,
        DisplayTypes             = new DisplayTypeInfo { IsWallpaper = isWallpaper },
    };

    [Fact]
    public void PickNewestActive_ReturnsNewestMatching()
    {
        var older = Make("old", Now.AddDays(-3), isWallpaper: true);
        var newer = Make("new", Now.AddHours(-1), isWallpaper: true);

        var result = ActiveAssignmentSelector.PickNewestActive(
            new[] { older, newer }, Now, a => a.DisplayTypes.IsWallpaper);

        Assert.Equal("new", result?.PresentationId);
    }

    [Fact]
    public void PickNewestActive_ExcludesNonMatchingPredicate()
    {
        var wp  = Make("wp",  Now.AddDays(-2), isWallpaper: true);
        var not = Make("not", Now,             isWallpaper: false);   // newer, but not a wallpaper

        var result = ActiveAssignmentSelector.PickNewestActive(
            new[] { wp, not }, Now, a => a.DisplayTypes.IsWallpaper);

        Assert.Equal("wp", result?.PresentationId);
    }

    [Fact]
    public void PickNewestActive_ExcludesOutOfWindow()
    {
        var future = Make("future", Now, true, start: Now.AddDays(1),  end: Now.AddDays(2));
        var past   = Make("past",   Now, true, start: Now.AddDays(-2), end: Now.AddDays(-1));

        var result = ActiveAssignmentSelector.PickNewestActive(
            new[] { future, past }, Now, _ => true);

        Assert.Null(result);
    }

    [Fact]
    public void PickNewestActive_ExcludesWrongDayOfWeek()
    {
        var todayNum = Now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Now.DayOfWeek;
        var otherDay = (todayNum == 1 ? 2 : 1).ToString();

        var a = Make("a", Now, true, days: otherDay);

        Assert.Null(ActiveAssignmentSelector.PickNewestActive(new[] { a }, Now, _ => true));
    }

    [Fact]
    public void PickNewestActive_Empty_ReturnsNull()
    {
        Assert.Null(ActiveAssignmentSelector.PickNewestActive(
            Array.Empty<PublishedAssignmentIndex>(), Now, _ => true));
    }

    [Fact]
    public void IsActive_TrueWithinWindowAndDay()
    {
        Assert.True(ActiveAssignmentSelector.IsActive(Make("a", Now, true), Now));
    }

    [Fact]
    public void IsActive_FalseAfterScheduleEnd()
    {
        var a = Make("a", Now, true, start: Now.AddDays(-2), end: Now.AddSeconds(-1));
        Assert.False(ActiveAssignmentSelector.IsActive(a, Now));
    }
}
