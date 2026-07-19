using NewsCentral.Configuration;
using NewsCentral.Security;
using NewsService;
using NewsService.Configuration;
using NewsService.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "NewsService");

// ── Load appsettings, then the optional dev overlay, then registry overrides ─
// The overlay is wired explicitly and unconditionally (optional: true — its mere presence
// activates it), NOT via DOTNET_ENVIRONMENT: the host runs as Production, so the built-in
// appsettings.{Environment}.json mechanism would never load it. It is gitignored and excluded
// from publish (CopyToPublishDirectory=Never), so nothing in it can reach the fleet. The
// registry provider must stay LAST — registry always wins over both JSON layers.
// Company defines the registry path itself and is therefore not registry-overridable; it is the
// compile-time constant SolutionConstants.Company. The solution segment is SolutionConstants.SolutionName.
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true);
builder.Configuration.AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsService");

// Bind typed POCOs from the merged (appsettings + registry) configuration.
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

// Optional shared WinHTTP transport for blob + Graph (AzureBlob:UseWinHttpProxy). Default off →
// the factory instantiates nothing and all SDKs are built exactly as before.
builder.Services.AddSingleton<AzureProxyTransportFactory>();

// ── Entra device team resolution (Phase 2 — produces resolved-teams.json) ─────
// GraphServiceClient/credential are built lazily inside EntraDeviceClient, so nothing is
// constructed when Entra is disabled or AzureBlob creds are absent.
builder.Services.AddSingleton<IDeviceIdentityProvider, DeviceIdentityProvider>();
builder.Services.AddSingleton<IEntraDeviceClient, EntraDeviceClient>();
builder.Services.AddSingleton<IEntraGroupClient, EntraGroupClient>();
builder.Services.AddSingleton<EntraTeamResolutionService>();

// ── Signing services ─────────────────────────────────────────────────────────
builder.Services.AddSingleton(new HmacService(config.Hmac));   // telemetry (unchanged)
builder.Services.AddSingleton<EcdsaSignatureService>();         // index.json verification

// ── Cache and services ───────────────────────────────────────────────────────
builder.Services.AddSingleton(_ => new CacheManager(
    config.Service.CacheRootPath, JsonDefaults.Options));

builder.Services.AddSingleton<ILockScreenService, LockScreenService>();
builder.Services.AddSingleton<TelemetryUploader>();
builder.Services.AddSingleton<SyncService>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
