using System.Diagnostics;
using Microsoft.Win32;

namespace NewsService.Services;

/// <summary>
/// Resolves THIS machine's Azure AD (Entra) device id. This is the one brittle,
/// environment-specific piece of the Entra feature — kept isolated and swappable.
///
/// Primary source: the CloudDomainJoin JoinInfo registry key written by Azure AD join.
/// Fallback: parsing <c>dsregcmd /status</c>. Returns null when neither yields a value.
/// </summary>
public sealed class DeviceIdentityProvider(ILogger<DeviceIdentityProvider> logger)
{
    private const string JoinInfoKey =
        @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo";

    public string? TryGetAzureAdDeviceId()
    {
        return TryFromRegistry() ?? TryFromDsregcmd();
    }

    // ── Primary: CloudDomainJoin JoinInfo ────────────────────────────────────

    private string? TryFromRegistry()
    {
        try
        {
            using var joinInfo = Registry.LocalMachine.OpenSubKey(JoinInfoKey);
            if (joinInfo is null) return null;

            // JoinInfo has one subkey per join, named by the device cert thumbprint.
            foreach (var thumbprint in joinInfo.GetSubKeyNames())
            {
                using var entry = joinInfo.OpenSubKey(thumbprint);
                if (entry?.GetValue("DeviceId") is string deviceId &&
                    !string.IsNullOrWhiteSpace(deviceId))
                {
                    return deviceId.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read DeviceId from CloudDomainJoin JoinInfo.");
        }

        return null;
    }

    // ── Fallback: dsregcmd /status ───────────────────────────────────────────

    private string? TryFromDsregcmd()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "dsregcmd.exe",
                Arguments              = "/status",
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            // Line form: "    DeviceId : 12345678-90ab-cdef-1234-567890abcdef"
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("DeviceId", StringComparison.OrdinalIgnoreCase))
                    continue;

                var colon = trimmed.IndexOf(':');
                if (colon < 0) continue;

                var value = trimmed[(colon + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read DeviceId from dsregcmd /status.");
        }

        return null;
    }
}
