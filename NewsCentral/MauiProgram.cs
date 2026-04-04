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
            }
        }
        catch { }

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
                }
            }
            catch { }
        }

        // Method 3: Use hardcoded defaults if all else fails
        if (config == null)
        {
            var inMemorySettings = new Dictionary<string, string>
            {
                {"AppSettings:DataPath", "C:\\Download\\NewsCentral"},
                {"AppSettings:DefaultAdminUsername", "admin"},
                {"AppSettings:DefaultAdminPassword", "admin"},
                {"AppSettings:LockExpirationMinutes", "15"},
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
        }

        // DEBUG: Check which method worked
        System.Diagnostics.Debug.WriteLine($"DataPath: {config["AppSettings:DataPath"]}");
        System.Diagnostics.Debug.WriteLine("Configuration loaded successfully!");

        builder.Configuration.AddConfiguration(config);

        // Register services
        builder.Services.AddSingleton<AppConfiguration>();
        builder.Services.AddSingleton<AuthenticationService>();
        builder.Services.AddSingleton<TeamService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<PosterGenerationService>();
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

        // TEMPORARY: Force German for testing
        var culture = new System.Globalization.CultureInfo("es");
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;

        return builder.Build();
    }
}

#pragma warning restore CA1416