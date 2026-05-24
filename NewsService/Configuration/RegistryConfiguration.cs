using Microsoft.Win32;

namespace NewsService.Configuration;

/// <summary>
/// Reads registry overrides from HKLM\Software\[Company]\[ApplicationName].
/// Registry values take precedence over appsettings.json.
/// </summary>
public sealed class RegistryConfiguration
{
    private readonly string _keyPath;

    public RegistryConfiguration(ServiceConfiguration config)
    {
        _keyPath = $@"Software\{config.Company}\{config.ApplicationName}";
    }

    /// <summary>Team folder names configured for this machine (semicolon-separated in registry).</summary>
    public string[] GetTeams()
    {
        var raw = ReadString("teams");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Returns "Share" or "Azure", or null if not set in registry.</summary>
    public string? GetStorageMode() => ReadString("StorageMode");

    /// <summary>Returns the registry poll interval override, or null if absent.</summary>
    public int? GetPollIntervalSeconds() => ReadDword("PollIntervalSeconds");

    public bool GetAzureUploadEnabled() => (ReadDword("AzureUploadEnabled") ?? 0) != 0;

    private string? ReadString(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(_keyPath);
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }

    private int? ReadDword(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(_keyPath);
            return key?.GetValue(name) is int v ? v : null;
        }
        catch { return null; }
    }
}
