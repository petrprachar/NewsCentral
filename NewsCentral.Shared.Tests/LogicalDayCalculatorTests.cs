using NewsCentral.Models;

namespace NewsCentral.Shared.Tests;

public class LogicalDayCalculatorTests
{
    // startHour = 0 is a strict no-op: identical to the calendar day (DateTime.Today) for any time.
    [Theory]
    [InlineData(2026, 1, 1, 0, 0, 0)]
    [InlineData(2026, 1, 1, 8, 30, 0)]
    [InlineData(2026, 1, 1, 23, 59, 59)]
    [InlineData(2026, 7, 12, 12, 0, 0)]
    public void StartHourZero_IsCalendarDay(int y, int mo, int d, int h, int mi, int s)
    {
        var now = new DateTime(y, mo, d, h, mi, s);
        Assert.Equal(now.ToString("yyyy-MM-dd"), LogicalDayCalculator.LogicalDay(now, 0));
    }

    // Night shift (startHour = 5): a session at 22:00 and an unlock at 01:00 the next calendar day
    // are the SAME logical day; 22:00 the following evening is the NEXT one.
    [Fact]
    public void NightShift_AcrossMidnight_StaysWithinOneLogicalDay()
    {
        const int startHour = 5;
        var evening     = new DateTime(2026, 1, 1, 22, 0, 0);
        var earlyNext   = new DateTime(2026, 1, 2, 1, 0, 0);
        var nextEvening = new DateTime(2026, 1, 2, 22, 0, 0);

        var d1 = LogicalDayCalculator.LogicalDay(evening, startHour);
        var d2 = LogicalDayCalculator.LogicalDay(earlyNext, startHour);
        var d3 = LogicalDayCalculator.LogicalDay(nextEvening, startHour);

        Assert.Equal(d1, d2);       // 22:00 Jan 1 and 01:00 Jan 2 share a logical day
        Assert.NotEqual(d1, d3);    // 22:00 Jan 2 is the next logical day
    }

    // Exact boundary (startHour = 5): 04:59:59 is still the previous logical day; 05:00:00 rolls over.
    [Fact]
    public void Boundary_AtStartHour_SwitchesLogicalDay()
    {
        const int startHour = 5;
        var justBefore = new DateTime(2026, 1, 2, 4, 59, 59);
        var atStart    = new DateTime(2026, 1, 2, 5, 0, 0);

        Assert.Equal("2026-01-01", LogicalDayCalculator.LogicalDay(justBefore, startHour));
        Assert.Equal("2026-01-02", LogicalDayCalculator.LogicalDay(atStart, startHour));
        Assert.NotEqual(
            LogicalDayCalculator.LogicalDay(justBefore, startHour),
            LogicalDayCalculator.LogicalDay(atStart, startHour));
    }

    // Clamping: values below 0 and above 23 degrade to 0 (not boundary-clamped: 24 -> 0, not 23).
    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(24)]
    [InlineData(99)]
    public void NormalizeStartHour_OutOfRange_DegradesToZero(int raw)
    {
        Assert.Equal(0, LogicalDayCalculator.NormalizeStartHour(raw));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(23)]
    public void NormalizeStartHour_InRange_IsUnchanged(int raw)
    {
        Assert.Equal(raw, LogicalDayCalculator.NormalizeStartHour(raw));
    }
}
