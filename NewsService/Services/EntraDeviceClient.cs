using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Four-state result of one Entra device fetch. <see cref="Attributes"/> and
/// <see cref="DeviceObjectId"/> are populated only when <see cref="Outcome"/> is <c>Found</c>.
/// <c>PermissionDenied</c> (403) is persistent — callers remove promptly, never grace.
/// </summary>
public sealed record EntraDeviceFetch(
    EntraFetchOutcome Outcome,
    IReadOnlyDictionary<string, string?>? Attributes = null,
    string? DeviceObjectId = null)
{
    public static EntraDeviceFetch Found(IReadOnlyDictionary<string, string?> attrs, string? deviceObjectId) =>
        new(EntraFetchOutcome.Found, attrs, deviceObjectId);

    public static readonly EntraDeviceFetch NotFound         = new(EntraFetchOutcome.NotFound);
    public static readonly EntraDeviceFetch PermissionDenied = new(EntraFetchOutcome.PermissionDenied);
    public static readonly EntraDeviceFetch Unreachable      = new(EntraFetchOutcome.Unreachable);
}

public enum EntraFetchOutcome { Found, NotFound, PermissionDenied, Unreachable }

/// <summary>
/// Reads this machine's Entra device object via the Microsoft Graph SDK and returns its
/// extensionAttributes. The <see cref="GraphServiceClient"/> and its credential are built
/// lazily on first use so nothing is constructed when Entra is disabled or creds are absent.
/// Rides the same default .NET HTTP stack as <see cref="AzureBlobRepositoryReader"/> — no
/// app-specific proxy configuration.
/// </summary>
public sealed class EntraDeviceClient(
    AzureBlobSection azureBlob,
    AzureProxyTransportFactory transportFactory,
    ILogger<EntraDeviceClient> logger) : IEntraDeviceClient
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private GraphServiceClient? _graph;

    private GraphServiceClient Graph => _graph ??= BuildGraph();

    private GraphServiceClient BuildGraph()
    {
        var credential = AzureCredentialFactory.Create(azureBlob, transportFactory.AzureTransport);

        // Off (GraphHttpClient == null) → credential-only construction, exactly as before; on → use the
        // shared HttpClient (Graph middleware over the WinHTTP transport).
        return transportFactory.GraphHttpClient is null
            ? new GraphServiceClient(credential, GraphScopes)
            : new GraphServiceClient(transportFactory.GraphHttpClient, credential, GraphScopes);
    }

    public async Task<EntraDeviceFetch> FetchAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            var response = await Graph.Devices.GetAsync(rc =>
            {
                rc.QueryParameters.Filter = $"deviceId eq '{deviceId}'";
                rc.QueryParameters.Select = ["id", "extensionAttributes"];
            }, ct);

            var device = response?.Value?.FirstOrDefault();
            if (device is null)
            {
                logger.LogWarning("Entra device not found for deviceId {DeviceId}.", deviceId);
                return EntraDeviceFetch.NotFound;
            }

            return EntraDeviceFetch.Found(ExtractAttributes(device), device.Id);
        }
        catch (Exception ex)
        {
            // 403 is persistent (missing consent) — surface it distinctly so callers remove promptly
            // and log at Error; 404 is authoritative not-found; transient/other ride the grace window.
            switch (GraphFailureClassifier.Classify(ex))
            {
                case GraphFailureKind.PermissionDenied:
                    logger.LogError(ex,
                        "Entra device query denied (403) for deviceId {DeviceId} — check Device.Read.All admin consent.",
                        deviceId);
                    return EntraDeviceFetch.PermissionDenied;

                case GraphFailureKind.NotFound:
                    logger.LogWarning("Entra device query returned 404 for deviceId {DeviceId}.", deviceId);
                    return EntraDeviceFetch.NotFound;

                default:
                    logger.LogWarning(ex, "Entra device fetch failed (transient) — treated as Unreachable.");
                    return EntraDeviceFetch.Unreachable;
            }
        }
    }

    /// <summary>
    /// Extracts the device's extensionAttributes into the dictionary the resolver expects.
    ///
    /// Decision (intentional, v1.0 stable — beta SDK avoided for production): the v1.0 Graph
    /// Device model has no typed extensionAttributes property, so on <c>$select=extensionAttributes</c>
    /// the value arrives in <see cref="Device.AdditionalData"/> as a Kiota <see cref="UntypedObject"/>.
    /// Reading AdditionalData is Microsoft's documented v1.0 pattern. The UntypedObject→JsonElement
    /// projection below is the ONE line not exercised by offline tests; everything downstream is the
    /// pure, unit-tested <see cref="EntraExtensionAttributeMapper"/>.
    /// </summary>
    private static Dictionary<string, string?> ExtractAttributes(Device device)
    {
        if (device.AdditionalData is null ||
            !device.AdditionalData.TryGetValue("extensionAttributes", out var raw) ||
            raw is not UntypedObject obj)
        {
            return EntraExtensionAttributeMapper.Map(default);   // no attributes → 15 nulls
        }

        // ── ISOLATED SDK→JSON bridge (the only offline-unverifiable line) ──
        var element = JsonSerializer.SerializeToElement(
            obj.GetValue().ToDictionary(kv => kv.Key, kv => kv.Value is UntypedString s ? s.GetValue() : null));

        return EntraExtensionAttributeMapper.Map(element);
    }
}
