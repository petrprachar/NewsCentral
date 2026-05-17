#pragma warning disable CA1416 // Validate platform compatibility

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NewsCentral.Configuration;
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
                {"DataPath", "C:\\Download\\NewsCentral"},
                {"DefaultAdminUsername", "admin"},
                {"DefaultAdminPassword", "admin"},
                {"LockExpirationMinutes", "15"},
                {"Authentication:EnableAutoLogin", "true"},
                {"Authentication:UseMockUPN", "true"},
                {"Authentication:MockUPN", "petr.prachar@company.com"},
                {"AI:ClaudeApiKey", ""},
                {"AI:ClaudeApiUrl", "https://api.anthropic.com/v1/messages"},
                {"AI:OpenAIApiKey", ""},
                {"AI:OpenAIApiUrl", "https://api.openai.com/v1/chat/completions"},
                {"Storage:AzureBlobConnectionString", ""},
                {"Storage:DefaultStorageType", "NetworkShare"}
            };

            config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings!)
                .Build();
            System.Diagnostics.Debug.WriteLine("⚠ Using hardcoded default configuration");
        }

        // Add configuration to builder
        builder.Configuration.AddConfiguration(config);

        // DEBUG: Verify configuration values
        System.Diagnostics.Debug.WriteLine("=== CONFIGURATION CHECK ===");
        System.Diagnostics.Debug.WriteLine($"DataPath: '{config["DataPath"]}'");
        System.Diagnostics.Debug.WriteLine($"EnableAutoLogin: '{config["Authentication:EnableAutoLogin"]}'");
        System.Diagnostics.Debug.WriteLine($"UseMockUPN: '{config["Authentication:UseMockUPN"]}'");
        System.Diagnostics.Debug.WriteLine($"MockUPN: '{config["Authentication:MockUPN"]}'");
        System.Diagnostics.Debug.WriteLine("=== END CONFIGURATION CHECK ===");

        // Create and register AppConfiguration
        var dataPath = config["DataPath"];
        if (string.IsNullOrEmpty(dataPath))
        {
            dataPath = "C:\\Download\\NewsCentral";
            System.Diagnostics.Debug.WriteLine($"⚠ DataPath was empty, using fallback: {dataPath}");
        }

        // Create AppConfiguration from IConfiguration
        var appConfig = new AppConfiguration(config);

        // Verify DataPath was loaded
        if (string.IsNullOrEmpty(appConfig.DataPath))
        {
            System.Diagnostics.Debug.WriteLine("⚠ WARNING: AppConfig.DataPath is empty!");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"✓ AppConfig.DataPath set to: '{appConfig.DataPath}'");
        }

        System.Diagnostics.Debug.WriteLine($"AppConfig.DataPath set to: '{appConfig.DataPath}'");

        builder.Services.AddSingleton(appConfig);

        // Register WindowsIdentityService (needs to be before AuthenticationService)
        builder.Services.AddSingleton<WindowsIdentityService>();

        // Register AuthenticationService with dependencies
        builder.Services.AddSingleton<AuthenticationService>(sp =>
        {
            var appConfiguration = sp.GetRequiredService<AppConfiguration>();
            var windowsIdentityService = sp.GetRequiredService<WindowsIdentityService>();

            System.Diagnostics.Debug.WriteLine($"Creating AuthenticationService with DataPath: '{appConfiguration.DataPath}'");

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

        // Add localization
        builder.Services.AddLocalization();

        // Register string localizer
        builder.Services.AddSingleton<IStringLocalizer>(sp =>
        {
            var factory = sp.GetRequiredService<IStringLocalizerFactory>();
            return factory.Create("Resources.Resources", typeof(MauiProgram).Assembly.GetName().Name!);
        });

        // Set culture for testing
        var culture = new CultureInfo("es");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        return builder.Build();
    }
}

#pragma warning restore CA1416