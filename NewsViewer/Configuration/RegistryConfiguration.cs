using Microsoft.Win32;

namespace NewsViewer.Configuration;

public sealed class RegistryConfiguration
{
    private readonly string _keyPath;

    public RegistryConfiguration(ViewerConfiguration config)
    {
        _keyPath = $@"Software\{config.Company}\{config.ApplicationName}";
    }

    public string[] GetTeams()
    {
        var raw = ReadString("teams");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private string? ReadString(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(_keyPath);
            return key?.GetValue(name) as string;
        }
        catch { return null; }
    }
}
