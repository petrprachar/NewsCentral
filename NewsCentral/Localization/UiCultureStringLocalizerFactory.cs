using Microsoft.Extensions.Localization;
using NewsCentral.Services;

namespace NewsCentral.Localization;

/// <summary>
/// L10N-1.1: decorates the framework's <see cref="IStringLocalizerFactory"/> (normally
/// <c>ResourceManagerStringLocalizerFactory</c>) so every <see cref="IStringLocalizer"/> it hands
/// out — including the ones backing every <c>IStringLocalizer&lt;T&gt;</c>, since
/// <c>StringLocalizer&lt;T&gt;</c> itself calls <c>factory.Create(typeof(T))</c> — resolves strings
/// against <see cref="UiLanguageService.CurrentUiCulture"/> instead of the ambient UI culture.
/// Registered in <c>MauiProgram.cs</c> AFTER <c>AddLocalization</c>, so this is the last
/// registration and therefore the one every consumer actually resolves.
/// </summary>
public sealed class UiCultureStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly IStringLocalizerFactory _inner;
    private readonly UiLanguageService _uiLanguageService;

    public UiCultureStringLocalizerFactory(IStringLocalizerFactory inner, UiLanguageService uiLanguageService)
    {
        _inner = inner;
        _uiLanguageService = uiLanguageService;
    }

    public IStringLocalizer Create(Type resourceSource) =>
        new UiCultureStringLocalizer(_inner.Create(resourceSource), _uiLanguageService);

    public IStringLocalizer Create(string baseName, string location) =>
        new UiCultureStringLocalizer(_inner.Create(baseName, location), _uiLanguageService);
}
