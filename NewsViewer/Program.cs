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
        // Single-instance guard, session-scoped. HKLM Run (logon) and the Workstation Unlock task can
        // double-launch on a fast lock/unlock cycle; two concurrent runs race on the HKCU wallpaper
        // write and on viewerstate.json (whose Write swallows exceptions), so a lost write silently
        // re-fires the poster next unlock. Local\ (not Global\) so fast user switching still gives each
        // session its own poster. createdNew — not WaitOne — avoids AbandonedMutexException if a prior
        // instance crashed holding it.
        using var mutex = new Mutex(initiallyOwned: true, "Local\\NewsCentral.NewsViewer", out bool createdNew);
        if (!createdNew) return;   // another instance is live in this session — exit silently

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var configuration = BuildConfiguration();
        var config        = configuration.Get<ViewerConfiguration>() ?? new ViewerConfiguration();

        // Per-machine master switch (registry Active, DWORD 0/1). Evaluated first, ahead of every
        // other guard: the guards below all mean "there is work but conditions block it", whereas
        // Active = false means "there is no work". Exits silently — no poster, no wallpaper style
        // re-assert, no viewerstate write, no telemetry. The last-applied wallpaper style is
        // deliberately left in place; deactivation is not a revert. (The wallpaper image is
        // NewsService's concern and is unaffected either way.)
        if (!config.Active) return;

        // UI theme — resolved ONCE here and consumed via Theme.Current (the same
        // resolve-once-then-pass pattern as duration resolution). Registry Ui\Theme
        // (REG_SZ "Dark" | "Light", surfaced as Ui:Theme) overrides the code default:
        // "light" (case-insensitive) selects Light; anything else — absent, "Dark",
        // or garbage — is Dark. Must be set before any Form is constructed.
        Theme.Current = string.Equals(configuration["Ui:Theme"], "Light", StringComparison.OrdinalIgnoreCase)
            ? Theme.Light
            : Theme.Dark;

        // Company is a build-time constant (SolutionConstants.Company), enforced at build time — it is
        // not read from config and cannot be empty here. Only CacheRootPath still needs validating.
        if (string.IsNullOrWhiteSpace(config.CacheRootPath))
        {
            MessageBox.Show(
                "NewsViewer configuration is incomplete.\n" +
                "CacheRootPath must be set in appsettings.json.",
                "NewsViewer — Configuration Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var teams = TeamConfigurationReader.GetTeams(configuration);
        if (teams.Length == 0) return;

        // Diagnostic-only, non-fatal: DefaultWallpaperPath moved to the NewsService hive when
        // wallpaper-image ownership migrated there. A value still present in NewsViewer's own
        // hive is orphaned — nothing reads it — and is worth flagging to an operator.
        WarnIfOrphanedWallpaperPathConfigured();

        // Remote/virtual sessions get neither the poster nor the wallpaper style re-assert. The
        // style step otherwise runs on any local interactive session — it is NOT gated behind a
        // qualifying monitor.
        if (IsRemoteOrVirtualSession()) return;

        var selector = new PresentationSelector(
            config.CacheRootPath, configuration, config.BypassImageIntegrityCheck);

        // Terminal step — re-asserted every run, unconditionally, regardless of whether any
        // wallpaper content is active. NewsService owns the wallpaper IMAGE machine-wide via
        // PersonalizationCSP; this only re-applies the per-user HKCU STYLE (Fill/Fit/Stretch/
        // Center/Tile + letterbox background), which CSP does not cover and NewsService's
        // session-0 context cannot reach. See WallpaperService for the verified CSP-enforces-
        // image / HKCU-controls-fit finding.
        void ApplyWallpaperStyle()
        {
            new WallpaperService(config.Delivery.WallpaperStyle, config.Delivery.WallpaperBackgroundColor)
                .ApplyWallpaperStyle();
            System.Diagnostics.Debug.WriteLine("[Wallpaper] style re-asserted");
        }

        // Resolve the active display (poster) assignment — may be absent.
        var (assignment, imagePath) = selector.SelectActive(teams);

        // Poster requires an active assignment AND a Full-HD-or-better monitor. When neither poster
        // can be shown, the wallpaper style is still the terminal step before exit.
        if (assignment is null || imagePath is null || !HasQualifyingMonitor())
        {
            ApplyWallpaperStyle();
            return;
        }

        var userStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NewsCentral");
        Directory.CreateDirectory(userStatePath);

        var hmac        = new HmacService(config.Hmac);        // telemetry only — unchanged
        var viewerState = new ViewerStateService(userStatePath, config.Display.LogicalDayStartHour);
        var telemetry   = new TelemetryWriter(config.CacheRootPath, hmac);
        bool bypass     = config.BypassDailyGate;
        bool alreadyShown = !bypass && viewerState.AlreadyShownToday();

        // Show once per day then exit. The wallpaper style still re-asserts on an already-shown day.
        if (alreadyShown)
        {
            ApplyWallpaperStyle();
            return;
        }

        var isOnline = ReadOnlineStatus(config.CacheRootPath);

        // Resolve the raw duration to an effective value here (not in the view): -1 = never auto-close,
        // 0 / any other negative = unset -> 30s default, >0 = as-is. Single source of truth is
        // PresentationDefaults.
        int effectiveDuration = PresentationDefaults.ResolveDuration(assignment.DisplayDurationSeconds);

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
            var ed = effectiveDuration;

            var uiThread = new Thread(() =>
            {
                using var desktop = new VirtualDesktopManager();
                desktop.SwitchToNew();

                var bgForm     = new BackgroundForm(a.VirtualDesktopBackgroundColor);
                bgForm.Show();

                var viewerForm = new ViewerForm(a, ip, t, vs, io, ed);
                viewerForm.FormClosed += (_, _) => { bgForm.Close(); desktop.SwitchToOriginal(); };
                Application.Run(viewerForm);
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            uiThread.Join();

            // Back on the main thread / original desktop after VD switch-back and teardown.
            ApplyWallpaperStyle();
        }
        else
        {
            Application.Run(new ViewerForm(assignment, imagePath, telemetry, viewerState, isOnline, effectiveDuration));
            ApplyWallpaperStyle();
        }

        // Keep the mutex rooted until the very end. `using var` guarantees disposal at method exit but
        // does NOT keep the object reachable — once the last read is behind us the GC may finalize it
        // and release the mutex while the VD thread and the wallpaper style write are still in flight,
        // which is exactly the window this guard exists to close.
        GC.KeepAlive(mutex);
    }

    private static IConfiguration BuildConfiguration()
    {
        var jsonPath    = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var devJsonPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json");

        // Company is the build-time constant SolutionConstants.Company (defines the hive path, not
        // registry-overridable), so there is nothing to pre-read. Layering, registry always wins:
        //   appsettings.json (required) -> appsettings.Development.json (optional) -> registry.
        // The Development overlay is deliberately NOT gated on ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT
        // (unset for a WinForms process launched from HKLM Run or a scheduled task) — its mere
        // presence activates it. It is gitignored and never published (see NewsViewer.csproj).
        return new ConfigurationBuilder()
            .AddJsonFile(jsonPath, optional: false)
            .AddJsonFile(devJsonPath, optional: true)
            .AddRegistryOverrides(SolutionConstants.Company, SolutionConstants.SolutionName, "NewsViewer")
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

    /// <summary>
    /// Diagnostics only, never fatal: Delivery:DefaultWallpaperPath moved from the NewsViewer
    /// registry hive to the NewsService hive when wallpaper-image ownership migrated there.
    /// ViewerConfiguration no longer has a property for it, so this reads the raw registry value
    /// directly — a value still present here is orphaned (nothing reads it) and worth flagging to
    /// an operator, but is deliberately left untouched: this method never deletes it.
    /// </summary>
    private static void WarnIfOrphanedWallpaperPathConfigured()
    {
        try
        {
            var hive = $@"SOFTWARE\{SolutionConstants.Company}\{SolutionConstants.SolutionName}\NewsViewer\Delivery";
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(hive);
            var orphaned = key?.GetValue("DefaultWallpaperPath") as string;
            if (!string.IsNullOrEmpty(orphaned))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Config] WARNING — orphaned registry value: HKLM\\{hive}\\DefaultWallpaperPath = " +
                    $"'{orphaned}'. NewsViewer no longer reads this key; it moved to the NewsService hive " +
                    $"(HKLM\\SOFTWARE\\{SolutionConstants.Company}\\{SolutionConstants.SolutionName}\\" +
                    "NewsService\\Delivery\\DefaultWallpaperPath). Not removed automatically.");
            }
        }
        catch { /* diagnostics only — never fail startup over this */ }
    }
}
