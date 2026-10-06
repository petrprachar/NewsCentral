using System.Globalization;
using NewsCentral.Localization;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class UiLanguageResolverTests
{
    private static readonly CultureInfo AnyWindowsCulture = new("en-US");

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    public void Resolve_SupportedCode_ReturnsThatCulture(string code)
    {
        var result = UiLanguageResolver.Resolve(code, AnyWindowsCulture);

        Assert.Equal(code, result.Name);
    }

    [Theory]
    [InlineData("DE")]
    [InlineData("Fr")]
    [InlineData("ES")]
    public void Resolve_SupportedCode_IsCaseInsensitive(string code)
    {
        var result = UiLanguageResolver.Resolve(code, AnyWindowsCulture);

        Assert.Equal(code.ToLowerInvariant(), result.Name);
    }

    [Fact]
    public void Resolve_NullPreferred_DeAt_MapsToGerman()
    {
        var result = UiLanguageResolver.Resolve(null, new CultureInfo("de-AT"));

        Assert.Equal("de", result.Name);
    }

    [Fact]
    public void Resolve_NullPreferred_FrCa_MapsToFrench()
    {
        var result = UiLanguageResolver.Resolve(null, new CultureInfo("fr-CA"));

        Assert.Equal("fr", result.Name);
    }

    [Fact]
    public void Resolve_NullPreferred_EsMx_MapsToSpanish()
    {
        var result = UiLanguageResolver.Resolve(null, new CultureInfo("es-MX"));

        Assert.Equal("es", result.Name);
    }

    [Fact]
    public void Resolve_NullPreferred_EnGb_MapsToEnglish()
    {
        var result = UiLanguageResolver.Resolve(null, new CultureInfo("en-GB"));

        Assert.Equal("en", result.Name);
    }

    [Fact]
    public void Resolve_NullPreferred_CsCz_FallsBackToEnglish()
    {
        var result = UiLanguageResolver.Resolve(null, new CultureInfo("cs-CZ"));

        Assert.Equal("en", result.Name);
    }

    [Fact]
    public void Resolve_NullPreferred_JaJp_FallsBackToEnglish()
    {
        var result = UiLanguageResolver.Resolve(null, new CultureInfo("ja-JP"));

        Assert.Equal("en", result.Name);
    }

    [Fact]
    public void Resolve_EmptyPreferred_TreatedAsNull()
    {
        var result = UiLanguageResolver.Resolve(string.Empty, new CultureInfo("de-AT"));

        Assert.Equal("de", result.Name);
    }

    [Fact]
    public void Resolve_WhitespacePreferred_TreatedAsNull()
    {
        var result = UiLanguageResolver.Resolve("   ", new CultureInfo("fr-CA"));

        Assert.Equal("fr", result.Name);
    }

    [Fact]
    public void Resolve_UnsupportedStoredCode_TreatedAsNull_FallsBackToWindows()
    {
        var result = UiLanguageResolver.Resolve("it", new CultureInfo("es-MX"));

        Assert.Equal("es", result.Name);
    }

    [Fact]
    public void Resolve_UnsupportedStoredCode_NoWindowsMatch_FallsBackToEnglish()
    {
        var result = UiLanguageResolver.Resolve("it", new CultureInfo("ja-JP"));

        Assert.Equal("en", result.Name);
    }

    [Fact]
    public void ResolveLanguage_SupportedCode_ReturnsNativeName()
    {
        var result = UiLanguageResolver.ResolveLanguage("de", AnyWindowsCulture);

        Assert.Equal("Deutsch", result.NativeName);
    }
}
