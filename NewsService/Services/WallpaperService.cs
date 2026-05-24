using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NewsService.Services;

/// <summary>
/// Applies wallpaper via the IDesktopWallpaper COM interface and sets the
/// logon/lock screen via the PersonalizationCSP registry keys.
///
/// Wallpaper: IDesktopWallpaper requires desktop access. When the service
/// runs as SYSTEM in session 0 it will log a warning and skip. Configure
/// the service to run as the interactive user, or use a Task Scheduler
/// action launched in the user session, to support wallpaper application.
///
/// Lock screen: PersonalizationCSP registry keys work from any session
/// including SYSTEM and are the enterprise-grade mechanism used by MDM/Intune.
/// </summary>
public sealed class WallpaperService(ILogger<WallpaperService> logger)
{
    // ── IDesktopWallpaper COM interface ──────────────────────────────────────
    // CLSID: C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD
    // IID:   B92B56A9-8B55-4E14-9A89-0199BBB6F93B
    // Vtable order must exactly match the Windows SDK definition.

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID,
                          [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID,
                            out RECT displayRect);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();
        void SetSlideshowOptions(uint options, uint slideshowTick);
        void GetSlideshowOptions(out uint options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    [ComImport]
    [Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
    private class DesktopWallpaperClass { }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the desktop wallpaper on all monitors using IDesktopWallpaper COM.
    /// Passing null as monitorID applies to all monitors.
    /// </summary>
    public void SetWallpaper(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            logger.LogWarning("Wallpaper image not found: {Path}", imagePath);
            return;
        }

        try
        {
            var wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
            wallpaper.SetWallpaper(null, imagePath);
            logger.LogInformation("Wallpaper applied: {Path}", imagePath);
        }
        catch (COMException ex)
        {
            logger.LogWarning(ex,
                "SetWallpaper failed (HRESULT 0x{HR:X8}). " +
                "The service may be running in session 0 without desktop access. " +
                "Run as the interactive user or via Task Scheduler in the user session.",
                (uint)ex.HResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error applying wallpaper");
        }
    }

    /// <summary>
    /// Sets the logon/lock screen image via PersonalizationCSP registry keys.
    /// Works from a SYSTEM service — no desktop access required.
    /// Windows applies the image automatically from these keys.
    /// </summary>
    public void SetLockScreen(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            logger.LogWarning("Lock screen image not found: {Path}", imagePath);
            return;
        }

        try
        {
            const string cspKey =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";

            using var key = Registry.LocalMachine.CreateSubKey(cspKey, writable: true);
            if (key is null)
            {
                logger.LogWarning(
                    "Cannot open PersonalizationCSP key — the service may lack write access to HKLM");
                return;
            }

            key.SetValue("LockScreenImagePath",   imagePath, RegistryValueKind.String);
            key.SetValue("LockScreenImageUrl",     imagePath, RegistryValueKind.String);
            key.SetValue("LockScreenImageStatus",  1,         RegistryValueKind.DWord);

            logger.LogInformation("Lock screen set via PersonalizationCSP: {Path}", imagePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set lock screen");
        }
    }
}
