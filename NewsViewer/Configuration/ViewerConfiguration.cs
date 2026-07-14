using NewsCentral.Models;
using NewsCentral.Security;

namespace NewsViewer.Configuration;

public class ViewerConfiguration
{
    // Company is not configuration — it defines the registry hive path and is the build-time
    // constant SolutionConstants.Company. It is intentionally absent from this POCO.
    public string CacheRootPath { get; set; } = @"C:\ProgramData\NewsCentral";
    public bool BypassDailyGate { get; set; } = false;
    public bool BypassImageIntegrityCheck { get; set; } = false;
    public HmacOptions Hmac { get; set; } = new();
    public DeliverySection Delivery { get; set; } = new();
    public DisplaySection Display { get; set; } = new();
}

public class DisplaySection
{
    private int _logicalDayStartHour;

    /// <summary>
    /// Hour (0..23, local) at which the once-per-day display gate rolls over. Default 0 = calendar
    /// day. Clamped on read: an out-of-range value degrades to 0 rather than throwing. Must be
    /// provisioned as REG_SZ — RegistryConfigurationProvider coerces REG_DWORD 0/1 to "False"/"True".
    /// </summary>
    public int LogicalDayStartHour
    {
        get => LogicalDayCalculator.NormalizeStartHour(_logicalDayStartHour);
        set => _logicalDayStartHour = value;
    }
}

public class DeliverySection
{
    /// <summary>
    /// Absolute, machine-readable path to a default wallpaper applied when no active IsWallpaper
    /// content is present. Empty = leave the current wallpaper in place (sticky).
    /// </summary>
    public string DefaultWallpaperPath { get; set; } = string.Empty;

    /// <summary>Fill | Fit | Stretch | Center | Tile (default Fit).</summary>
    public string WallpaperStyle { get; set; } = "Fit";

    /// <summary>"R G B" desktop background colour for Fit letterbox bars (default "0 0 0").</summary>
    public string WallpaperBackgroundColor { get; set; } = "0 0 0";
}
