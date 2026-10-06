using System.Linq;
using Microsoft.Extensions.Localization;
using NewsCentral.Services;

namespace NewsCentral.Localization;

/// <summary>
/// L10N-1.1: decorates a framework <see cref="IStringLocalizer"/> so every lookup resolves against
/// <see cref="UiLanguageService.CurrentUiCulture"/> — the user's EXPLICITLY chosen language — rather
/// than the ambient <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>, which Blazor
/// Hybrid's AsyncLocal semantics make unreliable (see <see cref="UiLanguageService"/>'s own remarks).
/// </summary>
internal sealed class UiCultureStringLocalizer : IStringLocalizer
{
    private readonly IStringLocalizer _inner;
    private readonly UiLanguageService _uiLanguageService;

    public UiCultureStringLocalizer(IStringLocalizer inner, UiLanguageService uiLanguageService)
    {
        _inner = inner;
        _uiLanguageService = uiLanguageService;
    }

    public LocalizedString this[string name] =>
        UiCultureScope.WithUiCulture(_uiLanguageService.CurrentUiCulture, () => _inner[name]);

    public LocalizedString this[string name, params object[] arguments] =>
        UiCultureScope.WithUiCulture(_uiLanguageService.CurrentUiCulture, () => _inner[name, arguments]);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        // Materialize INSIDE the scope — GetAllStrings can return a lazy sequence, and enumerating
        // it after the scope's finally block has already restored the previous culture would defeat
        // the whole point.
        UiCultureScope.WithUiCulture(_uiLanguageService.CurrentUiCulture,
            () => _inner.GetAllStrings(includeParentCultures).ToList());
}
