using System.Globalization;
using NewsCentral.Localization;
using Xunit;

namespace NewsCentral.Shared.Tests;

public sealed class UiCultureScopeTests
{
    [Fact]
    public void WithUiCulture_RunsFuncUnderRequestedCulture()
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");

            var observed = UiCultureScope.WithUiCulture(new CultureInfo("de"), () => CultureInfo.CurrentUICulture.Name);

            Assert.Equal("de", observed);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void WithUiCulture_RestoresPreviousCultureAfterNormalReturn()
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fr");

            UiCultureScope.WithUiCulture(new CultureInfo("es"), () => 0);

            Assert.Equal("fr", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void WithUiCulture_RestoresPreviousCultureAfterException()
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en");

            Assert.Throws<InvalidOperationException>(() =>
                UiCultureScope.WithUiCulture<int>(new CultureInfo("de"), () => throw new InvalidOperationException("boom")));

            Assert.Equal("en", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void WithUiCulture_NeverChangesCurrentCulture()
    {
        var originalUi = CultureInfo.CurrentUICulture;
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");

            UiCultureScope.WithUiCulture(new CultureInfo("de"), () => 0);

            Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUi;
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void WithUiCulture_ReturnsFuncResult()
    {
        var result = UiCultureScope.WithUiCulture(new CultureInfo("en"), () => "hello");

        Assert.Equal("hello", result);
    }
}
