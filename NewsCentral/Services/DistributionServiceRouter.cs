using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// IBlobDistributionService that rebuilds its inner implementation whenever the effective
/// per-environment distribution settings change — either EnvironmentContext.DataPath switches
/// (M4) or EnvironmentSettingsService.SettingsChanged fires (a save to config/environment.json),
/// instead of the inner service being chosen once at DI resolution time (as MauiProgram used to
/// do inline, pre-M3a). Selection logic — Distribution.Enabled, then Distribution.Mode, then
/// Null/Local/AzureBlob — now reads EnvironmentSettingsService.GetAsync() instead of
/// AppConfiguration directly.
///
/// Building the inner service is lazy (first call after construction or after a rebuild trigger)
/// and never throws out of DI resolution: a failure — an Invalid environment.json, or a build
/// failure such as missing Azure settings — is caught and swapped in for an
/// UnavailableBlobDistributionService instead, whose every method throws
/// InvalidOperationException naming the reason. <see cref="ConfigurationError"/> surfaces that
/// reason for future UI; it is null exactly when the real inner service built successfully.
/// </summary>
public sealed class DistributionServiceRouter : IBlobDistributionService
{
    private const string Tag = "[BlobDist ROUTER]";

    private readonly EnvironmentSettingsService _settingsService;
    private readonly EnvironmentContext _environment;
    private readonly DistributionStatusTracker _tracker;

    // volatile: GetInnerAsync's read-then-maybe-build races only with OnRebuildTriggered's write
    // (which just discards the reference), never with I/O, so a plain volatile swap is enough —
    // no lock. Concurrent first calls may build twice; that is acceptable.
    private volatile IBlobDistributionService? _inner;

    public string? ConfigurationError { get; private set; }

    public DistributionServiceRouter(
        EnvironmentSettingsService settingsService, EnvironmentContext environment, DistributionStatusTracker tracker)
    {
        _settingsService = settingsService;
        _environment = environment;
        _tracker = tracker;
        _environment.Changed += OnRebuildTriggered;
        _settingsService.SettingsChanged += OnRebuildTriggered;
    }

    private void OnRebuildTriggered() => _inner = null;

    /// <summary>
    /// Forces the inner service to build (if it hasn't already) purely so
    /// <see cref="ConfigurationError"/> reflects the CURRENT settings — the Environment Management
    /// page (M5a) needs to show a configuration problem even when nothing has actually attempted a
    /// distribution call yet this session.
    /// </summary>
    public async Task EnsureBuiltAsync() => await GetInnerAsync();

    private async Task<IBlobDistributionService> GetInnerAsync()
    {
        var current = _inner;
        if (current != null)
            return current;

        var built = await BuildAsync();
        _inner = built;
        return built;
    }

    private async Task<IBlobDistributionService> BuildAsync()
    {
        try
        {
            var effective = await _settingsService.GetAsync();

            if (effective.Source == EnvironmentSettingsSource.Invalid)
            {
                ConfigurationError = $"environment.json is invalid: {effective.Error}";
                System.Diagnostics.Debug.WriteLine(
                    $"{Tag} Build failed — using UnavailableBlobDistributionService: {ConfigurationError}");
                return new UnavailableBlobDistributionService(ConfigurationError);
            }

            // M5a: distinct from Invalid (readable-but-malformed) — the file itself could not be
            // read at all (e.g. an unreachable UNC DataPath). Treated the same way for distribution
            // purposes (fail closed via Unavailable), with its own reason text.
            if (effective.Source == EnvironmentSettingsSource.Unreachable)
            {
                ConfigurationError = $"environment settings could not be read: {effective.Error}";
                System.Diagnostics.Debug.WriteLine(
                    $"{Tag} Build failed — using UnavailableBlobDistributionService: {ConfigurationError}");
                return new UnavailableBlobDistributionService(ConfigurationError);
            }

            var distribution = effective.Settings!.Distribution;
            IBlobDistributionService service;

            if (!distribution.Enabled)
            {
                service = new NullBlobDistributionService();
            }
            else
            {
                service = string.Equals(distribution.Mode, "AzureBlob", StringComparison.OrdinalIgnoreCase)
                    ? new AzureBlobDistributionService(distribution.AzureBlob)
                    : new LocalBlobDistributionService(ResolveLocalDistributionRoot(distribution));
            }

            ConfigurationError = null;
            System.Diagnostics.Debug.WriteLine($"{Tag} Built: {service.GetType().Name}");
            return service;
        }
        catch (Exception ex)
        {
            ConfigurationError = ex.Message;
            System.Diagnostics.Debug.WriteLine(
                $"{Tag} Build failed — using UnavailableBlobDistributionService: {ex.Message}");
            return new UnavailableBlobDistributionService(ex.Message);
        }
    }

