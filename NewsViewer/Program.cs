using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewsCentral.Configuration;
using NewsCentral.Models;
using NewsCentral.Security;
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

        var configuration = BuildConfiguration();
        var config        = configuration.Get<ViewerConfiguration>() ?? new ViewerConfiguration();

        if (string.IsNullOrWhiteSpace(config.Company) ||
            string.IsNullOrWhiteSpace(config.ApplicationName) ||
            string.IsNullOrWhiteSpace(config.CacheRootPath))
        {
            MessageBox.Show(
                "NewsViewer configuration is incomplete.\n" +
                "Company, ApplicationName, and CacheRootPath must be set in appsettings.json.",
                "NewsViewer — Configuration Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var teams = TeamConfigurationReader.GetTeams(configuration);
        if (teams.Length == 0) return;

        if (!HasQualifyingMonitor()) return;
        if (IsRemoteOrVirtualSession()) return;

        var hmac     = new HmacService(config.Hmac);
        var selector = new PresentationSelector(config.CacheRootPath, hmac, config.BypassImageIntegrityCheck);
        var (assignment, imagePath) = selector.SelectActive(teams);
        if (assignment is null || imagePath is null) return;

        var userStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NewsCentral");
        Directory.CreateDirectory(userStatePath);

        var viewerState = new ViewerStateService(userStatePath);
        var telemetry   = new TelemetryWriter(config.CacheRootPath, hmac);
        bool bypass     = config.BypassShowOnceCheck;
        bool alreadyShown = !bypass && viewerState.AlreadyShownToday(assignment.PresentationId);

        if (assignment.ShowMode == Schedule.DisplayMode.ShowNew)
        {
            // Virtual desktop is incompatible with ShowNew: ShowNewApplicationContext creates
            // a hidden timer window before any SwitchToNew() call, causing SetThreadDesktop to fail.
            // ShowNew presentations always show on the current desktop.
            var context = new ShowNewApplicationContext(
                teams, selector, viewerState, telemetry, config.CacheRootPath, bypass);

            if (!alreadyShown)
                context.TryShowViewer();

            Application.Run(context);
            return;
        }

        // ShowOnce: show once per day then exit
        if (alreadyShown) return;

        var isOnline = ReadOnlineStatus(config.CacheRootPath);

        if (assignment.UseVirtualDesktop)
        {
            // Application.EnableVisualStyles() and other WinForms startup calls on the main
            // thread create hidden internal windows (the WinForms parking window, etc.).
            // SetThreadDesktop silently returns false once a thread owns any window handle,
            // so the forms end up on the original desktop instead of the new one.
            // Using a fresh STA thread guarantees no prior window handles exist when
            // SetThreadDesktop is called.
            var a  = assignment;
            var ip = imagePath;
            var t  = telemetry;
            var vs = viewerState;
            var io = isOnline;

            var uiThread = new Thread(() =>
            {
                using var desktop = new VirtualDesktopManager();
                desktop.SwitchToNew();

                var bgForm     = new BackgroundForm(a.VirtualDesktopBackgroundColor);
                bgForm.Show();

                var viewerForm = new ViewerForm(a, ip, t, vs, io);
                viewerForm.FormClosed += (_, _) => { bgForm.Close(); desktop.SwitchToOriginal(); };
                Application.Run(viewerForm);
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();
        }
        else
        {
            Application.Run(new ViewerForm(assignment, imagePath, telemetry, viewerState, isOnline));
        }
    }

    private static IConfiguration BuildConfiguration()
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        // Preliminary pass to read Company/ApplicationName (not registry-overridable).
        var preliminary = new ConfigurationBuilder()
            .AddJsonFile(jsonPath, optional: false)
            .Build();
        var baseConfig = preliminary.Get<ViewerConfiguration>() ?? new ViewerConfiguration();

        return new ConfigurationBuilder()
            .AddJsonFile(jsonPath, optional: false)
            .AddRegistryOverrides(baseConfig.Company, baseConfig.ApplicationName)
            .Build();
    }

    private static bool HasQualifyingMonitor() =>
        Screen.AllScreens.Any(s => s.Bounds.Width >= 1920 && s.Bounds.Height >= 1080);

    // RDP and Citrix ICA both set SM_REMOTESESSION (TerminalServerSession).
    // VMware Horizon sets the ViewClient_Machine_Name environment variable.
    private static bool IsRemoteOrVirtualSession() =>
        SystemInformation.TerminalServerSession ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ViewClient_Machine_Name"));

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
