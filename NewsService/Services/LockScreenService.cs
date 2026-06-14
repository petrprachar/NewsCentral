using Microsoft.Win32;

namespace NewsService.Services;

/// <summary>
/// Reads and writes the logon/lock screen image via the PersonalizationCSP registry keys.
/// The live <c>LockScreenImagePath</c> value is the single source of truth — there is no
/// separate state file. SyncService compares the intended path against the current value
/// and applies only on a difference.
///
/// This is a lock-screen-only, SYSTEM-context service: NewsService runs as LocalSystem, and
/// PersonalizationCSP works from session 0 without desktop access. It is the enterprise-grade
/// mechanism used by MDM/Intune.
///
/// Desktop wallpaper is intentionally not handled here — wallpaper application is owned by
/// NewsViewer (a later phase). No wallpaper/IDesktopWallpaper code belongs in this service.
/// </summary>
public interface ILockScreenService
{
    /// <summary>
    /// Writes the three PersonalizationCSP values for <paramref name="imagePath"/>.
    /// Returns <c>true</c> on success, <c>false</c> if the image is missing or the write fails.
    /// A <c>false</c> result must not be treated as applied — the next cycle re-evaluates and
    /// retries naturally because the live registry value still won't match the intended one.
    /// </summary>
    bool SetLockScreen(string imagePath);

    /// <summary>
    /// Returns the current <c>LockScreenImagePath</c> from PersonalizationCSP, or <c>null</c> if
    /// the key/value is absent or unreadable.
    /// </summary>
    string? GetCurrentLockScreenPath();
}

/// <inheritdoc cref="ILockScreenService"/>
public sealed class LockScreenService(ILogger<LockScreenService> logger) : ILockScreenService
{
    private const string CspKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";
    private const string ImagePathValue = "LockScreenImagePath";

    public bool SetLockScreen(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            logger.LogWarning("Lock screen image not found: {Path}", imagePath);
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(CspKey, writable: true);
            if (key is null)
            {
                logger.LogWarning(
                    "Cannot open PersonalizationCSP key — the service may lack write access to HKLM");
                return false;
            }

            key.SetValue(ImagePathValue,            imagePath, RegistryValueKind.String);
            key.SetValue("LockScreenImageUrl",      imagePath, RegistryValueKind.String);
            key.SetValue("LockScreenImageStatus",   1,         RegistryValueKind.DWord);

            // SyncService owns the Information-level "applied" line; keep this at Debug to
            // avoid two info lines per apply.
            logger.LogDebug("Lock screen set via PersonalizationCSP: {Path}", imagePath);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set lock screen");
            return false;
        }
    }

    public string? GetCurrentLockScreenPath()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CspKey, writable: false);
            return key?.GetValue(ImagePathValue) as string;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read current lock screen path from PersonalizationCSP");
            return null;
        }
    }
}
