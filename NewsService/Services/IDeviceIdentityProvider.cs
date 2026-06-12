namespace NewsService.Services;

/// <summary>
/// Seam over <see cref="DeviceIdentityProvider"/> so the Entra resolution orchestrator can be
/// unit-tested offline without a domain-joined machine.
/// </summary>
public interface IDeviceIdentityProvider
{
    string? TryGetAzureAdDeviceId();
}