    // Mirrors the pre-M3a fallback: LocalPath if configured, else a "_distribution" subfolder
    // next to the current DataPath.
    private string ResolveLocalDistributionRoot(DistributionSettings distribution) =>
        string.IsNullOrWhiteSpace(distribution.LocalPath)
            ? Path.Combine(_environment.DataPath, "_distribution")
            : distribution.LocalPath;

    // ── IBlobDistributionService — each method awaits GetInnerAsync() then delegates ─────────
    // Every delegated call is tracked (M5a) via TrackAsync, EXCEPT when the inner service is
    // NullBlobDistributionService — distribution being deliberately disabled is not a failure, and
    // is never reported as a success either; the tracker simply stays untouched by those calls.

    public async Task UploadTextAsync(string relativePath, string content)
    {
        var inner = await GetInnerAsync();
        await TrackAsync(relativePath, inner, () => inner.UploadTextAsync(relativePath, content));
    }

    public async Task UploadStreamAsync(string relativePath, Stream content)
    {
        var inner = await GetInnerAsync();
        await TrackAsync(relativePath, inner, () => inner.UploadStreamAsync(relativePath, content));
    }

    public async Task DeleteAsync(string relativePath)
    {
        var inner = await GetInnerAsync();
        await TrackAsync(relativePath, inner, () => inner.DeleteAsync(relativePath));
    }

    public async Task<bool> ExistsAsync(string relativePath)
    {
        var inner = await GetInnerAsync();
        return await TrackAsync(relativePath, inner, () => inner.ExistsAsync(relativePath));
    }

    private async Task TrackAsync(string relativePath, IBlobDistributionService inner, Func<Task> action)
    {
        if (inner is NullBlobDistributionService)
        {
            await action();
            return;
        }

        try
        {
            await action();
            _tracker.RecordSuccess();
        }
        catch (Exception ex)
        {
            _tracker.RecordFailure(relativePath, ex.Message);
            throw;
        }
    }

    private async Task<T> TrackAsync<T>(string relativePath, IBlobDistributionService inner, Func<Task<T>> action)
    {
        if (inner is NullBlobDistributionService)
            return await action();

        try
        {
            var result = await action();
            _tracker.RecordSuccess();
            return result;
        }
        catch (Exception ex)
        {
            _tracker.RecordFailure(relativePath, ex.Message);
            throw;
        }
    }

    // ── Fallback when building the real inner service failed ─────────────────────────────────

    private sealed class UnavailableBlobDistributionService : IBlobDistributionService
    {
        private readonly string _reason;

        public UnavailableBlobDistributionService(string reason)
        {
            _reason = reason;
        }

        public Task UploadTextAsync(string relativePath, string content) => throw Error();
        public Task UploadStreamAsync(string relativePath, Stream content) => throw Error();
        public Task DeleteAsync(string relativePath) => throw Error();
        public Task<bool> ExistsAsync(string relativePath) => throw Error();

        private InvalidOperationException Error() =>
            new($"Distribution is not configured: {_reason}");
    }
}
