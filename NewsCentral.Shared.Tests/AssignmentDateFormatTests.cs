using System.Globalization;
using NewsCentral.Formatting;

namespace NewsCentral.Shared.Tests;

public sealed class AssignmentDateFormatTests
{
    private static readonly CultureInfo Cs = CultureInfo.GetCultureInfo("cs-CZ");

    [Fact]
    public void DateOnly_CsCz_IsDdMmYyyy()
    {
        var d = new DateTime(2026, 6, 15);
        Assert.Equal("15.06.2026", AssignmentDateFormat.DateOnly(d, Cs));
    }

    [Fact]
    public void UtcTimestamp_CsCz_ShowsDateTimeWithUtcSuffix()
    {
        var d = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        Assert.Equal("15.06.2026 12:30 UTC", AssignmentDateFormat.UtcTimestamp(d, Cs));
    }

    [Fact]
    public void UtcTimestamp_AlwaysEndsWithUtc()
    {
        var d = new DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc);
        Assert.EndsWith(" UTC", AssignmentDateFormat.UtcTimestamp(d, Cs));
    }

    [Fact]
    public void UtcTimestamp_DoesNotConvertToLocal_RegardlessOfKind()
    {
        // Same wall-clock components with different Kinds must yield the identical string —
        // proving the formatter never shifts time zones (no ToLocalTime).
        var utc         = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Utc);
        var unspecified = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Unspecified);
        var local       = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Local);

        var expected = "15.06.2026 12:30 UTC";
        Assert.Equal(expected, AssignmentDateFormat.UtcTimestamp(utc, Cs));
        Assert.Equal(expected, AssignmentDateFormat.UtcTimestamp(unspecified, Cs));
        Assert.Equal(expected, AssignmentDateFormat.UtcTimestamp(local, Cs));
    }
}
