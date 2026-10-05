namespace NewsCentral.Components.Shared;

/// <summary>
/// UI-1: a stored timestamp is UTC almost everywhere in this codebase (LastLogin, GeneratedAt,
/// ModifiedUtc, …), but several pages formatted it directly with <c>ToString</c> and no
/// <c>ToLocalTime()</c> — displaying UTC wall-clock time as if it were the viewer's local time.
/// Centralizes the fix: a <see cref="DateTimeKind.Unspecified"/> value (the common case after a
/// JSON round-trip through a serializer that doesn't preserve Kind) is treated as UTC, since that
/// is what every caller of this helper actually stores; <see cref="DateTimeKind.Utc"/> is used as
/// is; <see cref="DateTimeKind.Local"/> is kept as is. <see cref="DateTime.ToLocalTime"/> then
/// converts to the viewer's local time for display.
/// </summary>
public static class DisplayTime
{
    public static string Local(DateTime? utc, string format)
    {
        if (utc == null)
            return "—";

        var value = utc.Value;
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return value.ToLocalTime().ToString(format);
    }
}
