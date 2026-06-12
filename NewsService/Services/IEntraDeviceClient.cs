namespace NewsService.Services;

/// <summary>
/// Seam over <see cref="EntraDeviceClient"/> so the Entra resolution orchestrator can be
/// unit-tested offline with a fake (no Graph call, no credentials).
/// </summary>
public interface IEntraDeviceClient
{
    Task<EntraDeviceFetch> FetchAsync(string deviceId, CancellationToken ct);
}
