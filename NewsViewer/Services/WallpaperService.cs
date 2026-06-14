using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NewsViewer.Services;

/// <summary>
/// Applies the desktop wallpaper in the current user session via <c>SystemParametersInfo</c> and
/// per-user HKCU registry values — <b>no COM / IDesktopWallpaper</b>, keeping NewsViewer's
/// NativeAOT migration path intact (DllImport P/Invoke with simple blittable types +
/// Microsoft.Win32.Registry, no extra packages, no unsafe code — matching NativeMethods.cs).
///
/// The applier is a thin, stateless shell: it writes the configured style + background colour and
/// asks Windows to set the wallpaper, returning the OS result. Selection and the apply/skip decision
/// live in the caller (re-asserted every run; no per-user state).
/// </summary>
internal sealed class WallpaperService
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE   = 0x01;
    private const uint SPIF_SENDCHANGE      = 0x02;
    private const int  COLOR_DESKTOP        = 1;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

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
    /// Sets the desktop wallpaper to <paramref name="imagePath"/>. Returns true on success, false if
    /// the file is missing or the OS call fails. Writes WallpaperStyle/TileWallpaper and a uniform
    /// desktop background colour (so Fit letterbox bars are even) before applying.
    /// </summary>
    public bool SetWallpaper(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            Debug.WriteLine($"[Wallpaper] image not found: {imagePath}");
            return false;
        }

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

        return SystemParametersInfo(
            SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
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
