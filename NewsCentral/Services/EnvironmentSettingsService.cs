using System.Text.Json;
using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// Reads, caches and writes the per-environment distribution settings at config/environment.json
/// (M3a). When the file is absent, <see cref="GetAsync"/> resolves to this machine's own
/// configuration (<see cref="EnvironmentSettingsResolver.FromMachineConfiguration"/>) instead —
/// see <see cref="EnvironmentSettingsResolver"/> for the resolution/validation rules themselves,
/// which are pure and live in NewsCentral.Shared.
///
/// M3b: after resolving (unless the result is Invalid), <see cref="GetAsync"/> also overlays a
/// matching Group Policy <see cref="PolicyEnvironment"/> from the injected
/// <see cref="EnvironmentCatalog"/> — read once at startup from a registry-only configuration — via
/// <see cref="EnvironmentSettingsResolver.ApplyPolicy"/>, and computes the fingerprint afterward so
/// it reflects the overlaid settings. <see cref="SaveAsync"/> is unaffected: it always writes
/// exactly what it is given, and the next read overlays policy on top again.
///
/// Caching note: an external edit to config/environment.json (e.g. by hand, or by a second
/// NewsCentral instance) is picked up only after a restart or an <see cref="EnvironmentContext"/>
/// switch — this is not a FileSystemWatcher, and there is deliberately no polling.
/// </summary>
public sealed class EnvironmentSettingsService
{
    private const string RelativePath = "config/environment.json";
    private const string TempRelativePath = "config/environment.json.tmp";

    private readonly IStorageService _storage;
    private readonly EnvironmentContext _environment;
    private readonly AppConfiguration _config;
    private readonly AuthenticationService _authService;
    private readonly EnvironmentCatalog _catalog;

    // volatile, no lock: a concurrent first GetAsync() may resolve from disk twice, same
    // acceptable race as DistributionServiceRouter's inner-service build.
    private volatile EffectiveEnvironmentSettings? _cache;

    public event Action? SettingsChanged;

    public EnvironmentSettingsService(
        IStorageService storage,
        EnvironmentContext environment,
        AppConfiguration config,
        AuthenticationService authService,
        EnvironmentCatalog catalog)
    {
        _storage = storage;
        _environment = environment;
        _config = config;
        _authService = authService;
        _catalog = catalog;
        _environment.Changed += OnEnvironmentChanged;
    }

    private void OnEnvironmentChanged() => _cache = null;

    /// <summary>
    /// Returns the cached effective settings, resolving from disk on first use (or after the
    /// cache was cleared by an environment switch or a save). Never throws — a read failure
    /// (e.g. an unreachable DataPath) resolves to <see cref="EnvironmentSettingsSource.Invalid"/>
    /// carrying the exception message, exactly like a malformed file would.
    /// </summary>
    public async Task<EffectiveEnvironmentSettings> GetAsync()
    {
        var cached = _cache;
        if (cached != null)
            return cached;

        var result = await ResolveFromDiskAsync();
        _cache = result;
        return result;
    }

    private async Task<EffectiveEnvironmentSettings> ResolveFromDiskAsync()
    {
        string? fileJson;
        try
        {
            fileJson = await _storage.ReadTextAsync(RelativePath);
        }
        catch (Exception ex)
        {
            return new EffectiveEnvironmentSettings(
                EnvironmentSettingsSource.Invalid, Settings: null, Error: ex.Message, DistributionFingerprint: null);
        }

        var machineDefaults = EnvironmentSettingsResolver.FromMachineConfiguration(_config);
        var resolved = EnvironmentSettingsResolver.Resolve(fileJson, machineDefaults);

        // Policy (M3b) never rescues a corrupt file — an Invalid result is returned as-is.
        if (resolved.Source == EnvironmentSettingsSource.Invalid)
            return resolved;

        var effectiveSettings = resolved.Settings!;
        string? policyEnvironmentName = null;
        IReadOnlyList<string> policyFields = Array.Empty<string>();

        var policy = _catalog.FindByDataPath(_environment.DataPath);
        if (policy != null)
        {
            (effectiveSettings, policyFields) = EnvironmentSettingsResolver.ApplyPolicy(effectiveSettings, policy);
            policyEnvironmentName = policy.IsImplicitDefault ? $"{policy.Name} (implicit Default)" : policy.Name;
        }

        // Fingerprint is computed AFTER the policy overlay, so it reflects what will actually be
        // used to publish, not the pre-overlay value.
        var fingerprint = EnvironmentSettingsResolver.Fingerprint(effectiveSettings, _environment.DataPath);

        return resolved with
        {
            Settings = effectiveSettings,
            DistributionFingerprint = fingerprint,
            PolicyEnvironmentName = policyEnvironmentName,
            PolicyFields = policyFields
        };
    }

    /// <summary>
    /// Writes <paramref name="settings"/> to config/environment.json. Restricted to a System
    /// Administrator. Validates first, via <see cref="EnvironmentSettingsResolver.Validate"/> —
    /// an invalid settings object is never written. Stamps <see cref="EnvironmentSettings.ModifiedBy"/>
    /// (the current user's UPN, falling back to their username) and
    /// <see cref="EnvironmentSettings.ModifiedUtc"/>. Writes atomically: the new content lands at
    /// config/environment.json.tmp first, then <see cref="IStorageService.MoveFileAsync"/> moves it
    /// over config/environment.json (overwriting), so a reader never sees a half-written file.
    /// </summary>
    public async Task SaveAsync(EnvironmentSettings settings)
    {
        if (!_authService.IsSystemAdmin())
            throw new UnauthorizedAccessException("Only a System Administrator can change environment settings.");

        var errors = EnvironmentSettingsResolver.Validate(settings);
        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid environment settings: {string.Join("; ", errors)}");

        var currentUser = _authService.GetCurrentUser();
        settings.ModifiedBy = !string.IsNullOrEmpty(currentUser?.UPN) ? currentUser.UPN : currentUser?.Username;
        settings.ModifiedUtc = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(settings, EnvironmentSettingsJson.Options);

        await _storage.WriteTextAsync(TempRelativePath, json);
        await _storage.MoveFileAsync(TempRelativePath, RelativePath);

        _cache = null;
        SettingsChanged?.Invoke();
    }

    /// <summary>
    /// For the "Save to environment" banner shown when no environment.json exists yet: persists
    /// this machine's current configuration (Storage:*, AzureBlob:*) as the environment's own
    /// file, so the banner — which only appears under <see cref="EnvironmentSettingsSource.MachineDefaults"/> —
    /// does not reappear.
    /// </summary>
    public Task SaveCurrentDefaultsAsync() =>
        SaveAsync(EnvironmentSettingsResolver.FromMachineConfiguration(_config));

    /// <summary>
    /// Human-readable description of where distribution currently points — the resolved local
    /// root, or "account/container" for AzureBlob, or "distribution disabled". Pure presentation
    /// helper (not used by any validation or build logic); shared by MainLayout's banner and the
    /// Settings page's Environment section so both render the same text for the same settings.
    /// </summary>
    public static string DescribeDistributionTarget(DistributionSettings distribution, string dataPath)
    {
        if (!distribution.Enabled)
            return "distribution disabled";

        if (string.Equals(distribution.Mode, "AzureBlob", StringComparison.OrdinalIgnoreCase))
            return $"{distribution.AzureBlob.AccountName}/{distribution.AzureBlob.ContainerName}";

        return string.IsNullOrWhiteSpace(distribution.LocalPath)
            ? Path.Combine(dataPath, "_distribution")
            : distribution.LocalPath;
    }
}
