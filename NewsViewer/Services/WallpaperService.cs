using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NewsViewer.Services;

/// <summary>
/// Asserts the per-user desktop wallpaper STYLE only — <b>not</b> the wallpaper image. As of the
/// wallpaper-ownership migration to NewsService, the image itself is enforced machine-wide via
/// PersonalizationCSP (<c>DesktopImagePath</c>/<c>DesktopImageUrl</c>/<c>DesktopImageStatus</c>),
/// which NewsService applies from session 0. CSP enforces WHICH image is shown (Settings greys
/// the picker out), but the HKCU values written here still control HOW that image is fitted
/// (Fill/Fit/Stretch/Center/Tile) — CSP has no equivalent for style. This class exists only
/// because style is per-user and unreachable from NewsService's session-0 context; it re-asserts
/// on every NewsViewer run regardless of whether wallpaper content is active, so the style is
/// always correct for whatever image CSP currently has applied.
///
/// No COM / IDesktopWallpaper — keeps NewsViewer's NativeAOT migration path intact
/// (Microsoft.Win32.Registry only, no extra packages, no unsafe code).
/// </summary>
internal sealed class WallpaperService
{
    private const int COLOR_DESKTOP = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSysColors(int cElements, int[] lpaElements, uint[] lpaRgbValues);

    private readonly string _style;
    private readonly string _backgroundColor;

    /// <param name="style">Fill | Fit | Stretch | Center | Tile (default Fit).</param>
    /// <param name="backgroundColor">"R G B" used for the desktop background (Fit letterbox bars).</param>
    public WallpaperService(string? style, string? backgroundColor)
    {
        _style           = string.IsNullOrWhiteSpace(style) ? "Fit" : style;
        _backgroundColor = string.IsNullOrWhiteSpace(backgroundColor) ? "0 0 0" : backgroundColor;
    }

    /// <summary>
    /// Writes WallpaperStyle/TileWallpaper and a uniform desktop background colour (so Fit
    /// letterbox bars are even) to the current user's HKCU. Does not touch the wallpaper image
    /// itself and does not call SystemParametersInfo — the image is CSP-managed by NewsService.
    /// </summary>
    public void ApplyWallpaperStyle()
    {
        var (wallpaperStyle, tileWallpaper) = MapStyle(_style);
        using (var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
        {
            desktop?.SetValue("WallpaperStyle", wallpaperStyle, RegistryValueKind.String);
            desktop?.SetValue("TileWallpaper",  tileWallpaper,  RegistryValueKind.String);
        }

        var (r, g, b) = ParseColor(_backgroundColor);
        using (var colors = Registry.CurrentUser.CreateSubKey(@"Control Panel\Colors"))
        {
            colors?.SetValue("Background", $"{r} {g} {b}", RegistryValueKind.String);
        }
        // COLORREF = 0x00BBGGRR
        SetSysColors(1, [COLOR_DESKTOP], [(uint)(r | (g << 8) | (b << 16))]);
    }

    /// <summary>Maps a style name to the (WallpaperStyle, TileWallpaper) HKCU value pair.</summary>
    private static (string WallpaperStyle, string TileWallpaper) MapStyle(string style) =>
        style.Trim().ToLowerInvariant() switch
        {
            "fill"    => ("10", "0"),
            "stretch" => ("2",  "0"),
            "center"  => ("0",  "0"),
            "tile"    => ("0",  "1"),
            _         => ("6",  "0"),   // Fit (default)
        };

    /// <summary>Parses "R G B" into a clamped byte triple; defaults to black on malformed input.</summary>
    private static (int R, int G, int B) ParseColor(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            return (Clamp(r), Clamp(g), Clamp(b));
        }
        return (0, 0, 0);

        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
