#pragma warning disable CA1416 // Validate platform compatibility

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Configuration;
using NewsCentral.Security;
using NewsCentral.Services;
using System.Reflection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace NewsCentral;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Try multiple methods to load configuration
        IConfiguration? config = null;

        // Method 1: Try embedded resource
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("NewsCentral.appsettings.json");

            if (stream != null)
            {
                config = new ConfigurationBuilder()
                    .AddJsonStream(stream)
                    .Build();
                System.Diagnostics.Debug.WriteLine("✓ Configuration loaded from embedded resource");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load embedded config: {ex.Message}");
        }

        // Method 2: Try file system if embedded failed
        if (config == null)
        {
            try
            {
                var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

                if (File.Exists(appSettingsPath))
                {
                    config = new ConfigurationBuilder()
                        .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
                        .Build();
                    System.Diagnostics.Debug.WriteLine($"✓ Configuration loaded from file: {appSettingsPath}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load file config: {ex.Message}");
            }
        }

        // Method 3: Use hardcoded defaults if all else fails
        if (config == null)
        {
            var inMemorySettings = new Dictionary<string, string>
            {
                {"Company",                           "Contoso"},
                {"DataPath",                          "C:\\Download\\NewsCentral"},
                {"DefaultAdminUsername",              "admin"},
                {"DefaultAdminPassword",              "admin"},
                {"LockExpirationMinutes",             "15"},
                {"Authentication:EnableAutoLogin",    "true"},
                {"Authentication:UseMockUPN",         "true"},
                {"Authentication:MockUPN",            "petr.prachar@company.com"},
                {"Storage:DefaultStorageType",        "NetworkShare"},
                {"Storage:EnableBlobDistribution",    "false"},
                {"Storage:DistributionMode",          "Local"},
                {"Storage:LocalDistributionPath",     "C:\\Download\\NewsCentralDist"},
                {"Storage:AzureBlobContainerName",    "newscentral"},
                {"AzureBlob:TenantId",                ""},
                {"AzureBlob:ClientId",                ""},
                {"AzureBlob:AccountName",             ""}
            };

            config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings!)
                .Build();
            System.Diagnostics.Debug.WriteLine("⚠ Using hardcoded default configuration");
        }

        // Add appsettings as the base layer, then registry overrides on top.
        // builder.Configuration is a ConfigurationManager — it implements both
        // IConfigurationBuilder and IConfigurationRoot, so values added via
        // AddRegistryOverrides are immediately readable from builder.Configuration.
        builder.Configuration.AddConfiguration(config);
        builder.Configuration.AddRegistryOverrides(
            config["Company"] ?? "Contoso",
            "NewsCentral",
            "NewsCentral");

        // DEBUG: read from builder.Configuration (includes registry overrides)
        System.Diagnostics.Debug.WriteLine("=== CONFIGURATION CHECK ===");
        System.Diagnostics.Debug.WriteLine($"DataPath:        '{builder.Configuration["DataPath"]}'");
        System.Diagnostics.Debug.WriteLine($"EnableAutoLogin: '{builder.Configuration["Authentication:EnableAutoLogin"]}'");
        System.Diagnostics.Debug.WriteLine($"UseMockUPN:      '{builder.Configuration["Authentication:UseMockUPN"]}'");
        System.Diagnostics.Debug.WriteLine($"MockUPN:         '{builder.Configuration["Authentication:MockUPN"]}'");
        System.Diagnostics.Debug.WriteLine("=== END CONFIGURATION CHECK ===");

        // Create AppConfiguration from the merged configuration — includes
        // registry overrides on top of appsettings.json defaults.
        // Previously this used the original 'config' object, which only held
        // appsettings.json values and never saw registry overrides.
        var appConfig = new AppConfiguration(builder.Configuration);

        if (string.IsNullOrEmpty(appConfig.DataPath))
            System.Diagnostics.Debug.WriteLine("⚠ WARNING: AppConfig.DataPath is empty!");
        else
            System.Diagnostics.Debug.WriteLine($"✓ AppConfig.DataPath: '{appConfig.DataPath}'");

        builder.Services.AddSingleton(appConfig);
        builder.Services.AddSingleton(
            new HmacService(new HmacOptions { SecretKey = appConfig.HmacSecretKey }));
        builder.Services.AddSingleton<EcdsaSignatureService>();

        // ── Authoring tier storage (always local / Azure Files SMB) ─────────
        builder.Services.AddSingleton<IStorageService, LocalStorageService>();

        // ── Distribution tier storage (config-driven) ────────────────────────
        builder.Services.AddSingleton<IBlobDistributionService>(sp =>
        {
            var cfg = sp.GetRequiredService<AppConfiguration>();

            if (!cfg.EnableBlobDistribution)
            {
                System.Diagnostics.Debug.WriteLine(
                    "IBlobDistributionService: NULL (EnableBlobDistribution=false)");
                return new NullBlobDistributionService();
            }

            return cfg.DistributionMode switch
            {
                "AzureBlob" => (IBlobDistributionService)new AzureBlobDistributionService(cfg),
                _ => new LocalBlobDistributionService(cfg)
            };
        });

        // Register WindowsIdentityService (needs to be before AuthenticationService)
        builder.Services.AddSingleton<WindowsIdentityService>();

        // Register AuthenticationService with dependencies
        builder.Services.AddSingleton<AuthenticationService>(sp =>
        {
            var appConfiguration = sp.GetRequiredService<AppConfiguration>();
            var windowsIdentityService = sp.GetRequiredService<WindowsIdentityService>();

            System.Diagnostics.Debug.WriteLine(
                $"Creating AuthenticationService with DataPath: '{appConfiguration.DataPath}'");

            return new AuthenticationService(appConfiguration, windowsIdentityService);
        });

        // Register other services
        builder.Services.AddSingleton<TeamService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<PosterGenerationService>();
        builder.Services.AddSingleton<IndexGenerationService>();
        builder.Services.AddSingleton<PresentationService>();
        builder.Services.AddSingleton<TeamContextService>();
        builder.Services.AddSingleton<ScheduleService>();
        builder.Services.AddSingleton<AssignmentService>();
        builder.Services.AddSingleton<PublishingService>();

        // Register DataSeederService with IConfiguration dependency
        builder.Services.AddSingleton<DataSeederService>(sp =>
        {
            var appConfiguration = sp.GetRequiredService<AppConfiguration>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var storageService = sp.GetRequiredService<IStorageService>();
            return new DataSeederService(storageService, appConfiguration, configuration);
        });

        // Add localization — ResourcesPath tells the factory where to find per-type .resx files
        builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        // Set culture for testing
        // var culture = new CultureInfo("es");
        // CultureInfo.CurrentCulture = culture;
        // CultureInfo.CurrentUICulture = culture;

        var app = builder.Build();
        return app;
    }
}

#pragma warning restore CA1416