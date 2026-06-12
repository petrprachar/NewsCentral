using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Serialization;
using NewsService.Configuration;

namespace NewsService.Services;

/// <summary>
/// Tri-state result of one Entra device fetch.
/// <see cref="Attributes"/> is populated only when <see cref="Outcome"/> is <c>Found</c>.
/// </summary>
public sealed record EntraDeviceFetch(
    EntraFetchOutcome Outcome,
    IReadOnlyDictionary<string, string?>? Attributes = null)
{
    public static EntraDeviceFetch Found(IReadOnlyDictionary<string, string?> attrs) =>
        new(EntraFetchOutcome.Found, attrs);

    public static readonly EntraDeviceFetch NotFound    = new(EntraFetchOutcome.NotFound);
    public static readonly EntraDeviceFetch Unreachable = new(EntraFetchOutcome.Unreachable);
}

public enum EntraFetchOutcome { Found, NotFound, Unreachable }

/// <summary>
/// Reads this machine's Entra device object via the Microsoft Graph SDK and returns its
/// extensionAttributes. The <see cref="GraphServiceClient"/> and its credential are built
/// lazily on first use so nothing is constructed when Entra is disabled or creds are absent.
/// Rides the same default .NET HTTP stack as <see cref="AzureBlobRepositoryReader"/> — no
/// app-specific proxy configuration.
/// </summary>
public sealed class EntraDeviceClient(
    AzureBlobSection azureBlob,
    ILogger<EntraDeviceClient> logger)
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private GraphServiceClient? _graph;

    private GraphServiceClient Graph =>
        _graph ??= new GraphServiceClient(AzureCredentialFactory.Create(azureBlob), GraphScopes);

    public async Task<EntraDeviceFetch> FetchAsync(string deviceId, CancellationToken ct)
    {
        try
        {
            var response = await Graph.Devices.GetAsync(rc =>
            {
                rc.QueryParameters.Filter = $"deviceId eq '{deviceId}'";
                rc.QueryParameters.Select = ["extensionAttributes"];
            }, ct);

            var device = response?.Value?.FirstOrDefault();
            if (device is null)
            {
                logger.LogWarning("Entra device not found for deviceId {DeviceId}.", deviceId);
                return EntraDeviceFetch.NotFound;
            }

            return EntraDeviceFetch.Found(MapAttributes(device));
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            logger.LogWarning("Entra device query returned 404 for deviceId {DeviceId}.", deviceId);
            return EntraDeviceFetch.NotFound;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Entra device fetch was cancelled or timed out.");
            return EntraDeviceFetch.Unreachable;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Entra device fetch failed (auth/network) — treated as Unreachable.");
            return EntraDeviceFetch.Unreachable;
        }
    }

    /// <summary>
    /// Maps the device's extensionAttributes (ExtensionAttribute1..15) into the
    /// "extensionAttribute1".."extensionAttribute15" dictionary the resolver expects.
    ///
    /// The v1.0 Graph SDK Device model does not surface extensionAttributes as a typed
    /// property, so when <c>$select=extensionAttributes</c> is requested the value arrives in
    /// <see cref="Device.AdditionalData"/> as a Kiota <see cref="UntypedObject"/> whose keys are
    /// already "extensionAttribute1".."extensionAttribute15". All 15 slots are pre-seeded null so
    /// absent attributes resolve to null.
    /// </summary>
    private static Dictionary<string, string?> MapAttributes(Device device)
    {
        var result = new Dictionary<string, string?>(15);
        for (var i = 1; i <= 15; i++)
            result[$"extensionAttribute{i}"] = null;

        if (device.AdditionalData is null ||
            !device.AdditionalData.TryGetValue("extensionAttributes", out var raw) ||
            raw is not UntypedObject obj)
        {
            return result;
        }

        foreach (var (key, node) in obj.GetValue())
        {
            result[key] = node is UntypedString s ? s.GetValue() : null;
        }

        return result;
    }
}
