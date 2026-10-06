using System.Globalization;
using NewsCentral.Localization;

namespace NewsCentral.Services;

/// <summary>
/// L10N-1: resolves the effective UI language. Captures Windows' own UI culture once, at
/// construction (i.e. at app startup, before anything in-process can have changed it), so the
/// "Windows default (…)" label and the Windows-fallback mapping in <see cref="UiLanguageResolver"/>
/// always reflect the machine's actual setting rather than whatever this service last applied.
///
/// L10N-1.1: <see cref="CurrentUiCulture"/> is held as explicit STATE on this singleton, not read
/// from the ambient <see cref="CultureInfo.CurrentUICulture"/>. In Blazor Hybrid that ambient value
/// is backed by an <c>AsyncLocal</c>, so setting it inside an event handler or an async method
/// (a login call, a Settings page change handler) only affects that one async flow and reverts the
/// moment it completes — which is why the UI never actually changed language under the ambient
/// approach. <see cref="UiCultureStringLocalizer"/> reads <see cref="CurrentUiCulture"/> directly
/// and scopes it for the duration of each string lookup, so rendering no longer depends on which
/// thread/async-flow is doing the rendering.
///
/// Touches ONLY UI-culture state — never <see cref="CultureInfo.CurrentCulture"/> or
/// <see cref="CultureInfo.DefaultThreadCurrentCulture"/>, so date/number formatting keeps following
/// Windows regional settings regardless of the chosen UI language.
/// </summary>
public sealed class UiLanguageService
{
    private readonly CultureInfo _windowsUiCulture;
    private volatile CultureInfo _currentUiCulture;

    public UiLanguageService()
    {
        _windowsUiCulture = CultureInfo.CurrentUICulture;
        _currentUiCulture = UiLanguageResolver.Resolve(null, _windowsUiCulture);
    }

    /// <summary>The UI language Windows itself is set to, resolved to one of the four supported languages (English if unsupported).</summary>
    public UiLanguage WindowsLanguage => UiLanguageResolver.ResolveLanguage(null, _windowsUiCulture);

    /// <summary>
    /// The language every localizer lookup actually renders in — set by <see cref="Apply"/>.
    /// Thread-safe (a volatile reference swap); there is no partial/torn read.
    /// </summary>
    public CultureInfo CurrentUiCulture => _currentUiCulture;

    /// <summary>
    /// Resolves <paramref name="preferred"/> (a user's stored <c>PreferredUiLanguage</c>, or null
    /// to follow Windows) and makes it the effective UI language.
    /// </summary>
    public void Apply(string? preferred)
    {
        var culture = UiLanguageResolver.Resolve(preferred, _windowsUiCulture);

        _currentUiCulture = culture;

        // Convenience only for any BRAND NEW thread spun up after this call — nothing in this
        // app relies on it for correctness; see CurrentUiCulture above.
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
