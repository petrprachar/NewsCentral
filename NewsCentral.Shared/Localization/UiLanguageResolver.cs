using System.Globalization;

namespace NewsCentral.Localization;

/// <summary>
/// L10N-1: pure resolution of the effective UI language — no I/O, no ambient culture reads. The
/// caller supplies the stored preference (<c>User.PreferredUiLanguage</c>, null = follow Windows)
/// and the Windows UI culture captured once at process startup (before anything can have changed
/// it — see <c>UiLanguageService</c>).
/// </summary>
public static class UiLanguageResolver
{
    /// <summary>
    /// <paramref name="preferred"/> is a supported culture name (case-insensitive) → that
    /// language. Otherwise (null, empty, or unsupported — e.g. a stale value from a removed
    /// language) → match <paramref name="windowsUiCulture"/> by its two-letter ISO language
    /// (de-AT → de, fr-CA → fr, es-MX → es, en-GB → en); no match (cs-CZ, ja-JP, …) → English.
    /// </summary>
    public static UiLanguage ResolveLanguage(string? preferred, CultureInfo windowsUiCulture)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var exact = preferred.Trim();
            var match = UiLanguages.All.FirstOrDefault(l =>
                string.Equals(l.CultureName, exact, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        var windowsMatch = UiLanguages.All.FirstOrDefault(l =>
            string.Equals(l.CultureName, windowsUiCulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));

        return windowsMatch ?? UiLanguages.English;
    }

    /// <summary>Same resolution as <see cref="ResolveLanguage"/>, returning the <see cref="CultureInfo"/> to apply.</summary>
    public static CultureInfo Resolve(string? preferred, CultureInfo windowsUiCulture) =>
        new(ResolveLanguage(preferred, windowsUiCulture).CultureName);
}
