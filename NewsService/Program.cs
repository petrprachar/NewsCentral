using NewsCentral.Configuration;
using NewsCentral.Security;
using NewsService;
using NewsService.Configuration;
using NewsService.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "NewsService");

// ── Load appsettings, then apply registry overrides ──────────────────────────
// Company/ApplicationName are read first (they define the registry path itself and
// are therefore not registry-overridable).
var baseConfig = builder.Configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
builder.Configuration.AddRegistryOverrides(baseConfig.Company, "NewsCentral", "NewsService");

// Re-bind so all typed POCOs reflect registry overrides.
var config = builder.Configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
builder.Services.AddSingleton(config);

// ── Repository reader — mode resolved from merged configuration ───────────────
if (config.Repository.StorageMode.Equals("Azure", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IRepositoryReader, AzureBlobRepositoryReader>();
}
else
{
    builder.Services.AddSingleton<IRepositoryReader>(sp =>
        new LocalShareRepositoryReader(
            config.Repository.SharePath,
            sp.GetRequiredService<ILogger<LocalShareRepositoryReader>>()));
}

// AzureBlob section is needed by the blob reader and by the Entra Graph client (which reuses
// the same credential even when StorageMode=Share), so register it unconditionally.
builder.Services.AddSingleton(config.AzureBlob);

// ── Entra device team resolution (Phase 2 — produces resolved-teams.json) ─────
// GraphServiceClient/credential are built lazily inside EntraDeviceClient, so nothing is
// constructed when Entra is disabled or AzureBlob creds are absent.
builder.Services.AddSingleton<IDeviceIdentityProvider, DeviceIdentityProvider>();
builder.Services.AddSingleton<IEntraDeviceClient, EntraDeviceClient>();
builder.Services.AddSingleton<EntraTeamResolutionService>();

// ── Signing services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton(new HmacService(config.Hmac));   // telemetry (unchanged)
builder.Services.AddSingleton<EcdsaSignatureService>();         // index.json verification

// ── Cache and services ───────────────────────────────────────────────────────
builder.Services.AddSingleton(_ => new CacheManager(
    config.Service.CacheRootPath, JsonDefaults.Options));

builder.Services.AddSingleton<WallpaperService>();
builder.Services.AddSingleton<TelemetryUploader>();
builder.Services.AddSingleton<SyncService>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
