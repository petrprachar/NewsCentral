using NewsCentral.Security;

namespace NewsViewer.Configuration;

public class ViewerConfiguration
{
    public string Company { get; set; } = "Contoso";
    public string CacheRootPath { get; set; } = @"C:\ProgramData\NewsCentral";
    public bool BypassShowOnceCheck { get; set; } = false;
    public bool BypassImageIntegrityCheck { get; set; } = false;
    public HmacOptions Hmac { get; set; } = new();
    public DeliverySection Delivery { get; set; } = new();
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
