using System.Globalization;
using NewsCentral.Localization;

namespace NewsCentral.Services;

/// <summary>
/// L10N-1: applies the effective UI language. Captures Windows' own UI culture once, at
/// construction (i.e. at app startup, before anything in-process can have changed it), so the
/// "Windows default (…)" label and the Windows-fallback mapping in <see cref="UiLanguageResolver"/>
/// always reflect the machine's actual setting rather than whatever this service last applied.
///
/// Touches ONLY <see cref="CultureInfo.CurrentUICulture"/> and
/// <see cref="CultureInfo.DefaultThreadCurrentUICulture"/> — never <see cref="CultureInfo.CurrentCulture"/>
/// or <see cref="CultureInfo.DefaultThreadCurrentCulture"/>, so date/number formatting keeps
/// following Windows regional settings regardless of the chosen UI language.
/// </summary>
public sealed class UiLanguageService
{
    private readonly CultureInfo _windowsUiCulture;

    public UiLanguageService()
    {
        _windowsUiCulture = CultureInfo.CurrentUICulture;
    }

    /// <summary>The UI language Windows itself is set to, resolved to one of the four supported languages (English if unsupported).</summary>
    public UiLanguage WindowsLanguage => UiLanguageResolver.ResolveLanguage(null, _windowsUiCulture);

    /// <summary>
    /// Resolves <paramref name="preferred"/> (a user's stored <c>PreferredUiLanguage</c>, or null
    /// to follow Windows) and applies it as the current UI culture.
    /// </summary>
    public void Apply(string? preferred)
    {
        var culture = UiLanguageResolver.Resolve(preferred, _windowsUiCulture);

        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
