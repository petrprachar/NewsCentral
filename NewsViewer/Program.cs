using System.Text.Json;
using NewsViewer.Configuration;
using NewsViewer.Forms;
using NewsViewer.Services;

namespace NewsViewer;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var config   = LoadConfiguration();
        var registry = new RegistryConfiguration(config);

        var teams = registry.GetTeams();
        if (teams.Length == 0) return;

        if (!HasQualifyingMonitor()) return;

        var selector             = new PresentationSelector(config.CacheRootPath);
        var (assignment, imagePath) = selector.SelectActive(teams);
        if (assignment is null || imagePath is null) return;

        var viewerState = new ViewerStateService(config.CacheRootPath);
        if (!registry.GetBypassShowOnceCheck() && viewerState.AlreadyShownToday(assignment.PresentationId)) return;

        var isOnline = ReadOnlineStatus(config.CacheRootPath);
        var telemetry = new TelemetryWriter(config.CacheRootPath);

        Application.Run(new ViewerForm(assignment, imagePath, telemetry, viewerState, isOnline));
    }

    private static ViewerConfiguration LoadConfiguration()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<ViewerConfiguration>(json, JsonDefaults.Options)
                       ?? new ViewerConfiguration();
            }
        }
        catch { }
        return new ViewerConfiguration();
    }

    private static bool HasQualifyingMonitor() =>
        Screen.AllScreens.Any(s => s.Bounds.Width >= 1920 && s.Bounds.Height >= 1080);

    private static bool ReadOnlineStatus(string cacheRootPath)
    {
        try
        {
            var path = Path.Combine(cacheRootPath, "status.json");
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("isOnline", out var prop) && prop.GetBoolean();
        }
        catch { return false; }
    }
}
