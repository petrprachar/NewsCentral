using Microsoft.Extensions.Configuration;

namespace NewsCentral.Configuration;

/// <summary>
/// One Group Policy-defined environment entry — a DataPath plus optional distribution overrides,
/// read from either an explicit <c>Environments\{Name}\</c> registry subkey or (for
/// <see cref="IsImplicitDefault"/>) the flat <c>DataPath</c>/<c>Storage\*</c>/<c>AzureBlob\*</c>
/// registry values at the NewsCentral component root. Every override field is null when the
/// policy entry does not set it — see <see cref="EnvironmentSettingsResolver.ApplyPolicy"/>.
/// </summary>
public sealed record PolicyEnvironment(
    string Name,
    string DataPath,
    string? DisplayName,
    bool? EnableBlobDistribution,
    string? DistributionMode,
    string? LocalDistributionPath,
    string? AzureBlobContainerName,
    string? AzureTenantId,
    string? AzureClientId,
    string? AzureAccountName,
    bool IsImplicitDefault);

/// <summary>
/// The full policy-defined environment catalog for this machine, read once at startup from a
/// registry-only <see cref="IConfiguration"/> (never the merged appsettings+registry
/// configuration — see <see cref="EnvironmentCatalogReader.Read"/>).
/// </summary>
public sealed record EnvironmentCatalog(
    IReadOnlyList<PolicyEnvironment> Entries,
    bool AllowUserEnvironments,
    IReadOnlyList<string> Warnings)
{
    public static readonly EnvironmentCatalog Empty =
        new(Array.Empty<PolicyEnvironment>(), AllowUserEnvironments: true, Array.Empty<string>());

    /// <summary>
    /// The entry whose canonical DataPath matches <paramref name="dataPath"/>, or null. Canonical
    /// form (<see cref="EnvironmentPaths.Canonicalize"/>) makes the match case- and
    /// trailing-separator-insensitive.
    /// </summary>
    public PolicyEnvironment? FindByDataPath(string dataPath)
    {
        var canonical = EnvironmentPaths.Canonicalize(dataPath);
        foreach (var entry in Entries)
        {
            if (EnvironmentPaths.Canonicalize(entry.DataPath) == canonical)
                return entry;
        }
        return null;
    }
}

/// <summary>Canonical-path comparison shared by the catalog's dedup/match logic.</summary>
public static class EnvironmentPaths
{
    /// <summary><see cref="Path.GetFullPath"/>, trailing separators trimmed, lower-cased.</summary>
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var full = Path.GetFullPath(path);
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="path"/> is a drive-rooted or UNC absolute path
    /// (<see cref="Path.IsPathFullyQualified(string)"/>) — false for a relative path, an empty
    /// string, or null. Used to reject relative DataPath values wherever one must be authoritative
    /// (policy catalog entries, user-added environments).
    /// </summary>
    public static bool IsAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    /// <summary>
    /// True when <paramref name="path"/> is a UNC path (<c>\\server\share\...</c>) reachable from
    /// another machine — as opposed to a local drive letter. Deliberately excludes the
    /// device-namespace forms <c>\\?\</c> and <c>\\.\</c>, which are local-only despite the leading
    /// double backslash. Drives the picker's "this computer only" badge; reused unchanged by M4b.
    /// </summary>
    public static bool IsShareable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        return !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
               !path.StartsWith(@"\\.\", StringComparison.Ordinal);
    }
}

/// <summary>
/// Reads the policy environment catalog from a registry-only <see cref="IConfiguration"/> — pure,
/// no I/O of its own. The caller is responsible for building that configuration from
/// <c>AddRegistryOverrides</c> alone (never merged with appsettings); see the "registry-only"
/// constraint in M3b's spec, enforced by never calling this with the merged configuration.
/// </summary>
public static class EnvironmentCatalogReader
{
    public static EnvironmentCatalog Read(IConfiguration registryLayer)
    {
        var warnings = new List<string>();

        var allowUserEnvironments = ReadAllowUserEnvironments(registryLayer);
        var explicitEntries = ReadExplicitEntries(registryLayer, warnings);
        var implicitDefault = ReadImplicitDefault(registryLayer, warnings);

        var hasExplicitDefault = explicitEntries.Any(
            e => string.Equals(e.Name, "Default", StringComparison.OrdinalIgnoreCase));

        var combined = new List<PolicyEnvironment>(explicitEntries);

        if (implicitDefault != null)
        {
            if (hasExplicitDefault)
            {
                warnings.Add(
                    "Both an implicit \"Default\" environment (from the flat DataPath/Storage/AzureBlob " +
                    "registry values) and an explicit \"Environments\\Default\" entry exist; the explicit " +
                    "entry takes precedence.");
            }
            else
            {
                combined.Add(implicitDefault);
            }
        }

        // Name order is enforced here, not relied upon from registry/IConfiguration enumeration
        // order (neither guarantees it) — this is also what "first entry in name order wins" (the
        // duplicate-DataPath rule below) depends on.
        combined = combined.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var deduped = new List<PolicyEnvironment>();
        var seenPaths = new HashSet<string>();
        foreach (var entry in combined)
        {
            var canonical = EnvironmentPaths.Canonicalize(entry.DataPath);
            if (!seenPaths.Add(canonical))
            {
                warnings.Add(
                    $"Environment \"{entry.Name}\" has the same DataPath as an earlier entry " +
                    "(by name order); it is ignored.");
                continue;
            }
            deduped.Add(entry);
        }

        return new EnvironmentCatalog(deduped, allowUserEnvironments, warnings);
    }

