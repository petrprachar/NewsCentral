using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace NewsCentral.Configuration;

public sealed class RegistryConfigurationSource : IConfigurationSource
{
    private readonly string _company;
    private readonly string _applicationName;

    public RegistryConfigurationSource(string company, string applicationName)
    {
        _company = company;
        _applicationName = applicationName;
    }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new RegistryConfigurationProvider(_company, _applicationName);
}

public sealed class RegistryConfigurationProvider : ConfigurationProvider
{
    private readonly string _company;
    private readonly string _applicationName;

    internal RegistryConfigurationProvider(string company, string applicationName)
    {
        _company = company;
        _applicationName = applicationName;
    }

    public override void Load()
    {
        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
            LoadFromRegistry();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void LoadFromRegistry()
    {
        try
        {
            using var rootKey = Registry.LocalMachine.OpenSubKey(
                $@"Software\{_company}\{_applicationName}");
            if (rootKey is null) return;
            WalkKey(rootKey, prefix: "");
        }
        catch { }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void WalkKey(RegistryKey key, string prefix)
    {
        foreach (var valueName in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(valueName)) continue;

            var configKey = string.IsNullOrEmpty(prefix)
                ? valueName
                : $"{prefix}:{valueName}";

            string? value = null;
            try
            {
                var kind = key.GetValueKind(valueName);
                if (kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString)
                {
                    value = key.GetValue(valueName) as string;
                }
                else if (kind == RegistryValueKind.DWord && key.GetValue(valueName) is int dword)
                {
                    // 0 → "False", 1 → "True" for bool binding; larger values stored as numeric
                    // string for int binding. Interpretation is left to the configuration binder.
                    value = dword switch
                    {
                        0 => "False",
                        1 => "True",
                        _ => dword.ToString()
                    };
                }
            }
            catch { }

            if (value is not null)
                Data[configKey] = value;
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            if (string.IsNullOrEmpty(prefix) &&
                subKeyName.Equals("teams", StringComparison.OrdinalIgnoreCase))
            {
                LoadTeamsSubKey(key, subKeyName);
                continue;
            }

            try
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey is null) continue;

                var subPrefix = string.IsNullOrEmpty(prefix)
                    ? subKeyName
                    : $"{prefix}:{subKeyName}";

                WalkKey(subKey, subPrefix);
            }
            catch { }
        }
    }

    // teams\ subkey: value names are team folder names; data is ignored.
    // Exposed as teams:0, teams:1, … so GetSection("teams").GetChildren() returns one per team.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void LoadTeamsSubKey(RegistryKey parentKey, string subKeyName)
    {
        try
        {
            using var teamsKey = parentKey.OpenSubKey(subKeyName);
            if (teamsKey is null) return;

            var index = 0;
            foreach (var valueName in teamsKey.GetValueNames())
            {
                if (!string.IsNullOrWhiteSpace(valueName))
                    Data[$"teams:{index++}"] = valueName;
            }
        }
        catch { }
    }
}

public static class RegistryConfigurationExtensions
{
    public static IConfigurationBuilder AddRegistryOverrides(
        this IConfigurationBuilder builder, string company, string applicationName)
    {
        builder.Add(new RegistryConfigurationSource(company, applicationName));
        return builder;
    }
}
