using NewsService.Configuration;
using NewsService.Services;
using Xunit;

namespace NewsService.Tests;

/// <summary>
/// Covers AzureProxyTransportFactory — the optional shared WinHTTP transport toggle. OFF (default) must
/// instantiate nothing (cross-platform safe); ON builds both transports (Windows-only — WinHttpHandler).
/// </summary>
public sealed class AzureProxyTransportFactoryTests
{
    [Fact]
    public void ToggleOff_BothTransportsNull()
    {
        var factory = new AzureProxyTransportFactory(new AzureBlobSection { UseWinHttpProxy = false });

        Assert.Null(factory.AzureTransport);
        Assert.Null(factory.GraphHttpClient);
    }

    [Fact]
    public void ToggleOn_BothTransportsNonNull()
    {
        // WinHttpHandler is Windows-only; the ON path can only be exercised on Windows.
        if (!OperatingSystem.IsWindows()) return;

        var factory = new AzureProxyTransportFactory(new AzureBlobSection { UseWinHttpProxy = true });

        Assert.NotNull(factory.AzureTransport);
        Assert.NotNull(factory.GraphHttpClient);
    }
}
