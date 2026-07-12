namespace NewsCentral.Models;

/// <summary>
/// Pure helpers for the once-per-logical-day display gate. A "logical day" lets a shift that
/// crosses midnight count as a single day: the boundary rolls at <c>startHour</c> local time
/// instead of at 00:00, so a worker who unlocks at 01:00 is still on the same logical day as
/// their 22:00 session and is not disturbed twice. Local time, not UTC — shifts are local.
/// </summary>
public static class LogicalDayCalculator
{
    /// <summary>
    /// The logical-day key ("yyyy-MM-dd") for <paramref name="localNow"/> given a day that starts
    /// at <paramref name="startHour"/>. <c>startHour = 0</c> is a strict no-op that reproduces the
    /// calendar day (<c>localNow.ToString("yyyy-MM-dd")</c>, i.e. <c>DateTime.Today</c>).
    /// </summary>
    public static string LogicalDay(DateTime localNow, int startHour)
        => localNow.AddHours(-startHour).ToString("yyyy-MM-dd");

    /// <summary>
    /// Clamps a configured start hour to the valid 0..23 range. An out-of-range value degrades to
    /// 0 (the calendar-day default) rather than throwing — it is not boundary-clamped (25 → 0, not 23).
    /// </summary>
    public static int NormalizeStartHour(int startHour)
        => startHour is >= 0 and <= 23 ? startHour : 0;
}