    private static bool ReadAllowUserEnvironments(IConfiguration registryLayer)
    {
        var raw = registryLayer["AllowUserEnvironments"];
        // Absent, or present but unparseable, both default to true (fail-open — M4 is what
        // actually enforces this flag; M3b only reads and displays it).
        return raw == null || !bool.TryParse(raw, out var value) || value;
    }

    private static List<PolicyEnvironment> ReadExplicitEntries(IConfiguration registryLayer, List<string> warnings)
    {
        var result = new List<PolicyEnvironment>();

        foreach (var child in registryLayer.GetSection("Environments").GetChildren())
        {
            var name = child.Key;
            var dataPath = child["DataPath"];

            if (string.IsNullOrWhiteSpace(dataPath))
            {
                warnings.Add($"Environment \"{name}\" has no DataPath and is skipped.");
                continue;
            }

            if (!EnvironmentPaths.IsAbsolute(dataPath))
            {
                warnings.Add($"Environment \"{name}\" has a relative DataPath and is skipped.");
                continue;
            }

            result.Add(new PolicyEnvironment(
                Name: name,
                DataPath: dataPath,
                DisplayName: NullIfEmpty(child["DisplayName"]),
                EnableBlobDistribution: ParseNullableBool(child["Storage:EnableBlobDistribution"], name, warnings),
                DistributionMode: NullIfEmpty(child["Storage:DistributionMode"]),
                LocalDistributionPath: NullIfEmpty(child["Storage:LocalDistributionPath"]),
                AzureBlobContainerName: NullIfEmpty(child["Storage:AzureBlobContainerName"]),
                AzureTenantId: NullIfEmpty(child["AzureBlob:TenantId"]),
                AzureClientId: NullIfEmpty(child["AzureBlob:ClientId"]),
                AzureAccountName: NullIfEmpty(child["AzureBlob:AccountName"]),
                IsImplicitDefault: false));
        }

        return result;
    }

    private static PolicyEnvironment? ReadImplicitDefault(IConfiguration registryLayer, List<string> warnings)
    {
        var dataPath = registryLayer["DataPath"];
        if (string.IsNullOrWhiteSpace(dataPath))
            return null;

        if (!EnvironmentPaths.IsAbsolute(dataPath))
        {
            warnings.Add("Environment \"Default\" has a relative DataPath and is skipped.");
            return null;
        }

        return new PolicyEnvironment(
            Name: "Default",
            DataPath: dataPath,
            DisplayName: null, // no flat root DisplayName value exists in the registry layout
            EnableBlobDistribution: ParseNullableBool(registryLayer["Storage:EnableBlobDistribution"], "Default", warnings),
            DistributionMode: NullIfEmpty(registryLayer["Storage:DistributionMode"]),
            LocalDistributionPath: NullIfEmpty(registryLayer["Storage:LocalDistributionPath"]),
            AzureBlobContainerName: NullIfEmpty(registryLayer["Storage:AzureBlobContainerName"]),
            AzureTenantId: NullIfEmpty(registryLayer["AzureBlob:TenantId"]),
            AzureClientId: NullIfEmpty(registryLayer["AzureBlob:ClientId"]),
            AzureAccountName: NullIfEmpty(registryLayer["AzureBlob:AccountName"]),
            IsImplicitDefault: true);
    }

    private static bool? ParseNullableBool(string? raw, string entryName, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (bool.TryParse(raw, out var value)) return value;

        warnings.Add(
            $"Environment \"{entryName}\" has an unparseable Storage:EnableBlobDistribution value " +
            $"(\"{raw}\"); treated as not set.");
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
