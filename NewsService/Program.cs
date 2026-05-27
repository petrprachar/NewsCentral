using NewsCentral.Configuration;
using NewsService;
using NewsService.Configuration;
using NewsService.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "NewsService");

// ── Load appsettings, then apply registry overrides ──────────────────────────
// Company/ApplicationName are read first (they define the registry path itself and
// are therefore not registry-overridable).
var baseConfig = builder.Configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
builder.Configuration.AddRegistryOverrides(baseConfig.Company, baseConfig.ApplicationName);

// Re-bind so all typed POCOs reflect registry overrides.
var config = builder.Configuration.Get<ServiceConfiguration>() ?? new ServiceConfiguration();
builder.Services.AddSingleton(config);

// ── Repository reader — mode resolved from merged configuration ───────────────
if (config.Repository.StorageMode.Equals("Azure", StringComparison.OrdinalIgnoreCase))
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
