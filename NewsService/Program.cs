using NewsService;
using NewsService.Configuration;
using NewsService.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "NewsService");

// ── Typed configuration ──────────────────────────────────────────────────────
var config = builder.Configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<RegistryConfiguration>();

// ── Repository reader — mode resolved from registry, falls back to appsettings ──
var earlyRegistry    = new RegistryConfiguration(config);
var effectiveMode    = earlyRegistry.GetStorageMode() ?? config.Repository.StorageMode;

if (effectiveMode.Equals("Azure", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton(config.AzureBlob);
    builder.Services.AddSingleton<IRepositoryReader, AzureBlobRepositoryReader>();
}
else
{
    builder.Services.AddSingleton<IRepositoryReader>(sp =>
        new LocalShareRepositoryReader(
            config.Repository.SharePath,
            sp.GetRequiredService<ILogger<LocalShareRepositoryReader>>()));
}

// ── Cache and services ───────────────────────────────────────────────────────
builder.Services.AddSingleton(_ => new CacheManager(
    config.Service.CacheRootPath, JsonDefaults.Options));

builder.Services.AddSingleton<WallpaperService>();
builder.Services.AddSingleton<TelemetryUploader>();
builder.Services.AddSingleton<SyncService>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
