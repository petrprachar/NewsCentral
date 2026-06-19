using System.Net.Http;
using Azure.Core.Pipeline;
using Microsoft.Graph;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Optional shared transport that routes ALL NewsService cloud SDK traffic — Azure Blob (Azure.Core)
/// and Microsoft Graph — through <see cref="WinHttpHandler"/> with
/// <see cref="WindowsProxyUsePolicy.UseWinHttpProxy"/>. Under Local System the default .NET HttpClient
/// resolves its proxy via WinINet (per-user, unreliable when no user profile is loaded), whereas the
/// machine WinHTTP proxy (<c>netsh winhttp</c> / WPAD) is the proven path the Intune client and Windows
/// Update use. This factory makes NewsService ride that same path — no per-app proxy config.
///
/// Governed by <c>AzureBlob:UseWinHttpProxy</c>. Default OFF: both members are <c>null</c>, nothing is
/// instantiated (safe on non-Windows build/test agents), and every SDK is constructed exactly as before.
/// Singleton — one set of handlers per process; not disposed per call.
/// </summary>
public sealed class AzureProxyTransportFactory
{
    /// <summary>Azure.Core transport shared by the credential + blob reader; <c>null</c> when the toggle is off.</summary>
    public HttpClientTransport? AzureTransport { get; }

    /// <summary>
    /// HttpClient carrying Graph's default middleware (retry/redirect/throttling) over the WinHTTP
    /// transport, shared by the device + group Graph clients; <c>null</c> when the toggle is off.
    /// </summary>
    public HttpClient? GraphHttpClient { get; }

    public AzureProxyTransportFactory(AzureBlobSection config)
    {
        if (!config.UseWinHttpProxy)
            return;   // OFF — leave both null; instantiate nothing (no WinHttpHandler on non-Windows agents).

        // WinHttpHandler is Windows-only and is constructed ONLY in this branch.
        AzureTransport  = new HttpClientTransport(new HttpClient(NewWinHttpHandler()));

        // GraphClientFactory.Create wraps the WinHTTP final handler in Graph's default middleware pipeline.
        GraphHttpClient = GraphClientFactory.Create(finalHandler: NewWinHttpHandler());
    }

    private static WinHttpHandler NewWinHttpHandler() =>
        new() { WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinHttpProxy };
}
