using System.Text.Json;

namespace NewsCentral.Configuration;

public enum EnvironmentSettingsSource
{
    /// <summary>A valid config/environment.json was found and is authoritative.</summary>
    EnvironmentFile,

    /// <summary>No config/environment.json exists — effective settings come from this machine.</summary>
    MachineDefaults,

    /// <summary>
    /// config/environment.json exists but is unparseable, has an unsupported schemaVersion, or
    /// fails validation. Deliberately never falls back to machine defaults, which could publish
    /// to the wrong target — distribution becomes unavailable instead.
    /// </summary>
    Invalid
}

/// <summary>
/// <see cref="EnvironmentSettingsResolver.Resolve"/>'s result. <see cref="Settings"/> is null only
/// when <see cref="Source"/> is <see cref="EnvironmentSettingsSource.Invalid"/>.
/// <see cref="DistributionFingerprint"/> is not populated by <see cref="EnvironmentSettingsResolver.Resolve"/>
/// itself (that method has no DataPath to compute it from) — callers that have one combine it in,
/// typically via <c>result with { DistributionFingerprint = Fingerprint(result.Settings, dataPath) }</c>.
/// </summary>
public sealed record EffectiveEnvironmentSettings(
    EnvironmentSettingsSource Source,
    EnvironmentSettings? Settings,
    string? Error,
    string? DistributionFingerprint);

/// <summary>
/// Pure, static, no I/O — parsing, validation and fingerprinting for per-environment distribution
/// settings. Reading/writing config/environment.json and caching live in
/// NewsCentral.Services.EnvironmentSettingsService (M3a commit 2); this type never touches disk.
/// </summary>
public static class EnvironmentSettingsResolver
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Resolves the effective settings from the raw file contents (null when the file is absent)
    /// and this machine's defaults. Never throws — a malformed or invalid file produces
    /// <see cref="EnvironmentSettingsSource.Invalid"/>, never a fallback to <paramref name="machineDefaults"/>.
    /// </summary>
    public static EffectiveEnvironmentSettings Resolve(string? fileJson, EnvironmentSettings machineDefaults)
    {
        if (fileJson == null)
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.MachineDefaults, machineDefaults, Error: null, DistributionFingerprint: null);

        EnvironmentSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EnvironmentSettings>(fileJson, EnvironmentSettingsJson.Options);
        }
        catch (JsonException ex)
        {
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.Invalid, Settings: null, Error: ex.Message, DistributionFingerprint: null);
        }

        if (parsed == null)
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.Invalid, Settings: null,
                Error: "environment.json parsed to an empty document.", DistributionFingerprint: null);

        var errors = Validate(parsed);
        if (errors.Count > 0)
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.Invalid, Settings: null,
                Error: string.Join("; ", errors), DistributionFingerprint: null);

        return new EffectiveEnvironmentSettings(
            EnvironmentSettingsSource.EnvironmentFile, parsed, Error: null, DistributionFingerprint: null);
    }

    /// <summary>
    /// Validation errors for a parsed <see cref="EnvironmentSettings"/>. Empty means valid.
    /// A bad SchemaVersion short-circuits — the rest of the document's shape is meaningless
    /// against a schema this build doesn't understand.
    /// </summary>
    public static IReadOnlyList<string> Validate(EnvironmentSettings settings)
    {
        var errors = new List<string>();

        if (settings.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add(settings.SchemaVersion > CurrentSchemaVersion
                ? $"environment.json was written by a newer version of NewsCentral (schemaVersion {settings.SchemaVersion})."
                : $"environment.json has an unsupported schemaVersion ({settings.SchemaVersion}); expected {CurrentSchemaVersion}.");
            return errors;
        }

        if (settings.Distribution.Enabled)
        {
            var mode = settings.Distribution.Mode;
            var isLocal = string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase);
            var isAzure = string.Equals(mode, "AzureBlob", StringComparison.OrdinalIgnoreCase);

            if (!isLocal && !isAzure)
            {
                errors.Add($"Distribution.Mode must be \"Local\" or \"AzureBlob\" (was \"{mode}\").");
            }
            else if (isAzure)
            {
                var az = settings.Distribution.AzureBlob;
                if (string.IsNullOrWhiteSpace(az.AccountName))
                    errors.Add("Distribution.AzureBlob.AccountName is required when Mode is \"AzureBlob\".");
                if (string.IsNullOrWhiteSpace(az.ClientId))
                    errors.Add("Distribution.AzureBlob.ClientId is required when Mode is \"AzureBlob\".");
                if (string.IsNullOrWhiteSpace(az.ContainerName))
                    errors.Add("Distribution.AzureBlob.ContainerName is required when Mode is \"AzureBlob\".");
            }
            // Local mode: LocalPath may be empty — falls back to DataPath\_distribution (see Fingerprint).
        }

        return errors;
    }

    /// <summary>
    /// A stable identifier for where distribution publishes to, or null when distribution is
    /// disabled. Two settings with the same effective target (even differently cased or with a
    /// trailing separator, for Local) produce equal fingerprints — this is what later milestones
    /// compare to detect two environments pointed at the same place.
    /// </summary>
    public static string? Fingerprint(EnvironmentSettings settings, string dataPath)
    {
        if (!settings.Distribution.Enabled)
            return null;

        if (string.Equals(settings.Distribution.Mode, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            var az = settings.Distribution.AzureBlob;
            return $"blob:{az.AccountName}/{az.ContainerName}".ToLowerInvariant();
        }

        var root = string.IsNullOrWhiteSpace(settings.Distribution.LocalPath)
            ? Path.Combine(dataPath, "_distribution")
            : settings.Distribution.LocalPath;

        return $"local:{Path.GetFullPath(root)}".ToLowerInvariant();
    }

    /// <summary>
    /// Maps this machine's existing appsettings.json/registry values (Storage:*, AzureBlob:*) into
    /// an EnvironmentSettings shape — the defaults used when no environment.json exists, and what
    /// "Save to environment" writes verbatim.
    /// </summary>
    public static EnvironmentSettings FromMachineConfiguration(AppConfiguration config) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        DisplayName = null,
        Distribution = new DistributionSettings
        {
            Enabled = config.EnableBlobDistribution,
            Mode = config.DistributionMode,
            LocalPath = string.IsNullOrEmpty(config.LocalDistributionPath) ? null : config.LocalDistributionPath,
            AzureBlob = new AzureBlobSettings
            {
                TenantId = config.AzureBlobTenantId,
                ClientId = config.AzureBlobClientId,
                AccountName = config.AzureBlobAccountName,
                ContainerName = config.AzureBlobContainerName
            }
        },
        ModifiedBy = null,
        ModifiedUtc = null
    };
}
