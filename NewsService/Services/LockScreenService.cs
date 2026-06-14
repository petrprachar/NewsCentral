using Microsoft.Win32;

namespace NewsService.Services;

/// <summary>
/// Applies the logon/lock screen image via the PersonalizationCSP registry keys.
///
/// This is a lock-screen-only, SYSTEM-context service: NewsService runs as
/// LocalSystem, and PersonalizationCSP works from session 0 without desktop
/// access. It is the enterprise-grade mechanism used by MDM/Intune.
///
/// Desktop wallpaper is intentionally not handled here — wallpaper application
/// is owned by NewsViewer (a later phase). No wallpaper/IDesktopWallpaper code
/// belongs in this service.
/// </summary>
public sealed class LockScreenService(ILogger<LockScreenService> logger)
{
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
