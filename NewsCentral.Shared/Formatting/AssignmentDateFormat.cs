using System.Globalization;

namespace NewsCentral.Formatting;

/// <summary>
/// Culture-driven date rendering for the Assignments view. Formats via the supplied culture
/// (callers pass <see cref="CultureInfo.CurrentCulture"/>) instead of hand-assembling an
/// English-ordered pattern — so on cs-CZ a date renders as <c>15.06.2026</c>, not a Czech month
/// token in an English layout.
/// </summary>
public static class AssignmentDateFormat
{
    /// <summary>Whole-day date, culture short date (cs-CZ → <c>dd.MM.yyyy</c>).</summary>
    public static string DateOnly(DateTime date, CultureInfo culture) =>
        date.ToString("d", culture);

    /// <summary>
    /// A UTC timestamp shown as the stored UTC value with a <c>UTC</c> suffix (cs-CZ →
    /// <c>15.06.2026 12:30 UTC</c>). The value is relabelled <see cref="DateTimeKind.Utc"/> and
    /// formatted with <see cref="DateTime.ToString(string, IFormatProvider)"/> — never
    /// <c>ToLocalTime</c> — so the displayed components are the stored ones with no time-zone shift,
    /// regardless of the incoming <see cref="DateTime.Kind"/>.
    /// </summary>
    public static string UtcTimestamp(DateTime date, CultureInfo culture)
    {
        var utc = DateTime.SpecifyKind(date, DateTimeKind.Utc);
        return $"{utc.ToString("d", culture)} {utc.ToString("HH:mm", culture)} UTC";
    }
}
