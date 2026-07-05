namespace NewsCentral.Configuration;

/// <summary>
/// Pure display helpers for config values (spec §5.1). Redaction lives here, not in the resolver,
/// so the resolver can return raw values for testing. A secret's <em>presence</em> and source are
/// preserved (shown as <c>••• (set)</c> vs <c>(not set)</c>); only the value is hidden. Signing
/// public keys are not secret (IsSecret = false) and are shown in full.
/// </summary>
public static class ConfigValueFormatter
{
    public static string ForDisplay(ConfigKeyDescriptor descriptor, string? rawValue)
    {
        if (rawValue is null) return "(not set)";
        if (rawValue.Length == 0) return "(empty)";
        if (descriptor.IsSecret) return "••• (set)";
        return rawValue;
    }
}
