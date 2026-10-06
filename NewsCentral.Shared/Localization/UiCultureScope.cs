using System.Globalization;

namespace NewsCentral.Localization;

/// <summary>
/// L10N-1.1: scopes <see cref="CultureInfo.CurrentUICulture"/> to a specific culture for the
/// duration of a synchronous call, then restores whatever was there before — including when the
/// call throws. Exists so a localizer can resolve strings in an EXPLICITLY chosen language
/// (<c>UiLanguageService.CurrentUiCulture</c> in NewsCentral) rather than relying on the ambient
/// culture, which in Blazor Hybrid is backed by an <c>AsyncLocal</c> and reverts the moment the
/// async flow that set it (a login handler, a Settings page event handler) completes.
///
/// Never touches <see cref="CultureInfo.CurrentCulture"/> — formatting (dates, numbers) must keep
/// following Windows regional settings regardless of the chosen UI language.
/// </summary>
public static class UiCultureScope
{
    public static T WithUiCulture<T>(CultureInfo culture, Func<T> func)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return func();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
