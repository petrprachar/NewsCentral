using NewsCentral.Configuration;

namespace NewsCentral.Services;

/// <summary>
/// IBlobDistributionService that rebuilds its inner implementation whenever
/// EnvironmentContext.DataPath changes, instead of the inner service being chosen once at DI
/// resolution time (as MauiProgram used to do inline). Selection logic — EnableBlobDistribution,
/// then DistributionMode, then Null/Local/AzureBlob — moved here unchanged from MauiProgram.
///
/// Building the inner service is lazy (first call after construction or after an environment
/// change) and never throws out of DI resolution: a failure (e.g. missing Azure settings) is
/// caught and swapped in for an UnavailableBlobDistributionService instead, whose every method
/// throws InvalidOperationException naming the reason. <see cref="ConfigurationError"/> surfaces
/// that reason for future UI; it is null exactly when the real inner service built successfully.
/// </summary>
public sealed class DistributionServiceRouter : IBlobDistributionService
{
    private const string Tag = "[BlobDist ROUTER]";

    private readonly AppConfiguration _config;
    private readonly EnvironmentContext _environment;

    // volatile: Inner's read-then-maybe-build races only with OnEnvironmentChanged's write (which
    // just discards the reference), never with I/O, so a plain volatile swap is enough — no lock.
    private volatile IBlobDistributionService? _inner;

    public string? ConfigurationError { get; private set; }

    public DistributionServiceRouter(AppConfiguration config, EnvironmentContext environment)
    {
        _config = config;
        _environment = environment;
        _environment.Changed += OnEnvironmentChanged;
    }

    private void OnEnvironmentChanged() => _inner = null;

    private IBlobDistributionService Inner
    {
        get
        {
            var current = _inner;
            if (current != null)
                return current;

            var built = Build();
            _inner = built;
            return built;
        }
    }

    private IBlobDistributionService Build()
    {
        try
        {
            IBlobDistributionService service;

            if (!_config.EnableBlobDistribution)
            {
                service = new NullBlobDistributionService();
            }
            else
            {
                service = _config.DistributionMode switch
                {
                    "AzureBlob" => (IBlobDistributionService)new AzureBlobDistributionService(_config),
                    _ => new LocalBlobDistributionService(ResolveLocalDistributionRoot())
                };
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

    // Mirrors LocalBlobDistributionService's old fallback: LocalDistributionPath if configured,
    // else a "_distribution" subfolder next to the current DataPath.
    private string ResolveLocalDistributionRoot() =>
        string.IsNullOrWhiteSpace(_config.LocalDistributionPath)
            ? Path.Combine(_environment.DataPath, "_distribution")
            : _config.LocalDistributionPath;

    // ── IBlobDistributionService — delegate to whichever instance Inner resolves to ──────────

    public Task UploadTextAsync(string relativePath, string content) =>
        Inner.UploadTextAsync(relativePath, content);

    public Task UploadStreamAsync(string relativePath, Stream content) =>
        Inner.UploadStreamAsync(relativePath, content);

    public Task DeleteAsync(string relativePath) =>
        Inner.DeleteAsync(relativePath);

    public Task<bool> ExistsAsync(string relativePath) =>
        Inner.ExistsAsync(relativePath);

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
